using System.Diagnostics;
using Microsoft.Win32;

namespace DynamicIsland;

internal sealed partial class IslandWindow : Form
{
    private const float CanvasInset = 46;
    private const string RegistryPath = @"Software\DynamicIsland";
    private enum Command { None, Lock, Previous, Play, Next, Web, File, Timer, Close, Settings }
    private enum Capsule { Mini, Track, Player }

    private readonly BrowserWindow browser = new();
    private readonly StudioWindow studio;
    private readonly Media media = new();
    private readonly GifArtwork gif = new();
    private readonly FrameSurface surface = new();
    private readonly ContextMenuStrip menu = new();
    private Appearance appearance = Presets.Read(Presets.CurrentPath) ?? new();
    private MediaState current = new();
    private FrameClock? frameClock;
    private IntPtr refreshMonitor;
    private Rectangle frameBounds;
    private Point pressStart;
    private Command pressed;
    private Capsule state, lastState;
    private bool locked, held, physicalPress, movedEnough, dragging, expanded, timerFinished;
    private float scale = 1;
    private float x, y, targetX, targetY, anchorX, anchorY, grabX, grabY;
    private float width = 96, height = 36, blend = 1;
    private float stretchX, stretchY, stretchCross, trailX, trailY, grip;
    private string battery = "";
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastFrame, nextStatus, deadline, showTrackUntil, saveAppearanceAt;

