using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XenoHUD;

/// <summary>Rolling in-memory history of the headline numbers, so graphs survive a page reload and can zoom out.</summary>
sealed class History
{
    public const int Capacity = 3600;
    readonly object gate = new();
    readonly Queue<Point> points = new();

    public record Point(long T, double? Cpu, double? CpuTemp, double? Mem, double?[] GpuLoad, double?[] GpuTemp,
                        double? NetRx, double? NetTx, double? DiskBusy);

    static double? D(JsonNode? n) { try { return n?.GetValue<double>(); } catch { return null; } }

    public void Add(JsonNode snap)
    {
        var gpus = snap["gpus"] as JsonArray ?? [];
        var disks = snap["disks"] as JsonArray ?? [];
        var p = new Point(
            (long)(D(snap["t"]) ?? 0),
            D(snap["cpu"]?["total"]), D(snap["cpu"]?["temp"]), D(snap["mem"]?["pct"]),
            gpus.Select(g => D(g?["load"])).ToArray(), gpus.Select(g => D(g?["temp"])).ToArray(),
            D(snap["net"]?["rxBps"]), D(snap["net"]?["txBps"]),
            disks.Select(d => D(d?["busy"])).Where(v => v.HasValue).DefaultIfEmpty(null).Max());
        lock (gate)
        {
            points.Enqueue(p);
            while (points.Count > Capacity) points.Dequeue();
        }
    }

    /// <summary>Last <paramref name="seconds"/> of history, averaged into at most <paramref name="maxPoints"/> buckets.</summary>
    public object Get(int seconds, int maxPoints)
    {
        Point[] all;
        lock (gate) all = points.ToArray();
        long from = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - seconds * 1000L;
        var sel = all.Where(p => p.T >= from).ToArray();
        int bucket = Math.Max(1, (int)Math.Ceiling(sel.Length / (double)Math.Max(10, maxPoints)));

        static double? Avg(IEnumerable<double?> xs) { var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToArray(); return v.Length == 0 ? null : Math.Round(v.Average(), 1); }
        static double?[] AvgArr(Point[] ps, Func<Point, double?[]> f)
        {
            int n = ps.Max(p => f(p).Length);
            return Enumerable.Range(0, n).Select(i => Avg(ps.Select(p => i < f(p).Length ? f(p)[i] : null))).ToArray();
        }

        var outPts = sel.Chunk(bucket).Select(c => new
        {
            t = c[^1].T,
            cpu = Avg(c.Select(p => p.Cpu)),
            cpuTemp = Avg(c.Select(p => p.CpuTemp)),
            mem = Avg(c.Select(p => p.Mem)),
            gpuLoad = AvgArr(c, p => p.GpuLoad),
            gpuTemp = AvgArr(c, p => p.GpuTemp),
            netRx = Avg(c.Select(p => p.NetRx)),
            netTx = Avg(c.Select(p => p.NetTx)),
            diskBusy = Avg(c.Select(p => p.DiskBusy)),
        }).ToArray();
        return new { seconds, bucketSamples = bucket, points = outPts };
    }
}

/// <summary>
/// Threshold alerts (temps, RAM, low disk). A condition must hold for several samples in a row before it fires,
/// and each alert has a cooldown, so spikes don't spam the tray.
/// </summary>
sealed class AlertWatcher
{
    readonly Dictionary<string, int> streak = new();
    readonly Dictionary<string, DateTime> lastFired = new();
    public event Action<object>? Fired;

    public void Check(JsonNode snap, JsonObject cfg)
    {
        double C(string k, double fallback) { try { return cfg[k]?.GetValue<double>() ?? fallback; } catch { return fallback; } }
        bool on; try { on = cfg["enabled"]?.GetValue<bool>() ?? true; } catch { on = true; }
        if (!on) return;
        static double? D(JsonNode? n) { try { return n?.GetValue<double>(); } catch { return null; } }

        int need = (int)C("sustainSamples", 5);
        var cooldown = TimeSpan.FromMinutes(C("cooldownMin", 10));

        void Test(string key, bool bad, string title, string text, string severity)
        {
            streak[key] = bad ? streak.GetValueOrDefault(key) + 1 : 0;
            if (!bad || streak[key] != need) return;
            if (lastFired.TryGetValue(key, out var at) && DateTime.UtcNow - at < cooldown) return;
            lastFired[key] = DateTime.UtcNow;
            Fired?.Invoke(new { key, title, text, severity, t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }

        if (D(snap["cpu"]?["temp"]) is double ct) Test("cpuTemp", ct >= C("cpuTempC", 90), "CPU running hot", $"CPU at {ct:0} °C", "critical");
        if (D(snap["mem"]?["pct"]) is double mp) Test("ram", mp >= C("ramPct", 92), "Memory almost full", $"RAM at {mp:0}%", "warning");
        if (snap["gpus"] is JsonArray gpus)
            for (int i = 0; i < gpus.Count; i++)
                if (D(gpus[i]?["temp"]) is double gt)
                    Test($"gpuTemp{i}", gt >= C("gpuTempC", 85), "GPU running hot", $"{gpus[i]?["name"]} at {gt:0} °C", "critical");
        if (snap["disks"] is JsonArray disks)
            foreach (var d in disks)
                if (D(d?["freeGB"]) is double free && D(d?["totalGB"]) is double total && total >= 8)
                    Test($"disk{d?["name"]}", free <= C("diskFreeGB", 10), "Disk almost full", $"{d?["name"]} has {free:0.#} GB free", "warning");
    }
}

/// <summary>Per-process GPU utilisation from the "GPU Engine" performance counters (no admin needed).</summary>
sealed partial class GpuEngines
{
    Dictionary<string, CounterSample> last = new();

    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
            if (data == null) return result;
            var now = new Dictionary<string, CounterSample>();
            var perPidEngine = new Dictionary<(int pid, string eng), double>();
            foreach (InstanceData inst in data.Values)
            {
                now[inst.InstanceName] = inst.Sample;
                if (!last.TryGetValue(inst.InstanceName, out var prev)) continue;
                var m = Instance().Match(inst.InstanceName);
                if (!m.Success) continue;
                double v = CounterSample.Calculate(prev, inst.Sample);
                if (double.IsNaN(v) || v <= 0) continue;
                var key = (int.Parse(m.Groups[1].Value), m.Groups[2].Value);
                perPidEngine[key] = perPidEngine.GetValueOrDefault(key) + v;
            }
            last = now;
            // Like Task Manager: a process's GPU % is its busiest engine type.
            foreach (var g in perPidEngine.GroupBy(k => k.Key.pid))
                result[g.Key] = Math.Round(Math.Min(100, g.Max(x => x.Value)), 1);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        return result;
    }

    [GeneratedRegex(@"^pid_(\d+)_.*_engtype_(.+)$")]
    private static partial Regex Instance();
}
