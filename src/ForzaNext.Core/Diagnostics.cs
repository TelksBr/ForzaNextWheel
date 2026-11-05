namespace ForzaNext.Core;

public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaNextWheel");
    public static string Configuration => Path.Combine(DataDirectory, "configuration.json");

    public static void EnsureConfiguration()
    {
        Directory.CreateDirectory(DataDirectory);
        var seed = Path.Combine(AppContext.BaseDirectory, "configuration.json");
        if (!File.Exists(Configuration))
        {
            if (File.Exists(seed)) File.Copy(seed, Configuration);
            else Config.ProfileManager.SaveToFile(Configuration, new());
        }
    }
}

public static class DiagnosticLog
{
    private static readonly object Sync = new();
    public static void Write(string context, string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var path = Path.Combine(AppPaths.DataDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{context}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Logging failed: {ex.Message}"); }
        }
    }
}