    public IslandWindow()
    {
        Text = "Dynamic Island";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Size = new(130, 34);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        studio = new StudioWindow(() => appearance, ApplyAppearance);
        LoadFonts();
        UpdateLayouts();
        gif.Load(appearance.Gif);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            const int toolWindow = 0x80, layered = 0x80000, noActivate = 0x8000000;
            parameters.ExStyle |= toolWindow | layered | noActivate;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        scale = Math.Max(96u, NativeMethods.GetDpiForWindow(Handle)) / 96f;
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        locked = key?.GetValue("Locked") is int flag && flag != 0;
        if (key?.GetValue("X") is int savedX && key.GetValue("Y") is int savedY)
            Place(savedX, savedY);
        else
            ResetPosition();

        x = anchorX = targetX;
        y = anchorY = targetY;
        RenderFrame();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        refreshMonitor = NativeMethods.MonitorFromWindow(Handle, 2);
        frameClock = new FrameClock(Handle, NativeMethods.RefreshRate(Screen.FromHandle(Handle).DeviceName));
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case NativeMethods.FrameMessage:
                frameClock?.AcknowledgeFrame();
                RenderFrame();
                return;
            case 0x21: // WM_MOUSEACTIVATE
                message.Result = 3; // MA_NOACTIVATE
                return;
            case 0x2E0: // WM_DPICHANGED
                scale = (message.WParam.ToInt64() & 0xffff) / 96f;
                if (!dragging)
                    Place(targetX, targetY);
                return;
            case 0x7E: // WM_DISPLAYCHANGE
            case 0x1A: // WM_SETTINGCHANGE
                refreshMonitor = IntPtr.Zero;
                Place(targetX, targetY);
                break;
        }
        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        pressStart = Cursor.Position;
        held = true;
        physicalPress = LeftButtonDown;
        movedEnough = false;
        pressed = HitTest(e.Location);
        grabX = pressStart.X - x;
        grabY = pressStart.Y - y;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (held)
            TrackPointer();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
        }
        else if (e.Button == MouseButtons.Left && held)
        {
            Command command = HitTest(e.Location);
            bool click = !movedEnough && !dragging && command == pressed;
            FinishPointer();
            if (click)
                ActivateCommand(command);
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (held && !physicalPress && !Capture)
            FinishPointer();
    }

    private static bool LeftButtonDown => (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0;

    private void TrackPointer()
    {
        Point pointer = Cursor.Position;
        movedEnough |= Math.Abs(pointer.X - pressStart.X) + Math.Abs(pointer.Y - pressStart.Y) >= 5;
        if (movedEnough && !locked && pressed == Command.None)
        {
            dragging = true;
            Place(pointer.X - grabX, pointer.Y - grabY);
        }
    }

    private void FinishPointer()
    {
        bool moved = dragging;
        held = dragging = physicalPress = false;
        if (Capture)
            Capture = false;
        if (moved)
        {
            Place(targetX, targetY, snap: true);
            RememberPosition();
        }
        showTrackUntil = clock.Elapsed.TotalSeconds + appearance.IdleSeconds;
    }

    private void Place(float center, float topEdge, bool snap = false)
    {
        Rectangle area = Screen.FromPoint(new((int)MathF.Round(center), (int)MathF.Round(topEdge + 12 * scale))).WorkingArea;
        float half = (expanded ? appearance.PlayerWidth : appearance.TrackWidth) * scale / 2;
        float desiredHeight = (expanded ? StateHeight(Capsule.Player) : 58) * scale;
        float left = Math.Min(area.Left + half, (area.Left + area.Right) / 2f);
        float right = Math.Max(area.Right - half, left);
        float top = area.Top + 6;
        float bottom = Math.Max(area.Bottom - desiredHeight - 6, top);
        targetX = Math.Clamp(center, left, right);
        targetY = Math.Clamp(topEdge, top, bottom);
        if (snap && targetY - top < 22 * scale)
            targetY = top;
    }

    private void ResetPosition()
    {
        Rectangle area = Screen.FromPoint(Point.Empty).WorkingArea;
        Place((area.Left + area.Right) / 2f, area.Top + 8);
    }

    private void RememberPosition()
    {
        anchorX = targetX;
        anchorY = targetY;
        SavePosition();
    }

    private void SavePosition()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("X", (int)MathF.Round(anchorX), RegistryValueKind.DWord);
            key.SetValue("Y", (int)MathF.Round(anchorY), RegistryValueKind.DWord);
            key.SetValue("Locked", locked ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    public void OpenAudio(string path) => media.Open(path);

    private void ChooseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.wma;*.opus|All files|*.*",
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenAudio(dialog.FileName);
    }

    private void SetExpanded(bool value)
    {
        expanded = value;
        if (!expanded)
            browser.Hide();
        showTrackUntil = clock.Elapsed.TotalSeconds + appearance.IdleSeconds;
        // Keep the saved position as the anchor while the expanded view fits on screen.
        Place(anchorX, anchorY);
    }

    private Rectangle BrowserAnchor => Rectangle.Inflate(frameBounds,
        -(int)MathF.Round(CanvasInset * scale), -(int)MathF.Round(CanvasInset * scale));

    private void ActivateCommand(Command command)
    {
        switch (command)
        {
            case Command.None:
                if (expanded || appearance.Click)
                    SetExpanded(!expanded);
                else
                    showTrackUntil = clock.Elapsed.TotalSeconds + Math.Max(4, appearance.IdleSeconds);
                break;
            case Command.Close: SetExpanded(false); break;
            case Command.Settings: studio.Open(this); break;
            case Command.Lock: locked = !locked; SavePosition(); break;
            case Command.Previous: if (current.Previous) media.Send(Media.Command.Previous); break;
            case Command.Play: if (current.Toggle) media.Send(Media.Command.Toggle); break;
            case Command.Next: if (current.Next) media.Send(Media.Command.Next); break;
            case Command.Web:
                if (browser.Visible)
                    browser.Hide();
                else
                    browser.Open(this, BrowserAnchor, scale);
                break;
            case Command.File: ChooseFile(); break;
            case Command.Timer:
                double now = clock.Elapsed.TotalSeconds;
                deadline = deadline > now ? 0 : now + 5 * 60;
                timerFinished = false;
                break;
        }
    }

    private void ShowMenu()
    {
        for (int i = menu.Items.Count - 1; i >= 0; i--)
            menu.Items[i].Dispose();

        void Add(string text, Action action) => menu.Items.Add(text, null, (_, _) => action());
        void OpenBrowser(string? url = null)
        {
            SetExpanded(true);
            browser.Open(this, BrowserAnchor, scale, url);
        }

        menu.Items.Add(new ToolStripMenuItem("Lock position", null, (_, _) => ActivateCommand(Command.Lock)) { Checked = locked });
        Add("Reset position", () => { ResetPosition(); RememberPosition(); });
        Add("Open browser", () => OpenBrowser());
        Add("Open music file...", ChooseFile);
        Add("Open Spotify Web", () => OpenBrowser("https://open.spotify.com/"));
        Add("Use Windows media session", () => media.Send(Media.Command.ReleaseLocal));
        Add("Customize island...", () => studio.Open(this));
        Add("Show player", () => SetExpanded(true));

        var presets = Presets.Named();
        if (presets.Length > 0)
        {
            var list = new ToolStripMenuItem("Presets");
            foreach (string path in presets.Take(30))
                list.DropDownItems.Add(Path.GetFileNameWithoutExtension(path).Replace("&", "&&"), null, (_, _) =>
                {
                    if (Presets.Read(path) is { } preset)
                    {
                        ApplyAppearance(preset);
                        studio.RefreshAppearance();
                    }
                });
            menu.Items.Add(list);
        }

        menu.Items.Add(new ToolStripSeparator());
        Add("Quit Dynamic Island", Close);
        menu.Show(this, PointToClient(Cursor.Position));
    }

    private void ApplyAppearance(Appearance value)
    {
        appearance = value;
        UpdateLayouts();
        gif.Load(appearance.Gif);
        double now = clock.Elapsed.TotalSeconds;
        saveAppearanceAt = now + .28;
        showTrackUntil = now + appearance.IdleSeconds;
        Place(anchorX, anchorY);
    }

    private void UpdateStatus(double now)
    {
        var next = media.State;
        if (appearance.TrackWake && next.Active && (!current.Active || next.Title != current.Title || next.Artist != current.Artist))
            showTrackUntil = now + appearance.IdleSeconds;
        current = next;
        UpdateCover();

        var power = SystemInformation.PowerStatus;
        float percent = power.BatteryLifePercent;
        battery = (power.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && percent is >= 0 and <= 1
            ? $"{MathF.Round(percent * 100)}%" + (power.PowerLineStatus == PowerLineStatus.Online ? "  ·  Plugged in" : "")
            : "";
    }

    private string TimerText(double now)
    {
        if (timerFinished)
            return "Timer done";
        if (deadline <= now)
            return "5 min timer";
        int remaining = (int)Math.Ceiling(deadline - now);
        return $"{remaining / 60}:{remaining % 60:00}";
    }

    private void RenderFrame()
    {
        // Capture can be lost when the pointer outruns the non-activating window.
        if (held && physicalPress)
        {
            if (LeftButtonDown)
                TrackPointer();
            else if (!Capture)
                FinishPointer();
        }

        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - lastFrame, .001, .08);
        lastFrame = now;
        if (saveAppearanceAt > 0 && now >= saveAppearanceAt)
        {
            saveAppearanceAt = 0;
            Presets.Write(Presets.CurrentPath, appearance);
        }
        if (now >= nextStatus)
        {
            UpdateStatus(now);
            nextStatus = now + .5;
        }
        if (deadline > 0 && now >= deadline)
        {
            deadline = 0;
            timerFinished = true;
            NativeMethods.MessageBeep(0);
        }

        Point pointer = Cursor.Position;
        if (appearance.Hover && !held && current.Active && BrowserAnchor.Contains(pointer))
            showTrackUntil = now + appearance.IdleSeconds;
        bool showTimer = deadline > now || timerFinished;
        bool showTrack = current.Active ? appearance.IdleSeconds == 0 || now < showTrackUntil : showTimer;
        var target = expanded ? Capsule.Player : showTrack ? Capsule.Track : Capsule.Mini;
        if (target != state && (!held || expanded))
        {
            lastState = state;
            state = target;
            blend = 0;
        }

        bool animateGif = appearance.Icon == 2 && (state == Capsule.Mini ? appearance.GifMini : state == Capsule.Track && appearance.GifTrack);
        gif.Tick(dt, animateGif, appearance.GifSpeed);
        Animate(dt);

        Size size = new((int)MathF.Ceiling((width + 2 * CanvasInset) * scale),
            (int)MathF.Ceiling((height + 2 * CanvasInset) * scale));
        Rectangle updated = new(new Point((int)MathF.Round(x - size.Width / 2f),
            (int)MathF.Round(y - CanvasInset * scale)), size);
        bool moved = updated != frameBounds;
        frameBounds = updated;
        if (surface.Resize(size) is not { } bitmap)
            return;

        PaintFrame(bitmap, now, pointer);
        surface.Present(Handle, updated);
        if (moved && browser.Visible)
            browser.Follow(BrowserAnchor, scale);
        IntPtr monitor = NativeMethods.MonitorFromWindow(Handle, 2);
        if (frameClock is not null && monitor != refreshMonitor)
        {
            refreshMonitor = monitor;
            frameClock.RefreshRate = NativeMethods.RefreshRate(Screen.FromHandle(Handle).DeviceName);
        }
    }

    private void Animate(float dt)
    {
        float Ease(float from, float to, float speed, float tolerance = .06f) =>
            MathF.Abs(from - to) < tolerance ? to : to + (from - to) * MathF.Exp(-speed * dt);

        float speed = appearance.Speed / 100f;
        width = Ease(width, StateWidth(state), 15 * speed);
        height = Ease(height, StateHeight(state), 15 * speed);
        blend = Math.Min(1, blend + dt * 5 * speed);

        float previousX = x, previousY = y;
        x = Ease(x, targetX, dragging ? 31 : 19);
        y = Ease(y, targetY, dragging ? 31 : 19);
        float dx = x - previousX, dy = y - previousY;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        bool grabbed = held && !locked && pressed == Command.None;
        float amount = appearance.Drag / 135f;
        float strength = dragging ? Math.Min(distance / (scale * dt * 680), 1) : 0;
        float ux = distance > .001f ? dx / distance : 0;
        float uy = distance > .001f ? dy / distance : 0;
        float Morph(float from, float to) => Ease(from, to, grabbed ? 35 : 21, .0002f);

        grip = Morph(grip, grabbed ? Math.Min(1, amount) : 0);
        float squeeze = grip * (1 - .7f * strength);
        float along = Math.Min(.2f, 62 / width);
        float direction = strength * (along + .09f) * amount;
        stretchX = Morph(stretchX, squeeze * .06f + direction * ux * ux - strength * .09f * amount);
        stretchY = Morph(stretchY, -squeeze * .16f + direction * uy * uy - strength * .09f * amount);
        stretchCross = Morph(stretchCross, direction * ux * uy);
        trailX = Morph(trailX, -ux * 10 * strength * amount);
        trailY = Morph(trailY, -uy * 7 * strength * amount);

        float extent = Math.Max(MathF.Abs(stretchX) * width / 2 + MathF.Abs(stretchCross) * height / 2 + MathF.Abs(trailX),
            MathF.Abs(stretchY) * height / 2 + MathF.Abs(stretchCross) * width / 2 + MathF.Abs(trailY));
        if (extent > CanvasInset - 2)
        {
            float limit = (CanvasInset - 2) / extent;
            stretchX *= limit;
            stretchY *= limit;
            stretchCross *= limit;
            trailX *= limit;
            trailY *= limit;
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        frameClock?.Dispose();
        frameClock = null;
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            frameClock?.Dispose();
            frameClock = null;
            if (saveAppearanceAt > 0)
                Presets.Write(Presets.CurrentPath, appearance);
            menu.Dispose();
            browser.Dispose();
            studio.Dispose();
            media.Dispose();
            gif.Dispose();
            surface.Dispose();
            DisposeArtwork();
        }
        base.Dispose(disposing);
    }
}
