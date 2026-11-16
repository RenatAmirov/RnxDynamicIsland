using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace DynamicIsland;

internal sealed class BrowserWindow : Form
{
    private const string ToolbarHost = "island-toolbar.example";
    private const string ToolbarUrl = "https://" + ToolbarHost + "/browser-toolbar.html";
    private const string Home = """
        <!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
        html{color-scheme:dark}body{margin:0;min-height:100vh;display:grid;place-items:center;background:radial-gradient(ellipse at 50% 0%,#392921,#1a1716 72%);color:#f5e5d9;font:14px "Segoe UI",sans-serif;text-align:center}
        p{margin:0;color:#c5a695}h1{margin:12px 0 10px;font:38px Georgia,serif;letter-spacing:-.04em}
        nav{display:flex;gap:8px;justify-content:center;margin-top:25px}a{color:#f1d2be;text-decoration:none;border:1px solid #c7947840;border-radius:10px;padding:8px 13px;background:#c794781a}a:hover{background:#c7947840}
        </style>
        <main><p>I S L A N D &nbsp; B R O W S E R</p><h1>Where to?</h1><p>Search or paste a link in the bar above.</p>
        <nav><a href="https://www.youtube.com/">YouTube</a><a href="https://soundcloud.com/">SoundCloud</a><a href="https://open.spotify.com/">Spotify</a></nav></main>
        """;

    private CoreWebView2Controller? toolbar, page;
    private bool starting, ready, focusAddress;
    private string status = "Opening Island Browser…";
    private string? pending;
    private string address = "";

    public BrowserWindow()
    {
        Text = "Island Browser";
        BackColor = Color.FromArgb(39, 33, 30);
        ForeColor = Color.FromArgb(235, 213, 195);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    public void Open(Form owner, Rectangle anchor, float scale, string? url = null)
    {
        Follow(anchor, scale);
        if (Owner is null)
            Show(owner);
        else
            Show();
        Activate();

        focusAddress = url is null;
        if (url is not null)
            Navigate(url);
        else
            PublishAddress();

        if (page is null && !starting)
            _ = StartAsync();
    }

    public void Follow(Rectangle anchor, float scale)
    {
        Rectangle area = Screen.FromRectangle(anchor).WorkingArea;
        int gap = (int)MathF.Round(8 * scale);
        int width = Math.Min((int)MathF.Round(740 * scale), area.Width);
        int desiredHeight = (int)MathF.Round(500 * scale);
        int below = area.Bottom - anchor.Bottom - gap;
        int above = anchor.Top - area.Top - gap;
        bool useBelow = below >= desiredHeight || below >= above;
        int height = Math.Min(desiredHeight, Math.Max(gap, useBelow ? below : above));
        int x = Math.Clamp((anchor.Left + anchor.Right - width) / 2, area.Left, area.Right - width);
        int y = useBelow ? anchor.Bottom + gap : anchor.Top - height - gap;
        Bounds = new(x, y, width, height);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutViews();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutViews();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        toolbar?.NotifyParentWindowPositionChanged();
        page?.NotifyParentWindowPositionChanged();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (toolbar is not null)
            toolbar.IsVisible = Visible;
        if (page is not null)
            page.IsVisible = Visible;
        if (!Visible)
            focusAddress = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape)
            return base.ProcessCmdKey(ref msg, keyData);
        Hide();
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (page is null)
            TextRenderer.DrawText(e.Graphics, status, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private void LayoutViews()
    {
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        float scale = DeviceDpi / 96f;
        float radius = Math.Min(14 * scale, Math.Min(size.Width, size.Height) / 2f);
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(new RectangleF(0, 0, size.Width, size.Height), new SizeF(radius, radius));
        var oldRegion = Region;
        Region = new Region(shape);
        oldRegion?.Dispose();

        int header = Math.Min((int)MathF.Round(53 * scale), size.Height);
        if (toolbar is not null)
            toolbar.Bounds = new(0, 0, size.Width, header);
        if (page is not null)
            page.Bounds = new(0, header, size.Width, size.Height - header);
    }

    private async Task StartAsync()
    {
        starting = true;
        status = "Opening Island Browser…";
        Invalidate();
        try
        {
            if (!File.Exists(Path.Combine(Presets.AssetsFolder, "browser-toolbar.html")))
            {
                status = "Keep assets/browser-toolbar.html next to DynamicIsland.exe.";
                return;
            }

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(Presets.SettingsFolder, "Browser"));
            if (IsDisposed)
                return;
            toolbar = await CreateViewAsync(environment);
            if (toolbar is null)
                return;
            page = await CreateViewAsync(environment);
            if (page is null)
                return;
            LayoutViews();
            ConfigureContent(page.CoreWebView2);
            WebDocuments.Load(toolbar.CoreWebView2, ToolbarHost, ToolbarUrl, HandleToolbar);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            status = "Microsoft Edge WebView2 Runtime is required for Island Browser.";
            CloseViews();
        }
        catch (Exception error) when (WebDocuments.IsInitializationError(error))
        {
            status = "Could not start Island Browser.";
            CloseViews();
        }
        finally
        {
            starting = false;
            if (!IsDisposed)
                Invalidate();
        }
    }

    private async Task<CoreWebView2Controller?> CreateViewAsync(CoreWebView2Environment environment)
    {
        var controller = await environment.CreateCoreWebView2ControllerAsync(Handle);
        if (IsDisposed)
        {
            controller.Close();
            return null;
        }

        controller.IsVisible = Visible;
        controller.DefaultBackgroundColor = BackColor;
        controller.AcceleratorKeyPressed += (_, args) =>
        {
            if (args.VirtualKey == (uint)Keys.Escape && args.KeyEventKind == CoreWebView2KeyEventKind.KeyDown)
            {
                args.Handled = true;
                BeginInvoke(Hide);
            }
        };
        return controller;
    }

    private void ConfigureContent(CoreWebView2 view)
    {
        view.Settings.IsWebMessageEnabled = false;
        view.Settings.AreHostObjectsAllowed = false;
        view.NavigationStarting += (_, args) => args.Cancel = !IsHttp(args.Uri) &&
            !args.Uri.Equals("about:blank", StringComparison.Ordinal) &&
            !args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
        view.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            Navigate(args.Uri);
        };
        view.SourceChanged += (_, _) =>
        {
            address = IsHttp(view.Source) ? view.Source : "";
            PublishAddress();
        };
        view.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;

        if (pending is { } url)
        {
            pending = null;
            Navigate(url);
        }
        else
        {
            view.NavigateToString(Home);
        }
    }

