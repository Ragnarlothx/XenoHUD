using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace XenoHUD;

/// <summary>
/// Samples the machine on a background thread and raises one JSON snapshot per tick.
/// Nothing here writes to the system; it only reads counters.
/// </summary>
sealed class SensorHub : IDisposable
{
    public event Action<string>? Snapshot;

    public int IntervalMs { get; set; } = 1000;
    public int ProcIntervalMs { get; set; } = 2000;

    public History History { get; }
    public AlertWatcher Alerts { get; } = new();
    public System.Text.Json.Nodes.JsonObject? AlertConfig { get; set; }
    readonly GpuEngines gpuEngines = new();

    readonly bool cpuSensors;
    readonly Thread thread;
    readonly ManualResetEventSlim stop = new();
    long seq;

    PerformanceCounter? cpuTotal, cpuPerf;
    PerformanceCounter[] cores = [];
    readonly Dictionary<string, (PerformanceCounter read, PerformanceCounter write, PerformanceCounter idle)?> diskCounters = new();
    Computer? lhm;

    readonly Dictionary<string, (long rx, long tx)> lastNet = new();
    readonly Dictionary<string, (string[] ips, DateTime at)> ipCache = new();
    long lastNetTicks;

    readonly Dictionary<int, TimeSpan> lastProcCpu = new();
    readonly HashSet<int> deniedPids = new();
    long lastProcTicks, lastDeniedReset;
    object? lastProcs;

