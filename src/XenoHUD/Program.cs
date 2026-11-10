using System.Diagnostics;

namespace XenoHUD;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // When relaunching elevated, the new copy waits for the old one to exit before taking the lock.
        int afterPid = ArgValue(args, "--after-pid") is { } s && int.TryParse(s, out var p) ? p : 0;
        if (afterPid > 0)
        {
            try { Process.GetProcessById(afterPid).WaitForExit(10_000); } catch { }
        }

        // XenoHUD.exe --dump: write a few raw snapshots to %LOCALAPPDATA%\XenoHUD\snapshot.json and exit (for diagnosing sensors).
        if (args.Contains("--dump"))
        {
            var snaps = new List<string>();
            using var done = new ManualResetEventSlim();
            using var hub = new SensorHub(false, new History());
            hub.Snapshot += j => { lock (snaps) { snaps.Add(j); if (snaps.Count == 4) done.Set(); } };
            hub.Start();
            done.Wait(30_000);
            lock (snaps) File.WriteAllText(Path.Combine(Paths.Data, "snapshot.json"), "[" + string.Join(",\n", snaps) + "]");
            return;
        }

        using var mutex = new Mutex(true, "XenoHUD.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(ArgValue(args, "--mode")));
    }

    static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
