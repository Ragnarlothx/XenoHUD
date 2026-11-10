namespace XenoHUD;

static class Paths
{
    public static readonly string Data =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XenoHUD");
    public static readonly string Backups = Path.Combine(Data, "backups");
    public static readonly string Settings = Path.Combine(Data, "settings.json");
    public static readonly string WebViewData = Path.Combine(Data, "webview2");

    static Paths()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Backups);
    }

    /// <summary>web\ next to the exe; in a dev build, walk up to the repo's web\ so edits show up live.</summary>
    public static string FindWeb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "web");
            if (File.Exists(Path.Combine(candidate, "index.html")) && d.GetDirectories("src").Length > 0)
                return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "web");
    }
}