    private static bool IsHttp(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme is "http" or "https") && uri.Host.Length > 0;

    private void Navigate(string url)
    {
        if (!IsHttp(url))
            return;
        if (page is null)
            pending = url;
        else
            page.CoreWebView2.Navigate(url);
    }

    private void PublishAddress()
    {
        if (!ready || toolbar is null)
            return;
        toolbar.CoreWebView2.PostWebMessageAsString("address:" + address);
        if (Visible && focusAddress)
        {
            focusAddress = false;
            toolbar.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            toolbar.CoreWebView2.PostWebMessageAsString("focus");
        }
    }

    private void HandleToolbar(string command)
    {
        switch (command)
        {
            case "ready": ready = true; PublishAddress(); return;
            case "close": Hide(); return;
            case "back": if (page?.CoreWebView2.CanGoBack == true) page.CoreWebView2.GoBack(); return;
            case "reload": page?.CoreWebView2.Reload(); return;
        }

        if (!command.StartsWith("navigate:", StringComparison.Ordinal) || command.Length is <= 9 or > 4105)
            return;

        string input = command[9..];
        if (IsHttp(input))
        {
            Navigate(input);
            return;
        }

        int end = input.IndexOfAny(['/', '?', '#']);
        string domain = end < 0 ? input : input[..end];
        string addressCandidate = "https://" + input;
        if (domain.Contains('.') && domain.IndexOfAny(['@', ':', '\\']) < 0 &&
            !input.Any(char.IsWhiteSpace) && IsHttp(addressCandidate))
            Navigate(addressCandidate);
        else
            Navigate("https://www.google.com/search?q=" + Uri.EscapeDataString(input));
    }

    private void CloseViews()
    {
        page?.Close();
        toolbar?.Close();
        page = toolbar = null;
        ready = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseViews();
        base.Dispose(disposing);
    }
}

internal static class WebDocuments
{
    public static void Load(CoreWebView2 view, string host, string url, Action<string> handleMessage)
    {
        view.Settings.AreDefaultContextMenusEnabled = false;
        view.Settings.AreDevToolsEnabled = false;
        view.Settings.IsZoomControlEnabled = false;
        view.Settings.AreHostObjectsAllowed = false;
        view.SetVirtualHostNameToFolderMapping(host, Presets.AssetsFolder, CoreWebView2HostResourceAccessKind.Deny);
        view.NavigationStarting += (_, args) => args.Cancel = !string.Equals(args.Uri, url, StringComparison.Ordinal);
        view.NewWindowRequested += (_, args) => args.Handled = true;
        view.WebMessageReceived += (_, args) =>
        {
            if (!string.Equals(args.Source, url, StringComparison.Ordinal))
                return;
            string message;
            try { message = args.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return; }
            handleMessage(message);
        };
        view.Navigate(url);
    }

    public static bool IsInitializationError(Exception error) => error is ExternalException or IOException or
        UnauthorizedAccessException or InvalidOperationException or ArgumentException;
}
