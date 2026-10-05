using System.Diagnostics;
using System.Text;

namespace WinTop;

public static class Program
{
    public static string BaseDir => AppContext.BaseDirectory;

    public static int Main(string[] args)
    {
        string configPath = Path.Combine(BaseDir, "config.json");
        for (int i = 0; i < args.Length; i++)
            if ((args[i] is "-c" or "--config") && i + 1 < args.Length) configPath = Path.GetFullPath(args[++i]);

        if (args.Contains("-h") || args.Contains("--help") || args.Contains("/?"))
        {
            Console.WriteLine("""
                wintop - Windows system monitor

                Usage: wintop [options]
                  -c, --config <file>   use a different config file (default: config.json next to the exe)
                  --dump                print all detected metrics and sensors once, then exit
                  --themes              list available themes
                  -h, --help            this help

                Keys: q/Esc quit, s sort, r reverse sort, t next theme, n next network adapter,
                      +/- update interval, a restart as administrator, F5 reload config/theme, ? help
                """);
            return 0;
        }

        var cfg = Config.Load(configPath);
        ThemeStore.EnsureDefaults(Path.Combine(BaseDir, "theme"));

        if (args.Contains("--themes"))
        {
            foreach (var t in ThemeStore.List(Path.Combine(BaseDir, "theme"))) Console.WriteLine(t);
            return 0;
        }

        if (args.Contains("--dump")) return Dump(cfg);

        int fi = Array.IndexOf(args, "--frame");
        if (fi >= 0 && fi + 1 < args.Length)
        {
            // Debug: render one frame of the given size to stdout (add --ansi for colors).
            var wh = args[fi + 1].Split('x');
            Console.OutputEncoding = new UTF8Encoding(false);
            using var a = new App(cfg);
            int ki = Array.IndexOf(args, "--keys");
            string keys = ki >= 0 && ki + 1 < args.Length ? args[ki + 1] : "";
            Console.Write(a.Frame(int.Parse(wh[0]), int.Parse(wh[1]), args.Contains("--ansi"), 3, keys));
            return 0;
        }

        if (cfg.RequireAdmin && !Native.IsElevated() && !args.Contains("--no-elevate"))
        {
            if (RelaunchElevated(args)) return 0;
        }

        using var app = new App(cfg);
        return app.Run();
    }

    public static bool RelaunchElevated(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            string argStr = string.Join(" ", args.Append("--no-elevate").Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            // Elevated console apps open in the legacy console host. When we were started from
            // Windows Terminal, open an elevated Terminal window instead so the experience matches.
            bool inTerminal = Environment.GetEnvironmentVariable("WT_SESSION") != null;
            var psi = new ProcessStartInfo(inTerminal ? "wt.exe" : exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
                Arguments = inTerminal ? $"-d \"{Environment.CurrentDirectory}\" \"{exe}\" {argStr}" : argStr,
            };
            try
            {
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (inTerminal && ex.NativeErrorCode != 1223)
            {
                // wt.exe unavailable: fall back to launching the exe directly.
                psi.FileName = exe;
                psi.Arguments = argStr;
                Process.Start(psi);
            }
            return true;
        }
        catch
        {
            return false; // UAC cancelled (1223) or launch failed
        }
    }

    static int Dump(Config cfg)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var sampler = new Sampler(cfg);
        sampler.Hardware.OpenAsync(cfg.Hardware);
        var sw = Stopwatch.StartNew();
        while (!sampler.Hardware.Ready && sw.ElapsedMilliseconds < 15000 && !sampler.Hardware.Status.StartsWith("sensors unavailable"))
            Thread.Sleep(100);
        Console.WriteLine($"# sensor init: {sw.ElapsedMilliseconds} ms, ready={sampler.Hardware.Ready} {sampler.Hardware.Status}");
        sampler.SampleNow();
        Thread.Sleep(1000);
        var s = sampler.SampleNow();

        Console.WriteLine($"elevated: {s.Elevated}   sample time: {s.SampleMs:0} ms");
        Console.WriteLine($"CPU: {s.CpuName}  cores {s.PhysicalCores}/{s.LogicalCores}  load {s.CpuLoad:0.0}%  temp {Fmt(s.CpuTemp)} ({s.CpuTempSource})  power {Fmt(s.CpuPower)} W");
        Console.WriteLine("  thread loads: " + string.Join(" ", s.ThreadLoads.Select(v => v.ToString("0"))));
        Console.WriteLine("  core temps:   " + string.Join(" ", s.CoreTemps.Select(Fmt)));
        Console.WriteLine("  core clocks:  " + string.Join(" ", s.CoreClocks.Select(Fmt)));
        Console.WriteLine($"MEM: used {s.MemUsed >> 20} / {s.MemTotal >> 20} MiB, cached {s.MemCached >> 20}, swap {s.SwapUsed >> 20}/{s.SwapTotal >> 20} MiB");
        foreach (var g in s.Gpus)
            Console.WriteLine($"GPU: {g.Name} load {Fmt(g.Load)} temp {Fmt(g.Temp)} hot {Fmt(g.HotSpot)} vram {Fmt(g.VramUsedMb)}/{Fmt(g.VramTotalMb)} clk {Fmt(g.CoreClock)} pwr {Fmt(g.Power)} fan {Fmt(g.FanRpm)} rpm {Fmt(g.FanPct)}%");
        foreach (var d in s.Disks)
            Console.WriteLine($"DISK {d.Letter} [{d.Label}] {d.Format} used {d.UsedPct:0.0}% of {d.Total >> 30} GiB  R {d.ReadBps:0} W {d.WriteBps:0} B/s  active {Fmt(d.ActivePct)}%");
        foreach (var n in s.Nets)
            Console.WriteLine($"NET {n.Name} ({n.Type}, {n.Description}) default={n.IsDefault} ip={n.Ip} rx {n.RxBps:0} tx {n.TxBps:0} B/s");
        if (s.Wifi is { } w)
            Console.WriteLine($"WIFI {w.Interface} connected={w.Connected} ssid='{w.Ssid}' signal={w.SignalPct}% rssi={w.Rssi} dBm {w.Phy} ch {w.Channel} rx {w.RxMbps} tx {w.TxMbps} Mbps {w.Error}");
        foreach (var f in s.Fans) Console.WriteLine($"FAN  {f.Group}/{f.Name}: {f.Value:0} rpm");
        foreach (var t in s.Temps) Console.WriteLine($"TEMP {t.Group}/{t.Name}: {t.Value:0.0} C");
        Console.WriteLine($"SYS uptime {s.Uptime}, procs {s.ProcessCount}, threads {s.ThreadCount}, handles {s.HandleCount}, battery {s.BatteryPct?.ToString() ?? "none"}");
        foreach (var p in s.Procs.OrderByDescending(p => p.Cpu).Take(5))
            Console.WriteLine($"PROC {p.Pid,6} {p.Name,-28} cpu {p.Cpu,5:0.0}% mem {p.MemBytes >> 20} MiB thr {p.Threads}");
        Console.WriteLine();
        Console.WriteLine("# raw sensors");
        foreach (var l in sampler.Hardware.DumpLines()) Console.WriteLine(l);
        return 0;
    }

    static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.#") : "-";
}
