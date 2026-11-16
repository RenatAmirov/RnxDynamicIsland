namespace DynamicIsland;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new Mutex(true, "Local\\DynamicIsland", out bool firstInstance);
        if (!firstInstance || NativeMethods.FindWindow("DynamicIsland.Native", "Dynamic Island") != IntPtr.Zero)
            return;

        ApplicationConfiguration.Initialize();
        using var island = new IslandWindow();
        if (args is [var path] && File.Exists(path))
            island.OpenAudio(Path.GetFullPath(path));
        Application.Run(island);
    }
}
