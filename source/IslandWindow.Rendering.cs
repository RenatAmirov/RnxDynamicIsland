using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DynamicIsland;

internal sealed partial class IslandWindow
{
    private readonly record struct Slot(char Id, RectangleF Bounds);
    private readonly record struct Hit(Command Command, RectangleF Bounds);
    private readonly Slot[][] layouts = new Slot[3][];
    private readonly List<Hit> hits = new(18);
    private readonly PrivateFontCollection fonts = new();
    private readonly Dictionary<(float Size, bool Bold), Font> labels = new();
    private FontFamily? inter;
    private float playerHeight;
    private byte[]? coverBytes;
    private Bitmap? cover;
    private int coverColor;

    private void LoadFonts()
    {
        string path = Path.Combine(Presets.AssetsFolder, "Inter.ttf");
        if (!File.Exists(path))
            return;
        try
        {
            fonts.AddFontFile(path);
            inter = fonts.Families.FirstOrDefault();
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or FileNotFoundException) { }
    }

    private void ClearLabels()
    {
        foreach (var font in labels.Values)
            font.Dispose();
        labels.Clear();
    }

    private void DisposeArtwork()
    {
        cover?.Dispose();
        ClearLabels();
        inter?.Dispose();
        fonts.Dispose();
    }

    private void Label(Graphics g, string text, RectangleF area, float size, Color color, bool bold = false, bool centered = false)
    {
        if (!labels.TryGetValue((size, bold), out var font))
        {
            float pixels = size * appearance.FontSize / 100f;
            FontStyle style = bold ? FontStyle.Bold : FontStyle.Regular;
            font = appearance.Font == 0 && inter?.IsStyleAvailable(style) == true
                ? new Font(inter, pixels, style, GraphicsUnit.Pixel)
                : new Font(appearance.Font == 2 ? "Consolas" : "Segoe UI", pixels, style, GraphicsUnit.Pixel);
            labels.Add((size, bold), font);
        }

        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = centered ? StringAlignment.Center : StringAlignment.Near
        };
        using var ink = new SolidBrush(color);
        g.DrawString(text, font, ink, area, format);
    }

    private void UpdateLayouts()
    {
        ClearLabels();
        layouts[(int)Capsule.Mini] = CompactLayout(Capsule.Mini);
        layouts[(int)Capsule.Track] = CompactLayout(Capsule.Track);
        layouts[(int)Capsule.Player] = PlayerLayout();
        playerHeight = layouts[(int)Capsule.Player].Max(slot => slot.Bounds.Bottom) + appearance.Padding;
    }

    private float StateWidth(Capsule capsule) => capsule switch
    {
        Capsule.Mini => appearance.MiniWidth,
        Capsule.Track => appearance.TrackWidth,
        _ => appearance.PlayerWidth
    };

    private float StateHeight(Capsule capsule) => capsule switch
    {
        Capsule.Mini => 36,
        Capsule.Track => 45,
        _ => playerHeight
    };

    private Slot[] CompactLayout(Capsule capsule)
    {
        string order = appearance.Blocks[(int)capsule];
        float pad = Math.Min(13, appearance.Padding * .8f);
        float available = StateWidth(capsule) - 2 * pad;
        float gap = Math.Min(6, available / 36);
        float BlockWidth(char id) => id switch
        {
            'A' => capsule == Capsule.Mini ? 24 : 28,
            'T' => capsule == Capsule.Mini ? 42 : 122,
            'S' => 74,
            'M' => 23,
            'P' => 69,
            'C' => 48,
            _ => 28
        };

        float needed = gap * (order.Length - 1) + order.Sum(BlockWidth);
        float ratio = Math.Min(1, available / needed);
        float spare = Math.Max(0, available - needed);
        float left = pad + (order.Contains('T') ? 0 : spare / 2);
        var slots = new Slot[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            char id = order[i];
            float blockWidth = BlockWidth(id) * ratio + (id == 'T' ? spare : 0);
            slots[i] = new(id, new(left, 0, blockWidth, StateHeight(capsule)));
            left += blockWidth + gap * ratio;
        }
        return slots;
    }

    private Slot[] PlayerLayout()
    {
        string order = appearance.Blocks[(int)Capsule.Player].Replace("TS", "H", StringComparison.Ordinal);
        float pad = appearance.Padding;
        float available = appearance.PlayerWidth - 2 * pad;
        const float gap = 7;
        float left = pad, top = 45, rowHeight = 0;
        var slots = new Slot[order.Length];
        void NextRow()
        {
            if (rowHeight == 0)
                return;
            left = pad;
            top += rowHeight + gap;
            rowHeight = 0;
        }

        for (int i = 0; i < order.Length; i++)
        {
            char id = order[i];
            float blockHeight = id switch { 'A' or 'T' or 'S' or 'H' => 58, 'P' => 36, _ => 32 };
            float blockWidth = id switch
            {
                'A' => 58,
                'T' or 'S' or 'H' => Math.Min(available - 65, 242),
                'M' => 48,
                'P' => 114,
                _ => Math.Min(122, (available - 2 * gap) / 3)
            };
            if (id == 'P' || left + blockWidth > pad + available)
                NextRow();
            if (id == 'P')
                left = (appearance.PlayerWidth - blockWidth) / 2;
            slots[i] = new(id, new(left, top, blockWidth, blockHeight));
            left += blockWidth + gap;
            rowHeight = Math.Max(rowHeight, blockHeight);
            if (id == 'P')
                NextRow();
        }
        return slots;
    }

    private Command HitTest(Point pixel)
    {
        PointF point = new(pixel.X / scale - CanvasInset, pixel.Y / scale - CanvasInset);
        foreach (var hit in hits)
        {
            if (hit.Bounds.Contains(point))
                return hit.Command;
        }
        return Command.None;
    }

    private void UpdateCover()
    {
        if (ReferenceEquals(current.Cover, coverBytes))
            return;
        cover?.Dispose();
        cover = null;
        coverColor = 0;
        coverBytes = current.Cover;
        if (coverBytes is null)
            return;

        try
        {
            using var stream = new MemoryStream(coverBytes, writable: false);
            using var source = Image.FromStream(stream);
            float side = Math.Min(source.Width, source.Height);
            var sampleArea = new RectangleF((source.Width - side) / 2, (source.Height - side) / 2, side, side);
            using var thumbnail = new Bitmap(120, 120, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(thumbnail))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, new RectangleF(0, 0, 120, 120), sampleArea, GraphicsUnit.Pixel);
            }

            int red = 0, green = 0, blue = 0, count = 0;
            for (int y = 12; y < 120; y += 16)
            {
                for (int x = 12; x < 120; x += 16)
                {
                    Color color = thumbnail.GetPixel(x, y);
                    if (color.A < 160)
                        continue;
                    red += color.R;
                    green += color.G;
                    blue += color.B;
                    count++;
                }
            }
            if (count > 0)
            {
                int Dark(int channel) => 18 + channel * 36 / (count * 100);
                coverColor = (Dark(red) << 16) | (Dark(green) << 8) | Dark(blue);
            }
            cover = thumbnail.Clone(new Rectangle(0, 0, 120, 120), PixelFormat.Format32bppPArgb);
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException) { }
    }

    private void DrawCover(Graphics g, RectangleF bounds, float opacity, Capsule capsule)
    {
        using var path = Rounded(bounds, bounds.Width * .25f);
        var saved = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        Image? icon = capsule != Capsule.Player && appearance.Icon == 2 ? gif.Image : null;
        Image? image = icon ?? (capsule == Capsule.Player || appearance.Icon == 0 ? cover : null);
        if (image is not null)
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float iw = image.Width, ih = image.Height;
            float side = Math.Min(iw, ih);
            RectangleF source = new((iw - side) / 2, (ih - side) / 2, side, side);
            RectangleF dest = bounds;
            if (icon is not null && appearance.GifFit)
            {
                float fit = bounds.Width / Math.Max(iw, ih);
                dest = new(bounds.X + (bounds.Width - iw * fit) / 2, bounds.Y + (bounds.Height - ih * fit) / 2,
                    iw * fit, ih * fit);
                source = new(0, 0, iw, ih);
            }
            g.DrawImage(image, [dest.Location, new(dest.Right, dest.Top), new(dest.Left, dest.Bottom)],
                source, GraphicsUnit.Pixel, attributes);
        }
        else
        {
            using var fill = new LinearGradientBrush(bounds, Faded(opacity, 0x944f3a), Faded(opacity, 0x412824), 55);
            g.FillRectangle(fill, bounds);
            using var ring = new Pen(Faded(opacity, 0xf3b58f), 1.2f);
            g.DrawEllipse(ring, bounds.X + bounds.Width * .22f, bounds.Y + bounds.Height * .22f,
                bounds.Width * .56f, bounds.Height * .56f);
            g.DrawEllipse(ring, bounds.X + bounds.Width * .43f, bounds.Y + bounds.Height * .43f,
                bounds.Width * .14f, bounds.Height * .14f);
        }
        g.Restore(saved);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Pill(Graphics g, RectangleF rect, float radius, Color color)
    {
        using var path = Rounded(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static Color Faded(float alpha, int rgb) => Color.FromArgb((int)(255 * Math.Clamp(alpha, 0, 1)),
        (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    private static void DrawIcon(Graphics g, Command command, float x, float y, Color color, bool playing)
    {
        using var pen = new Pen(color, 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(color);
        switch (command)
        {
            case Command.Play when playing:
                g.FillRectangle(fill, x - 4, y - 5, 2.5f, 10);
                g.FillRectangle(fill, x + 1.5f, y - 5, 2.5f, 10);
                break;
            case Command.Previous:
            case Command.Next:
            case Command.Play:
                float direction = command == Command.Previous ? -1 : 1;
                g.FillPolygon(fill, new PointF[] { new(x - 3 * direction, y - 5), new(x + 5 * direction, y), new(x - 3 * direction, y + 5) });
                if (command != Command.Play)
                    g.DrawLine(pen, x + 6 * direction, y - 4.5f, x + 6 * direction, y + 4.5f);
                break;
            case Command.Web:
                g.DrawEllipse(pen, x - 5.5f, y - 5.5f, 11, 11);
                g.DrawEllipse(pen, x - 2.5f, y - 5.5f, 5, 11);
                g.DrawLine(pen, x - 5, y, x + 5, y);
                break;
            case Command.File:
                g.DrawLine(pen, x - 1, y + 2, x - 1, y - 5);
                g.DrawLine(pen, x - 1, y - 5, x + 4, y - 3.5f);
                g.FillEllipse(fill, x - 5.5f, y + 1, 5, 4);
                break;
            case Command.Timer:
                g.DrawEllipse(pen, x - 5.2f, y - 5.2f, 10.4f, 10.4f);
                g.DrawLine(pen, x, y - 2.8f, x, y);
                g.DrawLine(pen, x, y, x + 2.1f, y + 1);
                break;
        }
    }

    private void RenderBlocks(Graphics g, Capsule capsule, float opacity, double now, bool interactive, PointF mouse)
    {
        if (opacity < .01f)
            return;
        float dx = (width - StateWidth(capsule)) / 2;
        float dy = capsule == Capsule.Player ? 0 : (height - StateHeight(capsule)) / 2;
        Color foreground = Faded(opacity, 0xfcf1e6), muted = Faded(opacity, 0xc9b0a0);

        void Button(Command command, RectangleF bounds, string text, bool active = true)
        {
            if (bounds.Width < 12 || bounds.Height < 10)
                return;
            if (interactive)
                hits.Add(new(command, bounds));
            bool hover = interactive && active && bounds.Contains(mouse);
            bool emphasis = command == Command.Play || (command == Command.Web && browser.Visible) ||
                (command == Command.Lock && locked);
            Pill(g, bounds, Math.Min(10, bounds.Width / 3), Faded(opacity * (hover ? .36f : emphasis ? .28f : .13f), 0xe4a07b));
            var ink = Faded(opacity * (active ? 1 : .35f), 0xfcf1e6);
            if (text.Length == 0 || (bounds.Width < 43 && command is Command.Web or Command.File or Command.Timer))
                DrawIcon(g, command, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, ink, current.Playing);
            else
                Label(g, text, bounds, bounds.Width < 44 ? 9 : 11, ink, bold: true, centered: true);
        }

        if (capsule == Capsule.Player)
        {
            float edge = StateWidth(capsule) + dx - appearance.Padding;
            Label(g, battery.Length == 0 ? "NOW PLAYING" : battery, new(dx + appearance.Padding, 11, 168, 22), 9, muted, bold: true);
            Button(Command.Settings, new(edge - 113, 11, 45, 25), "SET");
            Button(Command.Lock, new(edge - 62, 11, 34, 25), locked ? "PIN" : "FREE");
            Button(Command.Close, new(edge - 23, 11, 23, 25), "⌃");
        }

        string title = current.Title;
        if (title.Length == 0)
            title = deadline > now || timerFinished ? TimerText(now) : current.Active ? "Now playing" : "No media playing";

        foreach (var (id, bounds) in layouts[(int)capsule])
        {
            var rect = bounds;
            rect.Offset(dx, dy);
            float center = rect.Y + rect.Height / 2;
            switch (id)
            {
                case 'A':
                    float side = Math.Min(Math.Min(rect.Width, rect.Height), capsule == Capsule.Player ? 58 : 28);
                    if (side >= 5)
                        DrawCover(g, new(rect.X, center - side / 2, side, side), opacity, capsule);
                    break;
                case 'T': Label(g, title, rect, capsule == Capsule.Player ? 18 : 13, foreground, bold: true); break;
                case 'S': Label(g, current.Artist, rect, 11, muted); break;
                case 'H':
                    Label(g, title, new(rect.X, center - 22, rect.Width, 26), 18, foreground, bold: true);
                    Label(g, current.Artist, new(rect.X, center + 3, rect.Width, 19), 11, muted);
                    break;
                case 'M':
                    for (int i = 0; i < 4; i++)
                    {
                        float bar = current.Playing ? 5 + 11 * (float)Math.Abs(Math.Sin(now * (5 + i) + i)) : 4;
                        Pill(g, new(rect.X + rect.Width / 2 - 9 + i * 5, center - bar / 2, 2.5f, bar), 1.2f, Faded(opacity, 0xe5966d));
                    }
                    break;
                case 'P':
                    float part = rect.Width / 3;
                    Button(Command.Previous, new(rect.X, center - 14, part - 2, 28), "", current.Previous);
                    Button(Command.Play, new(rect.X + part, center - 16, part - 2, 32), "", current.Toggle);
                    Button(Command.Next, new(rect.X + 2 * part, center - 14, part - 2, 28), "", current.Next);
                    break;
                case 'B': Button(Command.Web, new(rect.X, center - 14, rect.Width, 28), browser.Visible ? "Hide browser" : "Browser"); break;
                case 'F': Button(Command.File, new(rect.X, center - 14, rect.Width, 28), "Open music"); break;
                case 'C': Button(Command.Timer, new(rect.X, center - 14, rect.Width, 28), TimerText(now)); break;
            }
        }
    }

    private void PaintFrame(Bitmap bitmap, double now, Point pointer)
    {
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(CanvasInset, CanvasInset);

        float radius = Math.Min(Math.Min(height / 2, width / 2), appearance.Radius);
        using var shape = Rounded(new(.6f, .6f, width - 1.2f, height - 1.2f), radius - .6f);
        var beforeStretch = g.Save();
        float cx = width / 2, cy = height / 2;
        using var stretch = new Matrix(1 + stretchX, stretchCross, stretchCross, 1 + stretchY,
            -cx * stretchX - cy * stretchCross + trailX, -cx * stretchCross - cy * stretchY + trailY);
        g.MultiplyTransform(stretch);
        float angle = .57f + (appearance.Gradient ? .32f * (float)Math.Sin(now * .47) : 0);
        float span = .54f * MathF.Sqrt(width * width + height * height);
        float dx = span * MathF.Cos(angle), dy = span * MathF.Sin(angle);
        float opacity = appearance.Opacity / 100f;
        int first = appearance.ArtPalette && coverColor != 0 ? coverColor : appearance.First;
        using var gradient = new LinearGradientBrush(new PointF(cx - dx, cy - dy), new PointF(cx + dx, cy + dy),
            Faded(opacity, first), Faded(opacity, appearance.Second)) { GammaCorrection = true };
        g.FillPath(gradient, shape);
        if (appearance.Outline)
        {
            using var border = new Pen(Faded(.37f + .35f * grip, 0xe3b398), 1 + grip * .55f);
            g.DrawPath(border, shape);
        }
        g.Restore(beforeStretch);

        g.SetClip(shape);
        PointF mouse = new((pointer.X - frameBounds.Left) / scale - CanvasInset,
            (pointer.Y - frameBounds.Top) / scale - CanvasInset);
        hits.Clear();
        if (lastState != state)
            RenderBlocks(g, lastState, 1 - blend, now, false, mouse);
        RenderBlocks(g, state, blend, now, blend > .58f, mouse);
    }
}
