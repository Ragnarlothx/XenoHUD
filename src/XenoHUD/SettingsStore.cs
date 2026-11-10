using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XenoHUD;

/// <summary>
/// settings.json = { "host": {...}, "ui": {...} }. The host only reads "host"; "ui" belongs to the web UI.
/// Every save backs up the previous file first.
/// </summary>
sealed partial class SettingsStore
{
    const int KeepBackups = 30;
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public JsonObject Root { get; private set; } = Defaults();

    public JsonObject Host => (JsonObject)Root["host"]!;

    public static JsonObject Defaults() => new()
    {
        ["host"] = new JsonObject
        {
            ["startMode"] = "window",     // window | hud | desktop | overlay
            ["sampleMs"] = 1000,
            ["procSampleMs"] = 2000,
            ["cpuSensors"] = false,       // opt-in; only works if PawnIO is already installed and app is admin
            ["desktopMonitor"] = "primary", // primary | all
            ["topmost"] = false,
            ["frameless"] = true,
            ["overlay"] = new JsonObject
            {
                ["corner"] = "tr",        // tl | tr | bl | br | custom (x/y set by dragging)
                ["monitor"] = "primary",  // primary | cursor
                ["margin"] = 16,
                ["width"] = 340,          // CSS px; the UI can also ask the host to fit its content
                ["height"] = 260,
                ["opacity"] = 0.92,
                ["clickThrough"] = false,
                ["x"] = null,
                ["y"] = null,
            },
            ["alerts"] = new JsonObject
            {
                ["enabled"] = true,
                ["tray"] = true,           // also show a Windows tray notification
                ["cpuTempC"] = 90.0,
                ["gpuTempC"] = 85.0,
                ["ramPct"] = 92.0,
                ["diskFreeGB"] = 10.0,
                ["sustainSamples"] = 5.0,  // condition must hold this many samples in a row
                ["cooldownMin"] = 10.0,
            },
            ["hotkeys"] = new JsonObject
            {
                ["hud"] = "Ctrl+Alt+H",
                ["desktop"] = "Ctrl+Alt+D",
                ["window"] = "Ctrl+Alt+W",
                ["overlay"] = "Ctrl+Alt+O",
                ["clickThrough"] = "Ctrl+Alt+C",
            },
        },
        ["ui"] = new JsonObject(),
    };

    public void Load()
    {
        var root = Defaults();
        try
        {
            if (File.Exists(Paths.Settings) && JsonNode.Parse(File.ReadAllText(Paths.Settings)) is JsonObject saved)
                Merge(root, saved);
        }
        catch
        {
            // Unreadable file: keep it as a backup and start from defaults.
            BackupCurrent("corrupt");
        }
        Root = root;
    }

    public void Save(JsonObject incoming)
    {
        var root = Defaults();
        Merge(root, incoming);
        BackupCurrent("settings");
        var tmp = Paths.Settings + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Pretty));
        File.Move(tmp, Paths.Settings, overwrite: true);
        Root = root;
    }

    public string[] ListBackups() =>
        Directory.GetFiles(Paths.Backups, "*.json")
            .Select(Path.GetFileName).OfType<string>()
            .OrderByDescending(n => n).ToArray();

    public void Restore(string name)
    {
        if (!BackupName().IsMatch(name)) throw new ArgumentException("bad backup name");
        var src = Path.Combine(Paths.Backups, name);
        if (!File.Exists(src)) throw new FileNotFoundException(name);
        if (JsonNode.Parse(File.ReadAllText(src)) is not JsonObject obj) throw new InvalidDataException("not a settings file");
        Save(obj);
    }

    DateTime lastBackup = DateTime.MinValue;

    void BackupCurrent(string prefix)
    {
        if (!File.Exists(Paths.Settings)) return;
        // Rapid saves (sliders, dragging the overlay) share one backup so real restore points aren't pushed out.
        if (prefix == "settings" && (DateTime.UtcNow - lastBackup).TotalSeconds < 60) return;
        lastBackup = DateTime.UtcNow;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        File.Copy(Paths.Settings, Path.Combine(Paths.Backups, $"{prefix}-{stamp}.json"), overwrite: true);
        foreach (var old in ListBackups().Skip(KeepBackups))
            try { File.Delete(Path.Combine(Paths.Backups, old)); } catch { }
    }

    static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject so && target[key] is JsonObject to) Merge(to, so);
            else target[key] = value?.DeepClone();
        }
    }

    public int Int(string key, int fallback, int min, int max)
    {
        try { return Math.Clamp(Host[key]?.GetValue<int>() ?? fallback, min, max); }
        catch { return fallback; }
    }

    public bool Bool(string key) { try { return Host[key]?.GetValue<bool>() ?? false; } catch { return false; } }

    public string Str(string key, string fallback) { try { return Host[key]?.GetValue<string>() ?? fallback; } catch { return fallback; } }

    public JsonObject Overlay => (JsonObject)Host["overlay"]!;

    public T Ov<T>(string key, T fallback) { try { return Overlay[key] is { } n ? n.GetValue<T>() : fallback; } catch { return fallback; } }

    /// <summary>Numbers may arrive as int or double depending on who wrote them; parse the JSON text.</summary>
    public double? OvNum(string key) =>
        Overlay[key] is { } n && double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    [GeneratedRegex(@"^[A-Za-z]+-\d{8}-\d{6}-\d{3}\.json$")]
    private static partial Regex BackupName();
}