    public static readonly bool IsAdmin =
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public static readonly string CpuName = ReadCpuName();
    static readonly double CpuBaseMHz = ReadCpuMHz();

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SensorHub(bool cpuSensors, History history)
    {
        this.cpuSensors = cpuSensors;
        History = history; // owned by the form so it survives a sensor restart
        thread = new Thread(Run) { IsBackground = true, Name = "XenoHUD sensors", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => thread.Start();

    public static object StaticInfo() => new
    {
        machine = Environment.MachineName,
        user = Environment.UserName,
        os = RuntimeInformation.OSDescription,
        osBuild = Environment.OSVersion.Version.ToString(),
        cpuName = CpuName,
        logicalCores = Environment.ProcessorCount,
        cpuBaseMHz = CpuBaseMHz,
        isAdmin = IsAdmin,
    };

    void Run()
    {
        Init();
        while (!stop.IsSet)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var node = JsonSerializer.SerializeToNode(Sample(), Json)!;
                History.Add(node);
                if (AlertConfig is { } cfg) Alerts.Check(node, cfg);
                Snapshot?.Invoke(node.ToJsonString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            var spent = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            stop.Wait(Math.Max(50, IntervalMs - spent));
        }
    }

    void Init()
    {
        try
        {
            cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            cores = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(i => new PerformanceCounter("Processor", "% Processor Time", i.ToString())).ToArray();
            cpuTotal.NextValue(); cpuPerf.NextValue();
            foreach (var c in cores) c.NextValue();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }

        try
        {
            lhm = new Computer
            {
                IsGpuEnabled = true,
                IsCpuEnabled = cpuSensors && IsAdmin,
                IsMotherboardEnabled = cpuSensors && IsAdmin,
            };
            lhm.Open();
        }
        catch (Exception ex) { Debug.WriteLine(ex); lhm = null; }
    }

    object Sample()
    {
        var now = DateTime.UtcNow;
        var hw = ReadHardware();

        return new
        {
            seq = ++seq,
            t = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            intervalMs = IntervalMs,
            cpu = ReadCpu(hw),
            mem = ReadMem(),
            gpus = hw.gpus,
            disks = ReadDisks(),
            net = ReadNet(),
            procs = ReadProcs(),
            sys = new
            {
                uptimeSec = Environment.TickCount64 / 1000,
                isAdmin = IsAdmin,
                battery = ReadBattery(),
            },
            sensors = hw.sensors,
        };
    }

    // ---- CPU ----------------------------------------------------------------

    object ReadCpu((List<object> gpus, List<object> sensors, double? cpuTemp, double? cpuPower) hw)
    {
        double Safe(PerformanceCounter? c) { try { return c?.NextValue() ?? 0; } catch { return 0; } }
        var perf = Safe(cpuPerf);
        return new
        {
            name = CpuName,
            threads = Environment.ProcessorCount,
            total = Math.Round(Math.Clamp(Safe(cpuTotal), 0, 100), 1),
            perCore = cores.Select(c => Math.Round(Math.Clamp(Safe(c), 0, 100), 1)).ToArray(),
            freqMHz = perf > 0 && CpuBaseMHz > 0 ? Math.Round(CpuBaseMHz * perf / 100) : (double?)null,
            temp = hw.cpuTemp,
            powerW = hw.cpuPower,
        };
    }

    // ---- Memory -------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    static object? ReadMem()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return null;
        const double MB = 1024 * 1024;
        return new
        {
            totalMB = Math.Round(m.ullTotalPhys / MB),
            usedMB = Math.Round((m.ullTotalPhys - m.ullAvailPhys) / MB),
            pct = (double)m.dwMemoryLoad,
            commitUsedMB = Math.Round((m.ullTotalPageFile - m.ullAvailPageFile) / MB),
            commitLimitMB = Math.Round(m.ullTotalPageFile / MB),
        };
    }

    // ---- GPUs + raw sensors (LibreHardwareMonitor) ---------------------------

    (List<object> gpus, List<object> sensors, double? cpuTemp, double? cpuPower) ReadHardware()
    {
        var gpus = new List<object>();
        var sensors = new List<object>();
        double? cpuTemp = null, cpuPower = null;
        if (lhm == null) return (gpus, sensors, cpuTemp, cpuPower);

        foreach (var h in lhm.Hardware)
        {
            try
            {
                h.Update();
                foreach (var sub in h.SubHardware) sub.Update();
            }
            catch { continue; }

            var all = h.Sensors.Concat(h.SubHardware.SelectMany(s => s.Sensors)).ToArray();
            foreach (var s in all)
                if (s.Value is float v && !float.IsNaN(v))
                    sensors.Add(new { hw = h.Name, hwType = h.HardwareType.ToString(), type = s.SensorType.ToString(), name = s.Name, value = Math.Round(v, 2) });

            double? Find(SensorType t, params string[] names)
            {
                foreach (var n in names)
                    if (all.FirstOrDefault(s => s.SensorType == t && s.Name == n)?.Value is float f) return Math.Round(f, 1);
                return null;
            }
            double? First(SensorType t) => all.FirstOrDefault(s => s.SensorType == t && s.Value != null)?.Value is float f ? Math.Round(f, 1) : null;

            switch (h.HardwareType)
            {
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpus.Add(new
                    {
                        name = h.Name,
                        vendor = h.HardwareType.ToString()[3..],
                        // Some AMD workstation drivers report "GPU Core" load as a constant 0; fall back to Windows' D3D 3D engine load.
                        load = Find(SensorType.Load, "GPU Core") is double core && (core > 0 || Find(SensorType.Load, "D3D 3D") is not > 0)
                            ? core : Find(SensorType.Load, "D3D 3D"),
                        memCtrlLoad = Find(SensorType.Load, "GPU Memory Controller"),
                        videoEngineLoad = Find(SensorType.Load, "GPU Video Engine", "D3D Video Decode"),
                        temp = Find(SensorType.Temperature, "GPU Core") ?? First(SensorType.Temperature),
                        hotspot = Find(SensorType.Temperature, "GPU Hot Spot"),
                        memUsedMB = Find(SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
                        memTotalMB = Find(SensorType.SmallData, "GPU Memory Total"),
                        powerW = Find(SensorType.Power, "GPU Package", "GPU Power", "GPU Core") ?? First(SensorType.Power),
                        coreMHz = Find(SensorType.Clock, "GPU Core"),
                        memMHz = Find(SensorType.Clock, "GPU Memory"),
                        fanRpm = First(SensorType.Fan),
                        fanPct = First(SensorType.Control),
                    });
                    break;
                case HardwareType.Cpu:
                    cpuTemp = Find(SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core Average") ?? First(SensorType.Temperature);
                    cpuPower = Find(SensorType.Power, "CPU Package", "Package") ?? First(SensorType.Power);
                    break;
            }
        }
        return (gpus, sensors, cpuTemp, cpuPower);
    }

    // ---- Disks --------------------------------------------------------------

    List<object> ReadDisks()
    {
        var list = new List<object>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                var letter = d.Name.TrimEnd('\\');
                if (!diskCounters.TryGetValue(letter, out var c))
                {
                    try
                    {
                        c = (new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", letter),
                             new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", letter),
                             new PerformanceCounter("LogicalDisk", "% Idle Time", letter));
                    }
                    catch { c = null; }
                    diskCounters[letter] = c;
                }
                double? read = null, write = null, busy = null;
                if (c is { } cc)
                {
                    try
                    {
                        read = Math.Round(cc.read.NextValue());
                        write = Math.Round(cc.write.NextValue());
                        busy = Math.Round(Math.Clamp(100 - cc.idle.NextValue(), 0, 100), 1);
                    }
                    catch { }
                }
                const double GB = 1024d * 1024 * 1024;
                list.Add(new
                {
                    name = letter,
                    label = d.VolumeLabel,
                    format = d.DriveFormat,
                    type = d.DriveType.ToString(),
                    totalGB = Math.Round(d.TotalSize / GB, 1),
                    freeGB = Math.Round(d.AvailableFreeSpace / GB, 1),
                    readBps = read,
                    writeBps = write,
                    busy,
                });
            }
            catch { }
        }
        return list;
    }

    // ---- Network ------------------------------------------------------------

    object ReadNet()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var dt = lastNetTicks == 0 ? 0 : Stopwatch.GetElapsedTime(lastNetTicks, nowTicks).TotalSeconds;
        lastNetTicks = nowTicks;

        var adapters = new List<object>();
        double rxSum = 0, txSum = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                // "Ethernet 5-Npcap Packet Driver (NPCAP)-0000" etc. are filter-driver bindings that repeat the real adapter's counters.
                if (System.Text.RegularExpressions.Regex.IsMatch(n.Name, @"-\d{4}$")) continue;
                var desc = (n.Name + " " + n.Description).ToLowerInvariant();
                // VPN/overlay traffic also crosses the physical adapter, so it's listed but not added to the totals.
                bool isVirtual = n.NetworkInterfaceType == NetworkInterfaceType.Ppp
                    || new[] { "vpn", "nord", "wireguard", "wintun", "zerotier", "tailscale", "hyper-v", "virtual", "vmware", "tap-", "wi-fi direct", "miniport", "bluetooth" }.Any(desc.Contains);
                var st = n.GetIPStatistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                double rxBps = 0, txBps = 0;
                if (dt > 0 && lastNet.TryGetValue(n.Id, out var prev))
                {
                    rxBps = Math.Max(0, (rx - prev.rx) / dt);
                    txBps = Math.Max(0, (tx - prev.tx) / dt);
                }
                lastNet[n.Id] = (rx, tx);
                if (!isVirtual) { rxSum += rxBps; txSum += txBps; }

                if (!ipCache.TryGetValue(n.Id, out var ipc) || (DateTime.UtcNow - ipc.at).TotalSeconds > 15)
                {
                    var ips = n.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()).ToArray();
                    ipc = (ips, DateTime.UtcNow);
                    ipCache[n.Id] = ipc;
                }

