using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DynamicIsland;

internal sealed class GifArtwork : IDisposable
{
    private byte[]? bytes;
    private MemoryStream? stream;
    private Image? source;
    private Bitmap? preview;
    private float[] delays = [];
    private int index;
    private float elapsed;

    public Image? Image => preview ?? source;

    public void Load(byte[]? artwork)
    {
        if (ReferenceEquals(bytes, artwork))
            return;
        Dispose();
        bytes = artwork;
        if (artwork is null)
            return;

        try
        {
            stream = new MemoryStream(artwork, writable: false);
            source = System.Drawing.Image.FromStream(stream);
            int frames = source.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? source.GetFrameCount(FrameDimension.Time) : 1;
            if (frames is < 1 or > 500 || source.Width is < 1 or > 1024 || source.Height is < 1 or > 1024)
            {
                Dispose();
                return;
            }

            delays = new float[frames];
            Array.Fill(delays, .10f);
            const int frameDelay = 0x5100;
            if (source.PropertyIdList.Contains(frameDelay) && source.GetPropertyItem(frameDelay)?.Value is { } values)
            {
                for (int i = 0; i < Math.Min(frames, values.Length / 4); i++)
                    delays[i] = Math.Clamp(BinaryPrimitives.ReadUInt32LittleEndian(values.AsSpan(i * 4)) / 100f, .02f, 10f);
            }

            int largest = Math.Max(source.Width, source.Height);
            if (largest > 96)
            {
                preview = new Bitmap(Math.Max(1, source.Width * 96 / largest),
                    Math.Max(1, source.Height * 96 / largest), PixelFormat.Format32bppPArgb);
                RenderPreview();
            }
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ can report an invalid image as an out-of-memory error.
            Dispose();
        }
    }

    public void Tick(float delta, bool animate, int speed)
    {
        if (source is null || delays.Length < 2)
            return;

        int next = animate ? index : 0;
        elapsed = animate ? elapsed + delta * speed / 100f : 0;
        while (elapsed >= delays[next])
        {
            elapsed -= delays[next];
            next = (next + 1) % delays.Length;
        }
        if (next == index)
            return;

        try
        {
            source.SelectActiveFrame(FrameDimension.Time, next);
            index = next;
            RenderPreview();
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException)
        {
            Dispose();
        }
    }

    private void RenderPreview()
    {
        if (preview is null || source is null)
            return;
        using var graphics = Graphics.FromImage(preview);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(source, 0, 0, preview.Width, preview.Height);
    }

    public void Dispose()
    {
        preview?.Dispose();
        preview = null;
        source?.Dispose();
        source = null;
        stream?.Dispose();
        stream = null;
        delays = [];
        index = 0;
        elapsed = 0;
    }
}
