using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DynamicIsland;

internal sealed record Appearance
{
    public string[] Blocks { get; init; } = ["AM", "ATM", "ATSPBFC"];
    public int IdleSeconds { get; init; } = 4;
    public bool Hover { get; init; } = true;
    public bool TrackWake { get; init; } = true;
    public bool Click { get; init; } = true;
    public int Icon { get; init; }
    public bool GifMini { get; init; } = true;
    public bool GifTrack { get; init; } = true;
    public bool GifFit { get; init; }
    public int GifSpeed { get; init; } = 100;
    public int MiniWidth { get; init; } = 96;
    public int TrackWidth { get; init; } = 248;
    public int PlayerWidth { get; init; } = 440;
    public int Radius { get; init; } = 30;
    public int Padding { get; init; } = 12;
    public int Font { get; init; }
    public int FontSize { get; init; } = 100;
    public int Opacity { get; init; } = 100;
    public bool Outline { get; init; } = true;
    public bool Gradient { get; init; } = true;
    public int Speed { get; init; } = 100;
    public int Drag { get; init; } = 135;
    public int First { get; init; } = 0x392821;
    public int Second { get; init; } = 0x1B1918;
    public bool ArtPalette { get; init; }
    public byte[]? Gif { get; init; }

    // Field order is shared by Island Studio and existing DynamicIsland/1 presets.
    public string Encode() => string.Join('|', Blocks) + "|" + FormattableString.Invariant(
        $"{IdleSeconds}|{Bit(Hover)}|{Bit(TrackWake)}|{Bit(Click)}|{Icon}|{Bit(GifMini)}|{Bit(GifTrack)}|{Bit(GifFit)}|{GifSpeed}|{MiniWidth}|{TrackWidth}|{PlayerWidth}|{Radius}|{Padding}|{Font}|{FontSize}|{Opacity}|{Bit(Outline)}|{Bit(Gradient)}|{Speed}|{Drag}|{First}|{Second}|{Bit(ArtPalette)}");

    private static int Bit(bool value) => value ? 1 : 0;

    public Appearance? Decode(string config)
    {
        if (config.Length > 256)
            return null;

        var tokens = (config.EndsWith('|') ? config[..^1] : config).Split('|');
        if (tokens.Length != 27)
            return null;

        var rows = tokens[..3];
        foreach (var row in rows)
        {
            if (row.Length is < 1 or > 8 || row.Any(id => !"ATSMPBFC".Contains(id)) ||
                row.Distinct().Count() != row.Length)
                return null;
        }

        var values = new int[24];
        for (int i = 0; i < values.Length; i++)
        {
            string value = tokens[i + 3];
            ReadOnlySpan<char> digits = value.StartsWith('-') ? value.AsSpan(1) : value;
            if (digits.IsEmpty || digits.ContainsAnyExceptInRange('0', '9') || !int.TryParse(value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out values[i]))
                return null;
        }

        int Field(int index, int min, int max) => Math.Clamp(values[index], min, max);
        bool Flag(int index) => values[index] > 0;
        return this with
        {
            Blocks = rows,
            IdleSeconds = Field(0, 0, 60), Hover = Flag(1), TrackWake = Flag(2), Click = Flag(3),
            Icon = Field(4, 0, 2), GifMini = Flag(5), GifTrack = Flag(6), GifFit = Flag(7),
            GifSpeed = Field(8, 25, 250), MiniWidth = Field(9, 68, 220), TrackWidth = Field(10, 150, 460),
            PlayerWidth = Field(11, 320, 620), Radius = Field(12, 8, 50), Padding = Field(13, 6, 22),
            Font = Field(14, 0, 2), FontSize = Field(15, 80, 135), Opacity = Field(16, 55, 100),
            Outline = Flag(17), Gradient = Flag(18), Speed = Field(19, 50, 200), Drag = Field(20, 0, 180),
            First = Field(21, 0, 0xFFFFFF), Second = Field(22, 0, 0xFFFFFF), ArtPalette = Flag(23)
        };
    }
}

internal static class Presets
{
    private static ReadOnlySpan<byte> Magic => "DynamicIsland/1\n"u8;
    public const int MaxGifBytes = 4 * 1024 * 1024;
    public static string SettingsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
    public static string AssetsFolder { get; } = Path.Combine(AppContext.BaseDirectory, "assets");
    public static string NamedFolder => Path.Combine(SettingsFolder, "Presets");
    public static string CurrentPath => Path.Combine(SettingsFolder, "Current.island");

    public static bool ValidGif(ReadOnlySpan<byte> bytes) => bytes.Length is >= 13 and <= MaxGifBytes &&
        (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)) &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) is > 0 and <= 1024 &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]) is > 0 and <= 1024;

    public static byte[]? ReadLimited(string path, int limit)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > limit)
                return null;
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Appearance? Read(string path)
    {
        var bytes = ReadLimited(path, MaxGifBytes + 512);
        if (bytes is null || !bytes.AsSpan().StartsWith(Magic))
            return null;

        int end = Array.IndexOf(bytes, (byte)'\n', Magic.Length);
        if (end < 0)
            end = bytes.Length;
        if (end - Magic.Length > 256)
            return null;

        var appearance = new Appearance().Decode(Encoding.ASCII.GetString(bytes, Magic.Length, end - Magic.Length));
        if (appearance is null || end + 1 >= bytes.Length)
            return appearance;

        var artwork = bytes.AsSpan(end + 1);
        return ValidGif(artwork) ? appearance with { Gif = artwork.ToArray() } : null;
    }

    public static bool Write(string path, Appearance appearance)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(Magic);
                file.Write(Encoding.ASCII.GetBytes(appearance.Encode() + "\n"));
                if (appearance.Gif is { } artwork)
                    file.Write(artwork);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    public static string[] Named()
    {
        try
        {
            return Directory.EnumerateFiles(NamedFolder, "*.island")
                .Take(200).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
