using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace DynamicIsland;

internal sealed class StudioWindow(Func<Appearance> current, Action<Appearance> change) : Form
{
    private const string Host = "island-editor.example";
    private const string Url = "https://" + Host + "/editor.html";
    private const string PresetFilter = "Island preset|*.island";
    private CoreWebView2Controller? controller;
    private CoreWebView2? View => controller?.CoreWebView2;
    private bool starting, ready;
    private string status = "Opening Island Studio…";

    public void Open(Form owner)
    {
        if (Owner is null)
        {
            Text = "Island Studio";
            BackColor = Color.FromArgb(25, 23, 21);
            ForeColor = Color.FromArgb(244, 232, 221);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new(960, 720);
            Show(owner);
        }
        else
        {
            Show();
        }

        Activate();
        if (controller is null && !starting)
            _ = StartAsync();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (controller is not null)
            controller.Bounds = ClientRectangle;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        controller?.NotifyParentWindowPositionChanged();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (controller is not null)
            controller.IsVisible = Visible;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (controller is null)
            TextRenderer.DrawText(e.Graphics, status, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private async Task StartAsync()
    {
        starting = true;
        status = "Opening Island Studio…";
        Invalidate();
        try
        {
            if (!File.Exists(Path.Combine(Presets.AssetsFolder, "editor.html")))
            {
                status = "Keep assets/editor.html next to DynamicIsland.exe.";
                return;
            }

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(Presets.SettingsFolder, "Studio"));
            if (IsDisposed)
                return;
            var viewController = await environment.CreateCoreWebView2ControllerAsync(Handle);
            if (IsDisposed)
            {
                viewController.Close();
                return;
            }

            controller = viewController;
            controller.Bounds = ClientRectangle;
            controller.DefaultBackgroundColor = BackColor;
            controller.IsVisible = Visible;
            // File dialogs run a modal loop, so handle commands after the WebView2 callback returns.
            WebDocuments.Load(controller.CoreWebView2, Host, Url, command => BeginInvoke(() => HandleCommand(command)));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            status = "Microsoft Edge WebView2 Runtime is required for Island Studio.";
            CloseView();
        }
        catch (Exception error) when (WebDocuments.IsInitializationError(error))
        {
            status = "Could not start Island Studio.";
            CloseView();
        }
        finally
        {
            starting = false;
            if (!IsDisposed)
                Invalidate();
        }
    }

    public void RefreshAppearance() => Publish(includeGif: true);

    private void Publish(bool includeGif)
    {
        if (!ready || View is not { } view)
            return;
        var appearance = current();
        var data = new Dictionary<string, object>
        {
            ["config"] = appearance.Encode(),
            ["presets"] = Presets.Named().Select(Path.GetFileNameWithoutExtension).ToArray()
        };
        if (includeGif)
            data["gif"] = appearance.Gif is { } gif ? "data:image/gif;base64," + Convert.ToBase64String(gif) : "";
        view.PostWebMessageAsJson(JsonSerializer.Serialize(data));
    }

    private void Status(string message) => View?.PostWebMessageAsJson(JsonSerializer.Serialize(new { status = message }));

    private void HandleCommand(string command)
    {
        if (IsDisposed)
            return;

        switch (command)
        {
            case "ready": ready = true; RefreshAppearance(); return;
            case "reset": Apply(new Appearance()); return;
            case "remove-gif":
                var appearance = current();
                Apply(appearance with { Gif = null, Icon = appearance.Icon == 2 ? 0 : appearance.Icon });
                return;
            case "gif": ImportGif(); return;
            case "export": ExportPreset(); return;
            case "import": ImportPreset(); return;
        }

        if (command.StartsWith("apply:", StringComparison.Ordinal))
        {
            if (command.Length < 270 && current().Decode(command[6..]) is { } parsed)
                change(parsed);
        }
        else if (command.StartsWith("save:", StringComparison.Ordinal) || command.StartsWith("load:", StringComparison.Ordinal))
        {
            NamedPreset(command[..4] == "save", command[5..]);
        }
    }

    private void Apply(Appearance appearance)
    {
        change(appearance);
        RefreshAppearance();
    }

    private void ImportGif()
    {
        using var dialog = new OpenFileDialog { Filter = "GIF image|*.gif", RestoreDirectory = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var bytes = Presets.ReadLimited(dialog.FileName, Presets.MaxGifBytes);
        if (bytes is null || !Presets.ValidGif(bytes))
        {
            Status("Choose a valid GIF up to 4 MB and 1024 × 1024.");
            return;
        }
        Apply(current() with { Gif = bytes, Icon = 2 });
    }

    private void ExportPreset()
    {
        using var dialog = new SaveFileDialog { Filter = PresetFilter, DefaultExt = "island", RestoreDirectory = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            Status(Presets.Write(dialog.FileName, current()) ? "Preset exported with artwork." : "Could not save preset.");
    }

    private void ImportPreset()
    {
        using var dialog = new OpenFileDialog { Filter = PresetFilter, RestoreDirectory = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadPreset(dialog.FileName, "Preset imported.", "Could not read this preset.");
    }

    private void NamedPreset(bool save, string name)
    {
        if (name.Length is < 1 or > 32 || name.Contains('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.StartsWith(' ') || name.EndsWith(' '))
        {
            Status("Enter a name of up to 32 letters or digits.");
            return;
        }

        string path = Path.Combine(Presets.NamedFolder, name + ".island");
        if (save)
        {
            Status(Presets.Write(path, current()) ? "Preset saved." : "Could not save preset.");
            Publish(includeGif: false);
        }
        else
        {
            LoadPreset(path, "Preset loaded.", "Preset not found or invalid.");
        }
    }

    private void LoadPreset(string path, string success, string failure)
    {
        if (Presets.Read(path) is { } appearance)
        {
            Apply(appearance);
            Status(success);
        }
        else
        {
            Status(failure);
        }
    }

    private void CloseView()
    {
        controller?.Close();
        controller = null;
        ready = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseView();
        base.Dispose(disposing);
    }
}