                adapters.Add(new
                {
                    name = n.Name,
                    description = n.Description,
                    type = n.NetworkInterfaceType.ToString(),
                    isVirtual,
                    speedMbps = n.Speed > 0 ? n.Speed / 1_000_000 : 0,
                    ipv4 = ipc.ips,
                    rxBps = Math.Round(rxBps),
                    txBps = Math.Round(txBps),
                    rxTotal = rx,
                    txTotal = tx,
                });
            }
            catch { }
        }
        return new { rxBps = Math.Round(rxSum), txBps = Math.Round(txSum), adapters };
    }

    // ---- Processes ----------------------------------------------------------

    /// <summary>Full process list, refreshed every ProcIntervalMs; null on ticks in between.</summary>
    object? ReadProcs()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        if (lastProcTicks != 0 && Stopwatch.GetElapsedTime(lastProcTicks, nowTicks).TotalMilliseconds < ProcIntervalMs - 50)
            return null;
        var dt = lastProcTicks == 0 ? 0 : Stopwatch.GetElapsedTime(lastProcTicks, nowTicks).TotalSeconds;
        lastProcTicks = nowTicks;

        if (Environment.TickCount64 - lastDeniedReset > 60_000) { deniedPids.Clear(); lastDeniedReset = Environment.TickCount64; }

        var list = new List<object>();
        var seen = new HashSet<int>();
        int threads = 0;
        var gpuByPid = gpuEngines.Sample();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    seen.Add(p.Id);
                    double? cpu = null;
                    if (!deniedPids.Contains(p.Id))
                    {
                        try
                        {
                            var t = p.TotalProcessorTime;
                            if (dt > 0 && lastProcCpu.TryGetValue(p.Id, out var prev))
                                cpu = Math.Round(Math.Clamp((t - prev).TotalSeconds / dt / Environment.ProcessorCount * 100, 0, 100), 1);
                            lastProcCpu[p.Id] = t;
                        }
                        catch { deniedPids.Add(p.Id); }
                    }
                    int th = 0;
                    try { th = p.Threads.Count; threads += th; } catch { }
                    list.Add(new
                    {
                        pid = p.Id,
                        name = p.ProcessName,
                        cpu,
                        gpu = gpuByPid.TryGetValue(p.Id, out var g) ? g : (double?)null,
                        memMB = Math.Round(p.WorkingSet64 / 1048576d, 1),
                        threads = th,
                        title = string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle,
                    });
                }
                catch { }
            }
        }
        foreach (var gone in lastProcCpu.Keys.Where(k => !seen.Contains(k)).ToList()) lastProcCpu.Remove(gone);

        lastProcs = new { count = list.Count, threads, list };
        return lastProcs;
    }

    static object? ReadBattery()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)) return null;
        return new
        {
            pct = Math.Round(ps.BatteryLifePercent * 100),
            charging = ps.PowerLineStatus == PowerLineStatus.Online,
            secondsLeft = ps.BatteryLifeRemaining >= 0 ? ps.BatteryLifeRemaining : (int?)null,
        };
    }

    // ---- One-off queries (called from the UI) --------------------------------

    public static object ProcessDetails(int pid)
    {
        using var p = Process.GetProcessById(pid);
        string? path = null, started = null, priority = null;
        try { path = p.MainModule?.FileName; } catch { }
        try { started = p.StartTime.ToString("o"); } catch { }
        try { priority = p.PriorityClass.ToString(); } catch { }
        return new
        {
            pid = p.Id,
            name = p.ProcessName,
            path,
            started,
            priority,
            threads = p.Threads.Count,
            handles = SafeInt(() => p.HandleCount),
            memMB = Math.Round(p.WorkingSet64 / 1048576d, 1),
            privateMB = Math.Round(p.PrivateMemorySize64 / 1048576d, 1),
            title = p.MainWindowTitle,
        };
    }

    public static object NetConnections()
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        return new
        {
            tcp = props.GetActiveTcpConnections().Take(500).Select(c => new
            {
                local = c.LocalEndPoint.ToString(),
                remote = c.RemoteEndPoint.ToString(),
                state = c.State.ToString(),
            }).ToArray(),
            listeners = props.GetActiveTcpListeners().Select(e => e.ToString()).Distinct().Take(200).ToArray(),
        };
    }

    static int? SafeInt(Func<int> f) { try { return f(); } catch { return null; } }

    static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    static double ReadCpuMHz()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Convert.ToDouble(k?.GetValue("~MHz") ?? 0);
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        stop.Set();
        thread.Join(2000);
        try { lhm?.Close(); } catch { }
        cpuTotal?.Dispose(); cpuPerf?.Dispose();
        foreach (var c in cores) c.Dispose();
        foreach (var c in diskCounters.Values)
            if (c is { } cc) { cc.read.Dispose(); cc.write.Dispose(); cc.idle.Dispose(); }
    }
}
