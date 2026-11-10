using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace XenoHUD;

sealed class MainForm : Form
{
    const string Origin = "https://xenohud.local/";
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Never offer to kill these, even with confirmation: doing so blue-screens or logs the user out.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "MemCompression", "Secure System", "explorer",
    };

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly SettingsStore settings = new();
    readonly DesktopLayer desktop = new();
    readonly Hotkeys hotkeys = new();
    readonly NotifyIcon tray = new();
    readonly string? startModeArg;
    SensorHub? hub;
    readonly History history = new();
    bool webReady;

    void OnAlert(object a)
    {
        Post("alert", a);
        bool tray; try { tray = settings.Host["alerts"]?["tray"]?.GetValue<bool>() ?? true; } catch { tray = true; }
        if (tray && JsonSerializer.SerializeToNode(a) is JsonObject o)
            this.tray.ShowBalloonTip(5000, o["title"]?.ToString() ?? "XenoHUD", o["text"]?.ToString() ?? "", ToolTipIcon.Warning);
    }
    string mode = "window";
    Rectangle windowBounds = new(120, 80, 1440, 900);
    bool clickThrough;
    Size overlayCss = Size.Empty; // from fitOverlay; empty = use settings width/height
    System.Windows.Forms.Timer? savePosTimer;

    public MainForm(string? startMode)
    {
        startModeArg = startMode;
        settings.Load();

        Text = "XenoHUD";
        BackColor = Color.Black;
        Icon = MakeIcon();
        StartPosition = FormStartPosition.Manual;
        Bounds = windowBounds;
        MinimumSize = new Size(640, 400);
        Controls.Add(web);

        tray.Icon = Icon;
        tray.Text = "XenoHUD";
        tray.Visible = true;
        tray.ContextMenuStrip = BuildTrayMenu();
        tray.DoubleClick += (_, _) => SetMode("window");

        hotkeys.Pressed += a =>
        {
            if (a == "clickThrough") ToggleClickThrough();
            else SetMode(a == mode && a != "window" ? "window" : a);
        };
        ApplyHostSettings(restartHub: true);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Native.DarkTitleBar(Handle);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData);
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = true; // F12 for UI work
            core.SetVirtualHostNameToFolderMapping("xenohud.local", Paths.FindWeb(), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, ev) => OnWebMessage(ev.WebMessageAsJson);
            core.NewWindowRequested += (_, ev) => { ev.Handled = true; OpenUrl(ev.Uri); };
            core.NavigationStarting += (_, ev) =>
            {
                if (!ev.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) { ev.Cancel = true; OpenUrl(ev.Uri); }
            };
            core.Navigate(Origin + "index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 could not start:\n\n" + ex.Message +
                "\n\nInstall the Microsoft Edge WebView2 Runtime and try again.", "XenoHUD", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        SetMode(startModeArg ?? settings.Str("startMode", "window"), force: true);
    }

    // ---- Host -> UI ---------------------------------------------------------

    void Post(string type, object? data)
    {
        if (!webReady || web.CoreWebView2 == null) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, data }, Json));
    }

    void PostRaw(string type, string dataJson)
    {
        if (!webReady || web.CoreWebView2 == null) return;
        web.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"{type}\",\"data\":{dataJson}}}");
    }

    object Hello() => new
    {
        app = "XenoHUD",
        version = Application.ProductVersion,
        mode,
        info = SensorHub.StaticInfo(),
        dataDir = Paths.Data,
        backupsDir = Paths.Backups,
        hotkeyErrors = hotkeys.Failed,
        cpuSensorsActive = settings.Bool("cpuSensors") && SensorHub.IsAdmin,
    };

    // ---- UI -> Host ---------------------------------------------------------

    void OnWebMessage(string json)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(json) as JsonObject; } catch { return; }
        if (msg == null) return;
        var type = msg["type"]?.GetValue<string>() ?? "";
        var id = msg["id"];
        try
        {
            var data = Dispatch(type, msg);
            if (id != null) Reply(id, true, data, null);
        }
        catch (Exception ex)
        {
            if (id != null) Reply(id, false, null, ex.Message);
        }
    }

    void Reply(JsonNode id, bool ok, object? data, string? error)
    {
        if (web.CoreWebView2 == null) return;
        var obj = new JsonObject
        {
            ["type"] = "result",
            ["id"] = id.DeepClone(),
            ["ok"] = ok,
            ["data"] = data == null ? null : JsonSerializer.SerializeToNode(data, Json),
            ["error"] = error,
        };
        web.CoreWebView2.PostWebMessageAsJson(obj.ToJsonString());
    }

    object? Dispatch(string type, JsonObject m)
    {
        string S(string k) => m[k]?.GetValue<string>() ?? throw new ArgumentException($"missing '{k}'");
        int I(string k) => m[k]?.GetValue<int>() ?? throw new ArgumentException($"missing '{k}'");

        switch (type)
        {
            case "ready":
                webReady = true;
                return new { hello = Hello(), settings = settings.Root };

            case "getSettings": return settings.Root;
            case "getDefaults": return SettingsStore.Defaults();

            case "saveSettings":
                {
                    if (m["data"] is not JsonObject data) throw new ArgumentException("missing 'data'");
                    bool cpuBefore = settings.Bool("cpuSensors");
                    settings.Save(data);
                    ApplyHostSettings(restartHub: cpuBefore != settings.Bool("cpuSensors"));
                    Post("settings", settings.Root);
                    return settings.Root;
                }

            case "resetSettings":
                settings.Save(SettingsStore.Defaults());
                ApplyHostSettings(restartHub: true);
                Post("settings", settings.Root);
                return settings.Root;

            case "listBackups": return settings.ListBackups();
            case "restoreBackup":
                settings.Restore(S("name"));
                ApplyHostSettings(restartHub: true);
                Post("settings", settings.Root);
                return settings.Root;
            case "openBackups": Process.Start("explorer.exe", Paths.Backups); return null;

            case "setMode": SetMode(S("mode")); return mode;
            case "setClickThrough": SetClickThrough(m["on"]?.GetValue<bool>() ?? false); return clickThrough;
            case "fitOverlay":
                {
                    // UI measured its content in CSS px; size the overlay to match.
                    var w = m["width"]?.GetValue<double>() ?? throw new ArgumentException("missing 'width'");
                    var h = m["height"]?.GetValue<double>() ?? throw new ArgumentException("missing 'height'");
                    overlayCss = new Size((int)Math.Clamp(w, 120, 1600), (int)Math.Clamp(h, 60, 1200));
                    if (mode == "overlay") PlaceOverlay();
                    return new { overlayCss.Width, overlayCss.Height };
                }
            case "getMode": return mode;
            case "minimize": if (mode == "window") WindowState = FormWindowState.Minimized; return null;
            case "toggleMaximize":
                if (mode == "window") WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                return null;
            case "setTopmost": if (mode == "window") TopMost = m["on"]?.GetValue<bool>() ?? false; return TopMost;
            case "startDrag":
                if (mode == "window" || (mode == "overlay" && !clickThrough)) { Native.ReleaseCapture(); Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero); }
                return null;
            case "startResize":
                {
                    // Frameless window: UI puts thin edge/corner grips over the page and calls this on mousedown.
                    int ht = S("edge") switch
                    {
                        "left" => 10, "right" => 11, "top" => 12, "topleft" => 13, "topright" => 14,
                        "bottom" => 15, "bottomleft" => 16, "bottomright" => 17,
                        _ => throw new ArgumentException("edge must be left|right|top|bottom|topleft|topright|bottomleft|bottomright"),
                    };
                    if (mode == "window" && WindowState == FormWindowState.Normal)
                    { Native.ReleaseCapture(); Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, ht, IntPtr.Zero); }
                    return null;
                }
            case "exit": BeginInvoke(Close); return null;

            case "killProcess": return KillProcess(I("pid"));
            case "processDetails": return SensorHub.ProcessDetails(I("pid"));
            case "netConnections": return SensorHub.NetConnections();
            case "getHistory":
                return history.Get(Math.Clamp(m["seconds"]?.GetValue<int>() ?? 300, 10, History.Capacity * 10),
                                   Math.Clamp(m["maxPoints"]?.GetValue<int>() ?? 300, 10, 2000));
            case "testAlert": OnAlert(new { key = "test", title = "XenoHUD alert test", text = "Alerts are working.", severity = "warning", t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); return null;

            case "openPath":
                {
                    var p = S("path");
                    if (Directory.Exists(p)) Process.Start("explorer.exe", $"\"{p}\"");
                    else if (File.Exists(p)) Process.Start("explorer.exe", $"/select,\"{p}\"");
                    else throw new FileNotFoundException(p);
                    return null;
                }
            case "launch":
                {
                    var p = S("path");
                    if (!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException(p);
                    Process.Start(new ProcessStartInfo(p, m["args"]?.GetValue<string>() ?? "") { UseShellExecute = true });
                    return null;
                }
            case "openUrl": OpenUrl(S("url")); return null;

            case "relaunchAdmin": RelaunchAdmin(); return null;
            case "notify":
                tray.ShowBalloonTip(3000, m["title"]?.GetValue<string>() ?? "XenoHUD", S("text"), ToolTipIcon.None);
                return null;

            default: throw new NotSupportedException($"unknown message '{type}'");
        }
    }

    // ---- Actions ------------------------------------------------------------

    object KillProcess(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId) throw new InvalidOperationException("That process can't be killed from here.");
        using var p = Process.GetProcessById(pid);
        var name = p.ProcessName;
        if (Protected.Contains(name)) throw new InvalidOperationException($"{name} is a core Windows process and is protected.");

        // Confirm natively, so nothing in the web layer can kill a process silently.
        var owner = mode == "desktop" ? null : this;
        var answer = MessageBox.Show(owner, $"Kill {name} (PID {pid})?\n\nUnsaved work in it will be lost.", "XenoHUD — confirm",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return new { killed = false };
        p.Kill(entireProcessTree: false);
        return new { killed = true, name, pid };
    }

    static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    void RelaunchAdmin()
    {
        if (SensorHub.IsAdmin) return;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--after-pid {Environment.ProcessId} --mode {mode}")
            { UseShellExecute = true, Verb = "runas" });
            BeginInvoke(Close);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User said no at the UAC prompt. Keep running as-is.
        }
    }

    void ApplyHostSettings(bool restartHub)
    {
        if (restartHub || hub == null)
        {
            hub?.Dispose();
            hub = new SensorHub(settings.Bool("cpuSensors"), history);
            hub.Snapshot += json => { try { BeginInvoke(() => PostRaw("snapshot", json)); } catch { } };
            hub.Alerts.Fired += a => { try { BeginInvoke(() => OnAlert(a)); } catch { } };
            hub.Start();
        }
        hub.AlertConfig = settings.Host["alerts"] as JsonObject;
        hub.IntervalMs = settings.Int("sampleMs", 1000, 250, 10_000);
        hub.ProcIntervalMs = settings.Int("procSampleMs", 2000, 500, 30_000);

        var map = new Dictionary<string, string>();
        if (settings.Host["hotkeys"] is JsonObject hk)
            foreach (var (k, v) in hk)
                if (k is "hud" or "desktop" or "window" or "overlay" or "clickThrough" && v?.GetValue<string>() is { Length: > 0 } s) map[k] = s;
        hotkeys.Register(map);

        if (mode == "window") TopMost = settings.Bool("topmost");
        if (mode == "window" && IsHandleCreated) FormBorderStyle = settings.Bool("frameless") ? FormBorderStyle.None : FormBorderStyle.Sizable;
        if (mode == "overlay" && IsHandleCreated)
        {
            clickThrough = settings.Ov("clickThrough", false);
            PlaceOverlay();
            PostMode();
        }
    }

    // ---- Modes --------------------------------------------------------------

    void SetMode(string next, bool force = false)
    {
        if (next is not ("window" or "hud" or "desktop" or "overlay")) throw new ArgumentException($"unknown mode '{next}'");
        if (next == mode && !force) return;

        if (mode == "window" && !force) windowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (desktop.Attached) desktop.Detach(this);
        WindowState = FormWindowState.Normal;
        if (mode == "overlay" || force) { clickThrough = false; Native.SetOverlayStyle(Handle, 1, false); }
        MinimumSize = next == "window" ? new Size(640, 400) : Size.Empty;

        switch (next)
        {
            case "window":
                FormBorderStyle = settings.Bool("frameless") ? FormBorderStyle.None : FormBorderStyle.Sizable;
                TopMost = settings.Bool("topmost");
                Bounds = windowBounds;
                break;
            case "overlay":
                FormBorderStyle = FormBorderStyle.None;
                TopMost = true;
                clickThrough = settings.Ov("clickThrough", false);
                mode = next; // PlaceOverlay reads the mode
                PlaceOverlay();
                break;
            case "hud":
                FormBorderStyle = FormBorderStyle.None;
                TopMost = true;
                Bounds = Screen.FromPoint(Cursor.Position).Bounds;
                break;
            case "desktop":
                FormBorderStyle = FormBorderStyle.None;
                TopMost = false;
                if (!desktop.Attach(this, settings.Str("desktopMonitor", "primary") == "all"))
                {
                    tray.ShowBalloonTip(4000, "XenoHUD", "Couldn't attach to the desktop on this Windows build. Staying in window mode.", ToolTipIcon.Warning);
                    SetMode("window", force: true);
                    return;
                }
                break;
        }
        mode = next;
        Show();
        if (next is "window" or "hud") Activate();
        PostMode();
    }

    void PostMode() => Post("mode", new
    {
        mode,
        interactive = mode != "desktop" && !(mode == "overlay" && clickThrough),
        clickThrough = mode == "overlay" && clickThrough,
    });

    /// <summary>Size, place and style the compact overlay from host.overlay settings.</summary>
    void PlaceOverlay()
    {
        if (mode != "overlay") return;
        var screen = settings.Ov("monitor", "primary") == "cursor" ? Screen.FromPoint(Cursor.Position) : Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        double scale = DeviceDpi / 96.0;
        var css = overlayCss.IsEmpty
            ? new Size((int)(settings.OvNum("width") ?? 340), (int)(settings.OvNum("height") ?? 260))
            : overlayCss;
        int w = (int)Math.Round(Math.Clamp(css.Width, 120, 1600) * scale);
        int h = (int)Math.Round(Math.Clamp(css.Height, 60, 1200) * scale);
        int margin = (int)Math.Round(Math.Clamp(settings.OvNum("margin") ?? 16, 0, 400) * scale);

        int x, y;
        var corner = settings.Ov("corner", "tr");
        if (corner == "custom" && settings.OvNum("x") is double cx && settings.OvNum("y") is double cy)
        {
            // Keep a dragged overlay on some screen even if monitors changed.
            var r = new Rectangle((int)cx, (int)cy, w, h);
            var on = Screen.FromRectangle(r).WorkingArea;
            x = Math.Clamp(r.X, on.Left, Math.Max(on.Left, on.Right - w));
            y = Math.Clamp(r.Y, on.Top, Math.Max(on.Top, on.Bottom - h));
        }
        else
        {
            x = corner is "tl" or "bl" ? area.Left + margin : area.Right - w - margin;
            y = corner is "tl" or "tr" ? area.Top + margin : area.Bottom - h - margin;
        }
        Bounds = new Rectangle(x, y, w, h);
        Native.SetOverlayStyle(Handle, Math.Clamp(settings.OvNum("opacity") ?? 0.92, 0.2, 1), clickThrough);
    }

    void SetClickThrough(bool on)
    {
        clickThrough = on;
        if (mode == "overlay")
        {
            Native.SetOverlayStyle(Handle, Math.Clamp(settings.OvNum("opacity") ?? 0.92, 0.2, 1), clickThrough);
            PostMode();
        }
    }

    void ToggleClickThrough()
    {
        if (mode != "overlay") { SetMode("overlay"); return; }
        SetClickThrough(!clickThrough);
        tray.ShowBalloonTip(1500, "XenoHUD", clickThrough ? "Overlay is click-through. Ctrl+Alt+C to grab it again." : "Overlay is clickable.", ToolTipIcon.None);
    }

    /// <summary>After the user drags the overlay, remember where it went (debounced so one drag = one save).</summary>
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (mode != "overlay") return;
        savePosTimer ??= new System.Windows.Forms.Timer { Interval = 800 };
        savePosTimer.Stop();
        savePosTimer.Tick -= SaveOverlayPos;
        savePosTimer.Tick += SaveOverlayPos;
        savePosTimer.Start();
    }

    void SaveOverlayPos(object? s, EventArgs e)
    {
        savePosTimer?.Stop();
        if (mode != "overlay") return;
        var root = (JsonObject)settings.Root.DeepClone();
        var ov = (JsonObject)((JsonObject)root["host"]!)["overlay"]!;
        ov["corner"] = "custom";
        ov["x"] = Left;
        ov["y"] = Top;
        settings.Save(root);
        Post("settings", settings.Root);
    }

    // ---- Tray + icon --------------------------------------------------------

    ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { BackColor = Color.FromArgb(0x20, 0x21, 0x24), ForeColor = Color.FromArgb(0xd4, 0xd4, 0xd4), ShowImageMargin = false };
        menu.Items.Add("Window mode", null, (_, _) => SetMode("window"));
        menu.Items.Add("HUD (full screen)", null, (_, _) => SetMode("hud"));
        menu.Items.Add("Compact overlay", null, (_, _) => SetMode("overlay"));
        menu.Items.Add("Overlay click-through on/off", null, (_, _) => ToggleClickThrough());
        menu.Items.Add("Desktop layer", null, (_, _) => SetMode("desktop"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restore normal desktop", null, (_, _) => SetMode("window"));
        menu.Items.Add("Open backups folder", null, (_, _) => Process.Start("explorer.exe", Paths.Backups));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        return menu;
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0x1e, 0x1f, 0x22));
            using var pen = new Pen(Color.FromArgb(0x4f, 0x9c, 0xf9), 3);
            g.DrawRectangle(pen, 2, 2, 27, 27);
            g.DrawLines(pen, [new Point(8, 10), new Point(14, 16), new Point(8, 22)]);
            g.DrawLine(pen, 16, 22, 24, 22);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (desktop.Attached) desktop.Detach(this);
        hub?.Dispose();
        hotkeys.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosing(e);
    }
}
