using System.Diagnostics;
using System.Text;

namespace WinTop;

readonly record struct Rect(int X, int Y, int W, int H);

public sealed class App : IDisposable
{
    readonly Config _cfg;
    readonly Sampler _sampler;
    readonly Screen _scr = new();
    readonly string _themeDir = Path.Combine(Program.BaseDir, "theme");
    Theme _theme;
    Stream _out = Stream.Null;
    uint _origOutMode, _origCp;
    bool _restored;

    // State
    Snapshot? _snap;
    readonly Ring _cpuHist = new();
    readonly Dictionary<string, (Ring rx, Ring tx)> _netHist = new();
    readonly Dictionary<int, Ring> _gpuHist = new();
    readonly Dictionary<string, double> _fanMax = new();
    double _energyWh;
    double _powerPeak;
    DateTime? _lastPowerTime;
    readonly DateTime _energyStart = DateTime.Now;
    int _selPid = -1;
    int _selIndex;
    int _procScroll;
    bool _showHelp;
    int? _confirmKillPid;
    long _confirmKillCreate; // creation time of the process the dialog asked about (0 = unknown)
    enum Overlay { None, Ports, Procs }
    Overlay _overlay;
    int _ovSel, _ovScroll, _ovPage = 10;
    string? _ovSelKey;
    string _confirmKillName = "";
    string _msg = "";
    DateTime _msgUntil;
    volatile bool _quit;
    readonly AutoResetEvent _wake = new(false);
    bool _pawnIoMissing;

    static readonly string[] SortKeys = { "cpu", "gpu", "mem", "pid", "name", "threads" };

    public App(Config cfg)
    {
        _cfg = cfg;
        _theme = ThemeStore.Load(_themeDir, cfg.Theme, out var err);
        if (err != null) Flash(err);
        if (cfg.LoadError != null) Flash(cfg.LoadError);
        _sampler = new Sampler(cfg);
        _sampler.Updated += () => _wake.Set();
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            _pawnIoMissing = k == null;
        }
        catch { }
    }

    void Flash(string m, int seconds = 6)
    {
        _msg = m;
        _msgUntil = DateTime.Now.AddSeconds(seconds);
    }

    // ------------------------------------------------------------------ console setup

    void SetupConsole()
    {
        var hOut = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        if (Native.GetConsoleMode(hOut, out _origOutMode))
            Native.SetConsoleMode(hOut, _origOutMode | Native.ENABLE_PROCESSED_OUTPUT | Native.ENABLE_VIRTUAL_TERMINAL_PROCESSING | Native.DISABLE_NEWLINE_AUTO_RETURN);
        _origCp = Native.GetConsoleOutputCP();
        Native.SetConsoleOutputCP(65001);
        Console.OutputEncoding = new UTF8Encoding(false);
        try { Console.TreatControlCAsInput = true; } catch { }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _quit = true; _wake.Set(); };
        try { Console.Title = "wintop" + (Native.IsElevated() ? " (admin)" : ""); } catch { }
        Native.SetConsoleIcon();
        _out = Console.OpenStandardOutput();
        Write("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[2J");
    }

    void RestoreConsole()
    {
        if (_restored) return;
        _restored = true;
        Write("\x1b[0m\x1b[?7h\x1b[?25h\x1b[?1049l");
        _out.Flush();
        var hOut = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        if (_origOutMode != 0) Native.SetConsoleMode(hOut, _origOutMode);
        if (_origCp != 0) Native.SetConsoleOutputCP(_origCp);
    }

    void Write(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        _out.Write(bytes, 0, bytes.Length);
        _out.Flush();
    }

    // ------------------------------------------------------------------ main loop

    public int Run()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            Console.Error.WriteLine("wintop needs an interactive console. Use --dump for text output.");
            return 1;
        }
        SetupConsole();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreConsole();
        try
        {
            _sampler.Start();
            while (!_quit)
            {
                var latest = _sampler.Latest;
                if (latest != null && !ReferenceEquals(latest, _snap))
                {
                    _snap = latest;
                    PushHistory(latest);
                }
                Render();
                // Wait for next sample, a key, or a resize.
                int w = Console.WindowWidth, h = Console.WindowHeight;
                while (!_quit)
                {
                    if (Console.KeyAvailable) { HandleKey(Console.ReadKey(true)); break; }
                    if (Console.WindowWidth != w || Console.WindowHeight != h) break;
                    if (_wake.WaitOne(30)) break;
                    if (_msg.Length > 0 && DateTime.Now > _msgUntil) { _msg = ""; break; }
                }
            }
        }
        finally
        {
            RestoreConsole();
        }
        return 0;
    }

    void PushHistory(Snapshot s)
    {
        _cpuHist.Push(s.CpuLoad);
        if (MeasuredPower(s) is double watts)
        {
            _powerPeak = Math.Max(_powerPeak, watts);
            // Integrate W over time into Wh; cap the step so a stall or sleep doesn't add a huge chunk.
            if (_lastPowerTime is DateTime last)
                _energyWh += watts * Math.Min((s.Time - last).TotalHours, 10.0 / 3600);
            _lastPowerTime = s.Time;
        }
        foreach (var n in s.Nets)
        {
            if (!_netHist.TryGetValue(n.Id, out var h)) _netHist[n.Id] = h = (new Ring(), new Ring());
            h.rx.Push(n.RxBps);
            h.tx.Push(n.TxBps);
        }
        for (int i = 0; i < s.Gpus.Count; i++)
        {
            if (!_gpuHist.TryGetValue(i, out var r)) _gpuHist[i] = r = new Ring();
            r.Push(s.Gpus[i].Load ?? 0);
        }
        foreach (var f in s.Fans)
        {
            var key = f.Group + "/" + f.Name;
            _fanMax[key] = Math.Max(_fanMax.GetValueOrDefault(key), f.Value);
        }
    }

    // ------------------------------------------------------------------ input

    void HandleKey(ConsoleKeyInfo k)
    {
        if (_confirmKillPid is int kp)
        {
            _confirmKillPid = null;
            if (k.KeyChar is 'y' or 'Y')
            {
                string? err = Native.KillProcess(kp, _confirmKillCreate);
                Flash(err == null ? $"killed {kp} {_confirmKillName}" : $"kill {kp} failed: {err}");
            }
            return;
        }
        if (_showHelp && k.Key != ConsoleKey.F5) { _showHelp = false; return; }
        if (_overlay != Overlay.None && HandleOverlayKey(k)) return;

        bool ctrlC = k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (k.Key)
        {
            case ConsoleKey.Escape:
            case ConsoleKey.Q when !ctrlC:
                _quit = true;
                return;
            case ConsoleKey.UpArrow: MoveSel(-1); return;
            case ConsoleKey.DownArrow: MoveSel(1); return;
            case ConsoleKey.PageUp: MoveSel(-10); return;
            case ConsoleKey.PageDown: MoveSel(10); return;
            case ConsoleKey.Home: MoveSel(-100000); return;
            case ConsoleKey.End: MoveSel(100000); return;
            case ConsoleKey.F5: Reload(); return;
        }
        if (ctrlC) { _quit = true; return; }

        switch (k.KeyChar)
        {
            case '?':
            case 'h':
                _showHelp = true;
                break;
            case 's':
                _cfg.ProcSort = SortKeys[(Array.IndexOf(SortKeys, _cfg.ProcSort) + 1) % SortKeys.Length];
                Save();
                break;
            case 'r':
                _cfg.ProcReversed = !_cfg.ProcReversed;
                Save();
                break;
            case 't':
                NextTheme();
                break;
            case 'n':
                NextNet();
                break;
            case 'g':
                _cfg.GraphSymbol = _cfg.GraphSymbol switch { "braille" => "block", "block" => "tty", _ => "braille" };
                Save();
                Flash("graph symbol: " + _cfg.GraphSymbol, 2);
                break;
            case 'f':
                _cfg.TempUnit = _cfg.TempUnit == "C" ? "F" : "C";
                Save();
                break;
            case '+':
            case '=': // same physical key without Shift
                _cfg.UpdateMs = Math.Min(10000, _cfg.UpdateMs + 250);
                Save();
                Flash($"update interval {_cfg.UpdateMs} ms", 2);
                break;
            case '-':
            case '_': // same physical key with Shift
                _cfg.UpdateMs = Math.Max(250, _cfg.UpdateMs - 250);
                Save();
                Flash($"update interval {_cfg.UpdateMs} ms", 2);
                break;
            case 'a':
                if (Native.IsElevated()) Flash("already running as administrator", 3);
                else if (Program.RelaunchElevated(Array.Empty<string>())) _quit = true;
                else Flash("elevation cancelled", 3);
                break;
            case 'o':
                OpenOverlay(Overlay.Ports);
                break;
            case 'l':
                OpenOverlay(Overlay.Procs);
                break;
            case 'w':
                if (_snap?.Procs.FirstOrDefault(p => p.Pid == _selPid) is { } wp) WebSearch(WithApp(wp.Name, wp.App), null);
                break;
            case 'p':
                _cfg.PortsSort = _cfg.PortsSort == "port" ? "process" : "port";
                Save();
                break;
            case 'k':
                var sel = _snap?.Procs.FirstOrDefault(p => p.Pid == _selPid);
                if (sel != null)
                {
                    _confirmKillPid = sel.Pid;
                    _confirmKillCreate = sel.CreateTime;
                    _confirmKillName = WithApp(sel.Name, sel.App);
                }
                break;
        }
    }

    void Save()
    {
        if (!_cfg.Save()) Flash("config.json has errors: change not saved (fix it and press F5)", 4);
    }

    void MoveSel(int d)
    {
        _selIndex = Math.Max(0, _selIndex + d);
        _selPid = -1; // re-resolved from index at next draw
    }

    void Reload()
    {
        var fresh = Config.Load(_cfg.Path);
        _cfg.Theme = fresh.Theme;
        _cfg.UpdateMs = fresh.UpdateMs;
        _cfg.GraphSymbol = fresh.GraphSymbol;
        _cfg.TempUnit = fresh.TempUnit;
        _cfg.Boxes = fresh.Boxes;
        _cfg.RoundedCorners = fresh.RoundedCorners;
        _cfg.ThemeBackground = fresh.ThemeBackground;
        _cfg.ProcSort = fresh.ProcSort;
        _cfg.ProcReversed = fresh.ProcReversed;
        _cfg.ProcPerCore = fresh.ProcPerCore;
        _cfg.NetInterface = fresh.NetInterface;
        _cfg.NetBits = fresh.NetBits;
        _cfg.DiskHide = fresh.DiskHide;
        _cfg.HideIdleFans = fresh.HideIdleFans;
        _cfg.PortsShowUdp = fresh.PortsShowUdp;
        _cfg.PortsSort = fresh.PortsSort;
        _theme = ThemeStore.Load(_themeDir, _cfg.Theme, out var err);
        _scr.Invalidate();
        Flash(fresh.LoadError ?? err ?? $"reloaded config and theme '{_cfg.Theme}'", 3);
    }

    void NextTheme()
    {
        var list = ThemeStore.List(_themeDir);
        if (list.Count == 0) return;
        int i = list.FindIndex(t => string.Equals(t, _cfg.Theme, StringComparison.OrdinalIgnoreCase));
        _cfg.Theme = list[(i + 1) % list.Count];
        _theme = ThemeStore.Load(_themeDir, _cfg.Theme, out var err);
        Save();
        _scr.Invalidate();
        Flash(err ?? $"theme: {_cfg.Theme}", 2);
    }

    void NextNet()
    {
        var nets = _snap?.Nets;
        if (nets == null || nets.Count == 0) return;
        int i = nets.FindIndex(n => n.Name == SelectedNet(_snap!)?.Name);
        _cfg.NetInterface = nets[(i + 1) % nets.Count].Name;
        Save();
    }

    // ------------------------------------------------------------------ rendering

    Rgb C(string key) => _theme[key];

    void Render()
    {
        int w = Math.Max(1, Console.WindowWidth), h = Math.Max(1, Console.WindowHeight);
        if (Environment.GetEnvironmentVariable("WINTOP_DEBUG") is { Length: > 0 } dbg)
            try { File.AppendAllText(dbg, $"{DateTime.Now:HH:mm:ss.fff} win {w}x{h} buf {Console.BufferWidth}x{Console.BufferHeight} left {Console.WindowLeft} top {Console.WindowTop}{Environment.NewLine}"); } catch { }
        Write(Compose(w, h));
    }

    /// <summary>Debug: sample for a few seconds and return one frame (plain text or ANSI) without touching the console.</summary>
    public string Frame(int w, int h, bool ansi, int samples, string keys = "")
    {
        _sampler.Start();
        var sw = Stopwatch.StartNew();
        while (!_sampler.Hardware.Ready && sw.ElapsedMilliseconds < 20000) Thread.Sleep(100);
        int got = 0;
        while (got < samples)
        {
            _wake.WaitOne(5000);
            var latest = _sampler.Latest;
            if (latest != null && !ReferenceEquals(latest, _snap)) { _snap = latest; PushHistory(latest); got++; }
        }
        foreach (char c in keys)
        {
            var key = c switch { 'D' => ConsoleKey.DownArrow, 'U' => ConsoleKey.UpArrow, '5' => ConsoleKey.F5, _ => (ConsoleKey)char.ToUpperInvariant(c) };
            HandleKey(new ConsoleKeyInfo(c is 'D' or 'U' or '5' ? (char)0 : c, key, false, false, false));
            Compose(w, h);
        }
        _scr.Invalidate(); // full frame, not a diff against the key presses above
        var vt = Compose(w, h);
        return ansi ? vt : _scr.PlainText();
    }

    string Compose(int w, int h)
    {
        _scr.Resize(w, h);
        _scr.DefaultBg = _cfg.ThemeBackground ? _theme.Opt("main_bg") : null;
        _scr.DefaultFg = C("main_fg");
        _scr.Clear();

        if (w < 60 || h < 16)
        {
            _scr.Text(1, 0, $"Terminal too small: {w}x{h}", C("hi_fg"));
            _scr.Text(1, 1, "Need at least 60x16", C("main_fg"));
        }
        else
        {
            _hideHints = _overlay != Overlay.None;
            Layout(w, h);
            _hideHints = false;
        }

        if (_overlay != Overlay.None)
        {
            // Fade the main screen so only the overlay and its own key hints stand out.
            _scr.Dim(_theme.Opt("main_bg") ?? new Rgb(0, 0, 0), 0.3);
            DrawOverlay();
        }
        if (_showHelp) DrawHelp();
        if (_confirmKillPid is int kp) DrawDialog($" Kill process {kp} ({_confirmKillName})? [y/N] ");
        if (_msg.Length > 0) DrawMessage();
        return _scr.Render();
    }

    bool Has(string box) => _cfg.Boxes.Contains(box);

    void Layout(int w, int h)
    {
        var s = _snap;
        int y = 0;
        if (Has("cpu"))
        {
            int cpuH = Math.Clamp(h * 30 / 100, 7, 16);
            int threads = s?.ThreadLoads.Length ?? Environment.ProcessorCount;
            // grow a bit so cores fit in at most 3 columns
            cpuH = Math.Clamp(Math.Max(cpuH, (threads + 2) / 3 + 4), 7, Math.Max(7, h / 2));
            DrawCpu(new Rect(0, 0, w, cpuH), s);
            y = cpuH;
        }
        int rest = h - y;
        if (rest < 3) return;

        // Grid below the CPU box:
        //   mem   | gpu
        //   disk  | power
        //   net   | sensors   (power + sensors height = disk + net)
        //   ports | proc      (gets all remaining height)
        bool anyLeft = new[] { "mem", "disk", "net", "ports" }.Any(Has);
        bool anyRight = new[] { "gpu", "power", "sensors", "proc" }.Any(Has);
        int leftW = !anyRight ? w : !anyLeft ? 0 : Math.Clamp(w * 45 / 100, 40, w - 40);
        int rightW = w - leftW;

        int rowA = Math.Max(Has("mem") ? Preferred("mem", s, leftW, rest) : 0, Has("gpu") ? Preferred("gpu", s, rightW, rest) : 0);
        int disk = Has("disk") ? Preferred("disk", s, leftW, rest) : 0;
        int netBase = Has("net") ? 3 + (s?.Wifi != null ? 1 : 0) : 0; // borders + rate line (+ Wi-Fi line)
        int powerH = Has("power") ? Preferred("power", s, rightW, rest) : 0;
        int sensorsPref = powerH + (Has("sensors") ? Preferred("sensors", s, rightW, rest) : 0);
        bool rowCShown = Has("ports") || Has("proc");

        // Net graph: k rows for download and k rows for upload, 3..5 normally.
        int k = 3;
        int NetH(int kk) => Has("net") ? netBase + 2 * kk : 0;
        int RowB(int kk) => disk + NetH(kk) > 0 ? disk + NetH(kk) : sensorsPref;
        int rowC = rest - rowA - RowB(k);
        if (rowCShown)
        {
            // Grow the net graph only while ports/proc already have plenty of room; otherwise they get the space.
            while (k < 5 && rowC - 2 >= 16) { k++; rowC -= 2; }
            // Tight window: shrink the net graph, then the top row.
            while (rowC < 8 && k > 1) { k--; rowC += 2; }
            while (rowC < 8 && rowA > 5) { rowA--; rowC++; }
        }
        else
        {
            // Nothing below: let the left stack (net) take the rest.
            int extra = Math.Max(0, rest - rowA - RowB(k));
            if (Has("net")) k += extra / 2;
            rowC = 0;
        }
        int netH = NetH(k);
        int rowB = RowB(k);

        Pair("mem", "gpu", y, rowA, leftW, w);
        y += rowA;

        if (rowB > 0)
        {
            bool leftStack = Has("disk") || Has("net");
            bool rightStack = Has("power") || Has("sensors");
            int lw = !rightStack ? w : leftStack ? leftW : 0;
            if (leftStack)
            {
                if (Has("disk")) Place("disk", new Rect(0, y, lw, disk), h);
                if (Has("net")) Place("net", new Rect(0, y + disk, lw, netH), h);
            }
            if (Has("power"))
            {
                int ph = Has("sensors") ? Math.Min(powerH, rowB) : rowB;
                Place("power", new Rect(lw, y, w - lw, ph), h);
                if (Has("sensors")) Place("sensors", new Rect(lw, y + ph, w - lw, rowB - ph), h);
            }
            else if (Has("sensors")) Place("sensors", new Rect(lw, y, w - lw, rowB), h);
            y += rowB;
        }

        if (rowCShown) Pair("ports", "proc", y, h - y, leftW, w);
    }

    /// <summary>Draws a left/right pair on one row; a lone box takes the full width.</summary>
    void Pair(string l, string r, int y, int height, int leftW, int w)
    {
        if (height <= 0) return;
        bool hl = Has(l), hr = Has(r);
        if (hl && hr)
        {
            Place(l, new Rect(0, y, leftW, height), y + height);
            Place(r, new Rect(leftW, y, w - leftW, height), y + height);
        }
        else if (hl) Place(l, new Rect(0, y, w, height), y + height);
        else if (hr) Place(r, new Rect(0, y, w, height), y + height);
    }

    /// <summary>Draws a box, clipped to the screen; boxes with fewer than 3 rows are skipped.</summary>
    void Place(string box, Rect rect, int maxY)
    {
        int bh = Math.Min(rect.H, Math.Min(maxY, _scr.H) - rect.Y);
        if (bh < 3 || rect.W < 10) return;
        var r = rect with { H = bh };
        var s = _snap;
        switch (box)
        {
            case "mem": DrawMem(r, s); break;
            case "gpu": DrawGpu(r, s); break;
            case "disk": DrawDisk(r, s); break;
            case "net": DrawNet(r, s); break;
            case "ports": DrawPorts(r, s); break;
            case "power": DrawPower(r, s); break;
            case "sensors": DrawSensors(r, s); break;
            case "proc": DrawProc(r, s); break;
        }
    }

    int Preferred(string box, Snapshot? s, int w, int h) => box switch
    {
        "mem" => 7,
        "gpu" => 2 + Math.Max(1, s?.Gpus.Count ?? 1) * 3 + 1, // +1: top GPU processes line
        "disk" => 2 + Math.Max(1, s?.Disks.Count ?? 1) * (w - 4 >= 62 ? 1 : 2),
        "net" => 10,
        "ports" => Math.Clamp(3 + VisiblePorts(s).Count, 5, Math.Max(8, h / 3)),
        "power" => 4,
        "sensors" => Math.Clamp(2 + SensorRows(s, w), 4, Math.Max(4, h * 45 / 100)),
        "proc" => 10,
        _ => 6,
    };

    // ------------------------------------------------------------------ boxes

    bool _hideHints; // set while drawing the boxes behind an open overlay

    void Box(Rect r, string colorKey, string title, string? right = null, string? bottom = null)
    {
        var line = C(colorKey);
        bool round = _cfg.RoundedCorners;
        char tl = round ? '╭' : '┌', tr = round ? '╮' : '┐', bl = round ? '╰' : '└', br = round ? '╯' : '┘';
        int x2 = r.X + r.W - 1, y2 = r.Y + r.H - 1;
        for (int x = r.X + 1; x < x2; x++)
        {
            _scr.Put(x, r.Y, '─', line);
            _scr.Put(x, y2, '─', line);
        }
        for (int y = r.Y + 1; y < y2; y++)
        {
            _scr.Put(r.X, y, '│', line);
            _scr.Put(x2, y, '│', line);
        }
        _scr.Put(r.X, r.Y, tl, line);
        _scr.Put(x2, r.Y, tr, line);
        _scr.Put(r.X, y2, bl, line);
        _scr.Put(x2, y2, br, line);

        int tx = r.X + 2;
        _scr.Put(tx - 1, r.Y, '┤', line);
        tx = _scr.Text(tx, r.Y, title, C("title"), r.W - 6, bold: true);
        _scr.Put(tx, r.Y, '├', line);
        if (right != null && right.Length + tx + 4 < x2)
        {
            int rx = x2 - 2 - right.Length;
            _scr.Put(rx - 1, r.Y, '┤', line);
            _scr.Text(rx, r.Y, right, C("graph_text"));
            _scr.Put(rx + right.Length, r.Y, '├', line);
        }
        if (bottom != null && !_hideHints && bottom.Length + 6 < r.W)
        {
            _scr.Put(r.X + 1, y2, '┤', line);
            _scr.Text(r.X + 2, y2, bottom, C("graph_text"), r.W - 6);
            _scr.Put(r.X + 2 + bottom.Length, y2, '├', line);
        }
    }

    void Waiting(Rect r)
    {
        _scr.Text(r.X + 2, r.Y + 1, "collecting...", C("inactive_fg"));
    }

    // ---- CPU

    void DrawCpu(Rect r, Snapshot? s)
    {
        string right = DateTime.Now.ToString("HH:mm:ss") + $" ─ {_cfg.UpdateMs}ms";
        string bottom = "";
        if (s != null)
        {
            var parts = new List<string> { "up " + Uptime(s.Uptime), $"{s.ProcessCount} procs", $"{s.ThreadCount} thr" };
            if (s.BatteryPct is int bp) parts.Add($"bat {bp}%{(s.OnAc == true ? "+" : "")}");
            parts.Add(s.Elevated ? "admin" : "user (a: elevate)");
            bottom = string.Join(" ─ ", parts);
        }
        Box(r, "cpu_box", s != null && s.CpuName.Length > 0 ? "cpu ─ " + s.CpuName : "cpu", right, bottom);
        if (s == null) { Waiting(r); return; }

        int ix = r.X + 1, iy = r.Y + 1, iw = r.W - 2, ih = r.H - 2;
        int n = s.ThreadLoads.Length;
        int tpc = s.PhysicalCores > 0 && n % s.PhysicalCores == 0 ? n / s.PhysicalCores : 1;
        bool coreTemps = s.CoreTemps.Length > 0 && s.CoreTemps.Length * tpc == n;
        bool coreClk = !coreTemps && s.CoreClocks.Length > 0 && s.CoreClocks.Length * tpc == n;

        int maxRows = Math.Max(1, ih - 2);
        int cols = Math.Max(1, (n + maxRows - 1) / maxRows);
        int coreRows = Math.Max(1, (n + cols - 1) / cols); // balance columns
        int labelW = n > 10 ? 4 : 3;
        int miniW = 6;
        int extraW = coreTemps ? 5 : coreClk ? 5 : 0;
        int EntryW() => labelW + miniW + 5 + extraW;
        int PanelW() => cols * EntryW() + (cols - 1) * 2 + 2;
        while (PanelW() > iw * 55 / 100 && miniW > 0) miniW--;
        if (PanelW() > iw * 55 / 100) extraW = 0;
        int panelW = Math.Min(PanelW(), iw - 10);
        int graphW = iw - panelW - 1;

        // History graph
        Draw.Graph(_scr, ix, iy, graphW, ih, _cpuHist, v => v, _theme.Grad("cpu"), _cfg.GraphSymbol);
        _scr.Text(ix + 1, iy, $"{s.CpuLoad,3:0}%", C("graph_text"));
        for (int y = 0; y < ih; y++) _scr.Put(ix + graphW, iy + y, '│', C("div_line"));

        int px = ix + graphW + 2;
        int pw = panelW - 2;
        // Summary line
        var tw = new Tw(_scr, px, iy, px + pw);
        tw.T("CPU ", C("title"), bold: true);
        string tempStr = s.CpuTemp is double ct ? " " + Temp(ct) : "";
        int meterW = pw - 4 - 5 - tempStr.Length;
        Draw.Meter(_scr, tw.X, iy, meterW, s.CpuLoad, _theme.Grad("cpu"), C("meter_bg"));
        tw.X += Math.Max(0, meterW);
        tw.T($"{s.CpuLoad,4:0}%", C("main_fg"));
        if (s.CpuTemp is double t2) tw.T(" " + Temp(t2), _theme.Grad("temp").At(TempPct(t2)));

        // Per-thread entries, column-major
        for (int i = 0; i < n; i++)
        {
            int col = i / coreRows, row = i % coreRows;
            int ex = px + col * (EntryW() + 2);
            int ey = iy + 1 + row;
            if (ey >= iy + ih || ex + EntryW() > px + pw + 1) continue;
            double load = s.ThreadLoads[i];
            var e = new Tw(_scr, ex, ey, ex + EntryW());
            e.T(("C" + i).PadRight(labelW), C("main_fg"));
            if (miniW > 0)
            {
                Draw.Meter(_scr, e.X, ey, miniW, load, _theme.Grad("cpu"), C("meter_bg"));
                e.X += miniW;
            }
            e.T($"{load,4:0}%", _theme.Grad("cpu").At(load));
            if (extraW > 0 && coreTemps && s.CoreTemps[i / tpc] is double ctemp)
                e.T(" " + Temp(ctemp, unit: false).PadLeft(4), _theme.Grad("temp").At(TempPct(ctemp)));
            else if (extraW > 0 && coreClk && s.CoreClocks[i / tpc] is double clk)
                e.T($" {clk / 1000,3:0.0}G", C("graph_text"));
        }

        // Footer line: power, clocks, voltage
        if (ih >= 3)
        {
            var f = new Tw(_scr, px, iy + ih - 1, px + pw);
            if (s.CpuPower is double pw2) { f.T("Pkg ", C("graph_text")); f.T($"{pw2:0.0}W  ", C("main_fg")); }
            var clocks = s.CoreClocks.Where(c => c.HasValue).Select(c => c!.Value).ToList();
            if (clocks.Count > 0) { f.T("Clk ", C("graph_text")); f.T($"{clocks.Average() / 1000:0.00}GHz  ", C("main_fg")); }
            if (s.CpuPower == null && clocks.Count == 0 && !s.Elevated) f.T("temps/power need admin (a)", C("inactive_fg"));
        }
    }

    // ---- MEM

    void DrawMem(Rect r, Snapshot? s)
    {
        Box(r, "mem_box", "mem", s != null ? $"total {Bytes(s.MemTotal)}" : null);
        if (s == null) { Waiting(r); return; }
        int ix = r.X + 2, iw = r.W - 4;
        var rows = new List<(string label, long val, long total, string grad)>
        {
            ("Used", s.MemUsed, s.MemTotal, "used"),
            ("Available", s.MemAvailable, s.MemTotal, "available"),
            ("Cached", s.MemCached, s.MemTotal, "cached"),
            ("Pagefile", s.SwapUsed, s.SwapTotal, "swap"),
            ("Commit", s.CommitTotal, s.CommitLimit, "used"),
        };
        for (int i = 0; i < rows.Count && i < r.H - 2; i++)
        {
            var (label, val, total, grad) = rows[i];
            int y = r.Y + 1 + i;
            double pct = total > 0 ? 100.0 * val / total : 0;
            var tw = new Tw(_scr, ix, y, ix + iw);
            tw.T(label.PadRight(10), C("main_fg"));
            string tail = $"{pct,4:0}% {Bytes(val),6}";
            int mw = iw - 10 - tail.Length - 1;
            Draw.Meter(_scr, tw.X, y, mw, pct, _theme.Grad(grad), C("meter_bg"));
            tw.X += Math.Max(0, mw) + 1;
            tw.T(tail, C("main_fg"));
        }
    }

    // ---- GPU

    void DrawGpu(Rect r, Snapshot? s)
    {
        Box(r, "gpu_box", "gpu");
        if (s == null) { Waiting(r); return; }
        if (s.Gpus.Count == 0)
        {
            _scr.Text(r.X + 2, r.Y + 1, "no GPU sensors found", C("inactive_fg"));
            return;
        }
        int ix = r.X + 2, iw = r.W - 4;
        int y = r.Y + 1, maxY = r.Y + r.H - 1;
        foreach (var (g, idx) in s.Gpus.Select((g, i) => (g, i)))
        {
            if (y >= maxY) break;
            // line 1: name + load meter
            var tw = new Tw(_scr, ix, y, ix + iw);
            string name = ShortGpu(g.Name);
            int nameW = Math.Min(18, iw / 3);
            tw.T(Fit(name, nameW - 1).PadRight(nameW), C("title"), bold: true);
            string load = g.Load is double l ? $"{l,4:0}%" : "   -%";
            int histW = Math.Min(16, Math.Max(0, (iw - tw.X + ix) / 3));
            int mw = ix + iw - tw.X - load.Length - 1 - (histW > 0 ? histW + 1 : 0);
            Draw.Meter(_scr, tw.X, y, mw, g.Load ?? 0, _theme.Grad("gpu"), C("meter_bg"));
            tw.X += Math.Max(0, mw) + 1;
            tw.T(load, C("main_fg"));
            if (histW > 0 && _gpuHist.TryGetValue(idx, out var hist))
                Draw.Graph(_scr, tw.X + 1, y, histW, 1, hist, v => v, _theme.Grad("gpu"), _cfg.GraphSymbol);
            y++;
            if (y >= maxY) break;

            // line 2: VRAM meter
            tw = new Tw(_scr, ix, y, ix + iw);
            tw.T("VRAM".PadRight(nameW), C("main_fg"));
            if (g.VramTotalMb is double vt && vt > 0)
            {
                double vu = g.VramUsedMb ?? 0;
                double pct = 100 * vu / vt;
                string tail = $"{Bytes((long)(vu * 1048576))}/{Bytes((long)(vt * 1048576))}";
                int mw2 = ix + iw - tw.X - tail.Length - 1;
                Draw.Meter(_scr, tw.X, y, mw2, pct, _theme.Grad("used"), C("meter_bg"));
                tw.X += Math.Max(0, mw2) + 1;
                tw.T(tail, C("main_fg"));
            }
            else tw.T("n/a", C("inactive_fg"));
            y++;
            if (y >= maxY) break;

            // line 3: temps, clock, power, fan
            tw = new Tw(_scr, ix, y, ix + iw);
            if (g.Temp is double t) { tw.T("Temp ", C("graph_text")); tw.T(Temp(t) + "  ", _theme.Grad("temp").At(TempPct(t))); }
            if (g.HotSpot is double hs) { tw.T("Hot ", C("graph_text")); tw.T(Temp(hs) + "  ", _theme.Grad("temp").At(TempPct(hs))); }
            if (g.CoreClock is double cc) { tw.T("Clk ", C("graph_text")); tw.T($"{cc:0}MHz  ", C("main_fg")); }
            if (g.Power is double p) { tw.T("Pwr ", C("graph_text")); tw.T($"{p:0}W  ", C("main_fg")); }
            if (g.FanRpm is double fr) { tw.T("Fan ", C("graph_text")); tw.T(fr > 0 ? $"{fr:0}rpm" : "stopped", C("main_fg")); }
            if (g.Temp == null && g.CoreClock == null && g.Power == null) tw.T("sensors n/a", C("inactive_fg"));
            y++;
        }

        // Busiest GPU processes (all adapters).
        if (y < maxY)
        {
            var top = s.Procs.Where(p => p.Gpu >= 0.5).OrderByDescending(p => p.Gpu).Take(4).ToList();
            var tw = new Tw(_scr, ix, y, ix + iw);
            tw.T("Top  ", C("graph_text"));
            if (top.Count == 0) tw.T("no process using the GPU", C("inactive_fg"));
            foreach (var p in top)
            {
                tw.T(Fit(p.App ?? Path.GetFileNameWithoutExtension(p.Name), 16) + " ", C("main_fg"));
                tw.T($"{p.Gpu:0}%  ", _theme.Grad("gpu").At(Math.Min(100, p.Gpu * 2)));
            }
        }
    }

    // ---- DISK

    void DrawDisk(Rect r, Snapshot? s)
    {
        Box(r, "disk_box", "disks");
        if (s == null) { Waiting(r); return; }
        int ix = r.X + 2, iw = r.W - 4;
        int y = r.Y + 1, maxY = r.Y + r.H - 1;
        // letter(3) + meter(>=6) + usage(16) + io(28) + spaces -> one line needs ~58 + label
        bool oneLine = iw >= 62;
        foreach (var d in s.Disks)
        {
            if (y >= maxY) break;
            var tw = new Tw(_scr, ix, y, ix + iw);
            tw.T(d.Letter + " ", C("title"), bold: true);
            int labelW = oneLine ? Math.Clamp(iw - 60, 0, 12) : Math.Max(4, iw / 3);
            if (labelW >= 5) tw.T(Fit(d.Label.Length > 0 ? d.Label : d.Format, labelW).PadRight(labelW + 1), C("graph_text"));
            string usage = $"{d.UsedPct,3:0}% {Bytes(d.Total - d.Free),5}/{Bytes(d.Total),-5}";
            string io = $" R {Rate(d.ReadBps, false),9} W {Rate(d.WriteBps, false),9}";
            string act = d.ActivePct is double a ? $" {a,3:0}%" : "";
            int tailW = usage.Length + (oneLine ? io.Length + act.Length : 0);
            int mw = ix + iw - tw.X - tailW - 1;
            Draw.Meter(_scr, tw.X, y, mw, d.UsedPct, _theme.Grad("disk"), C("meter_bg"));
            tw.X += Math.Max(0, mw) + 1;
            tw.T(usage, C("main_fg"));
            if (oneLine)
            {
                IoText(tw, d);
            }
            else
            {
                y++;
                if (y >= maxY) break;
                var t2 = new Tw(_scr, ix + 3, y, ix + iw);
                IoText(t2, d);
            }
            y++;
        }

        void IoText(Tw tw, DiskInfo d)
        {
            tw.T(" R ", C("graph_text"));
            tw.T($"{Rate(d.ReadBps, false),9}", d.ReadBps > 0 ? _theme.Grad("download").At(100) : C("inactive_fg"));
            tw.T(" W ", C("graph_text"));
            tw.T($"{Rate(d.WriteBps, false),9}", d.WriteBps > 0 ? _theme.Grad("upload").At(100) : C("inactive_fg"));
            if (d.ActivePct is double a)
            {
                tw.T(" ", C("graph_text"));
                tw.T($"{a,3:0}%", _theme.Grad("disk").At(a));
            }
        }
    }

    // ---- NET

    NetInfo? SelectedNet(Snapshot s)
    {
        if (s.Nets.Count == 0) return null;
        if (_cfg.NetInterface.Length > 0)
        {
            var m = s.Nets.FirstOrDefault(n => string.Equals(n.Name, _cfg.NetInterface, StringComparison.OrdinalIgnoreCase));
            if (m != null) return m;
        }
        return s.Nets[0];
    }

    void DrawNet(Rect r, Snapshot? s)
    {
        var n = s != null ? SelectedNet(s) : null;
        Box(r, "net_box", "net", n != null ? $"{Fit(n.Name, 24)} (n)" : null, n?.Ip);
        if (s == null) { Waiting(r); return; }
        if (n == null) { _scr.Text(r.X + 2, r.Y + 1, "no active network adapter", C("inactive_fg")); return; }

        int ix = r.X + 1, iw = r.W - 2;
        int y = r.Y + 1, maxY = r.Y + r.H - 1;
        bool bits = _cfg.NetBits;

        var tw = new Tw(_scr, ix + 1, y, ix + iw - 1);
        tw.T("▼ ", _theme.Grad("download").At(100));
        tw.T($"{Rate(n.RxBps, bits),11}", C("main_fg"));
        tw.T($" ({Bytes(n.RxTotal)})   ", C("graph_text"));
        tw.T("▲ ", _theme.Grad("upload").At(100));
        tw.T($"{Rate(n.TxBps, bits),11}", C("main_fg"));
        tw.T($" ({Bytes(n.TxTotal)})", C("graph_text"));
        y++;

        if (s.Wifi is { } w && y < maxY)
        {
            var t = new Tw(_scr, ix + 1, y, ix + iw - 1);
            if (!w.Connected) t.T("Wi-Fi: disconnected", C("inactive_fg"));
            else
            {
                int sig = w.SignalPct ?? 0;
                t.T("Wi-Fi ", C("graph_text"));
                const char bar = '■';
                int lit = (int)Math.Ceiling(sig / 25.0);
                for (int i = 0; i < 4; i++) t.T(bar.ToString(), i < lit ? _theme.Grad("signal").At(sig) : C("meter_bg"));
                t.T($" {sig}%", _theme.Grad("signal").At(sig));
                if (w.Rssi is int rssi) t.T($" {rssi}dBm", C("main_fg"));
                if (w.Ssid.Length > 0) t.T("  " + w.Ssid, C("title"));
                if (w.Phy.Length > 0) t.T("  " + w.Phy, C("graph_text"));
                if (w.Channel is int ch) t.T($" ch{ch}", C("graph_text"));
                if (w.RxMbps is double rx) t.T($"  {rx:0}/{w.TxMbps:0}Mbps", C("graph_text"));
                if (w.Error != null) t.T("  " + w.Error, C("inactive_fg"));
            }
            y++;
        }

        int gh = maxY - y;
        if (gh < 2 || !_netHist.TryGetValue(n.Id, out var hist)) return;
        int up = gh / 2, down = gh - up;
        int samples = iw * (_cfg.GraphSymbol == "braille" ? 2 : 1);
        double rxMax = Math.Max(hist.rx.MaxLast(samples), 10 * 1024);
        double txMax = Math.Max(hist.tx.MaxLast(samples), 10 * 1024);
        Draw.Graph(_scr, ix, y, iw, down, hist.rx, v => 100 * v / rxMax, _theme.Grad("download"), _cfg.GraphSymbol);
        Draw.Graph(_scr, ix, y + down, iw, up, hist.tx, v => 100 * v / txMax, _theme.Grad("upload"), _cfg.GraphSymbol, invert: true);
        _scr.Text(ix + 1, y, "▼ " + Rate(rxMax, bits), C("graph_text"));
        _scr.Text(ix + 1, y + gh - 1, "▲ " + Rate(txMax, bits), C("graph_text"));
    }

    // ---- PORTS

    List<PortInfo> VisiblePorts(Snapshot? s)
    {
        if (s == null) return new();
        IEnumerable<PortInfo> q = _cfg.PortsShowUdp ? s.Ports : s.Ports.Where(p => p.Proto == "TCP");
        // Collector order is TCP first, then port number.
        if (_cfg.PortsSort == "process")
            q = q.OrderBy(p => p.Process, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Proto).ThenBy(p => p.Port);
        return q.ToList();
    }

    void DrawPorts(Rect r, Snapshot? s)
    {
        string? right = s == null ? null
            : $"tcp {s.Ports.Count(p => p.Proto == "TCP")} ─ udp {s.Ports.Count(p => p.Proto == "UDP")} ─ est {s.TcpEstablished}";
        Box(r, "ports_box", "ports", right, $"sort: {_cfg.PortsSort} (p) ─ o all ─ all = reachable from network");
        if (s == null) { Waiting(r); return; }
        var ports = VisiblePorts(s);
        int ix = r.X + 2, iw = r.W - 4;
        int y = r.Y + 1, maxY = r.Y + r.H - 1;
        if (ports.Count == 0) { _scr.Text(ix, y, "no listening ports", C("inactive_fg")); return; }

        int addrW = Math.Clamp(iw - 4 - 6 - 7 - 18, 5, 15);
        int nameW = Math.Max(6, iw - 4 - 6 - addrW - 1 - 7 - 1);
        var hdr = new Tw(_scr, ix, y++, ix + iw);
        hdr.T("Prt ", C("title"), bold: true);
        hdr.T("Port".PadLeft(5) + " ", C("title"), bold: true);
        hdr.T("Address".PadRight(addrW) + " ", C("title"), bold: true);
        hdr.T("Pid".PadLeft(6) + " ", C("title"), bold: true);
        hdr.T("Process", C("title"), bold: true);

        int rows = maxY - y;
        for (int i = 0; i < ports.Count && i < rows; i++)
        {
            var p = ports[i];
            if (i == rows - 1 && ports.Count > rows)
            {
                _scr.Text(ix, y, $"… {ports.Count - rows + 1} more", C("inactive_fg"), iw);
                break;
            }
            var t = new Tw(_scr, ix, y++, ix + iw);
            t.T(p.Proto + " ", p.Proto == "TCP" ? _theme.Grad("download").At(100) : _theme.Grad("upload").At(100));
            t.T($"{p.Port,5} ", C("main_fg"), bold: true);
            var addrColor = p.Address == "all" ? C("hi_fg") : p.Address == "local" ? C("inactive_fg") : C("main_fg");
            t.T(Fit(p.Address, addrW).PadRight(addrW) + " ", addrColor);
            t.T($"{p.Pid,6} ", C("proc_misc"));
            t.Name(p.Process, p.App, nameW, C("main_fg"), C("inactive_fg"));
        }
    }

    // ---- SENSORS

    List<Reading> VisibleFans(Snapshot? s) =>
        s == null ? new() : s.Fans.Where(f => !_cfg.HideIdleFans || f.Value > 0).ToList();

    /// <summary>CPU package + all GPU power, or null when no power sensor reports.</summary>
    static double? MeasuredPower(Snapshot s)
    {
        double? total = s.CpuPower;
        foreach (var g in s.Gpus)
            if (g.Power is double p) total = (total ?? 0) + p;
        return total;
    }

    int SensorRows(Snapshot? s, int w)
    {
        if (s == null) return 2;
        int fans = VisibleFans(s).Count, temps = s.Temps.Count;
        int notice = SensorNotice(s) != null ? 1 : 0;
        return notice + (w - 2 >= 56 ? Math.Max(fans, temps) : fans + temps + (fans > 0 && temps > 0 ? 1 : 0));
    }

    string? SensorNotice(Snapshot s)
    {
        if (!s.Elevated) return "not admin: CPU temp, fans, disk temps hidden (press a)";
        if (_pawnIoMissing) return "PawnIO driver not installed (pawnio.eu): CPU temp/fans unavailable";
        if (s.HwStatus.Length > 0) return s.HwStatus;
        return null;
    }

    void DrawPower(Rect r, Snapshot? s)
    {
        Box(r, "power_box", "power", "cpu + gpu");
        if (s == null) { Waiting(r); return; }
        int ix = r.X + 2, iw = r.W - 4;
        int y = r.Y + 1, maxY = r.Y + r.H - 1;
        if (MeasuredPower(s) is not double total)
        {
            if (y < maxY) _scr.Text(ix, y, s.Elevated ? "no power sensors" : "no power sensors (press a to run as admin)", C("inactive_fg"), iw);
            return;
        }

        // Line 1: CPU W + GPU W = total W, with a meter against the peak seen so far
        var t = new Tw(_scr, ix, y++, ix + iw);
        t.T("CPU ", C("graph_text"));
        t.T(s.CpuPower is double cp ? $"{cp:0}W" : "n/a", s.CpuPower != null ? C("main_fg") : C("inactive_fg"));
        double gpuW = s.Gpus.Sum(g => g.Power ?? 0);
        t.T(" + GPU ", C("graph_text"));
        t.T(s.Gpus.Any(g => g.Power != null) ? $"{gpuW:0}W" : "n/a", C("main_fg"));
        t.T(" = ", C("graph_text"));
        string tot = $"{total:0}W";
        var grad = _theme.Grad("cpu");
        double pct = _powerPeak > 0 ? 100 * total / _powerPeak : 0;
        int barW = ix + iw - t.X - tot.Length - 1;
        if (barW >= 6)
        {
            Draw.Meter(_scr, t.X, y - 1, barW, pct, grad, C("meter_bg"));
            t.X += barW + 1;
        }
        t.T(tot, grad.At(Math.Min(100, total / 3)), bold: true);

        // Line 2: energy since start, average and peak
        if (y >= maxY) return;
        var e = new Tw(_scr, ix, y, ix + iw);
        var elapsed = DateTime.Now - _energyStart;
        e.T("Energy ", C("graph_text"));
        e.T(_energyWh >= 1000 ? $"{_energyWh / 1000:0.00} kWh" : _energyWh >= 10 ? $"{_energyWh:0.0} Wh" : $"{_energyWh:0.00} Wh", C("main_fg"));
        e.T($" in {Uptime(elapsed)}", C("graph_text"));
        if (elapsed.TotalHours > 0)
        {
            e.T("  avg ", C("graph_text"));
            e.T($"{_energyWh / elapsed.TotalHours:0}W", C("main_fg"));
        }
        e.T("  peak ", C("graph_text"));
        e.T($"{_powerPeak:0}W", C("main_fg"));
    }

    void DrawSensors(Rect r, Snapshot? s)
    {
        Box(r, "sensors_box", "sensors", "fans ─ temps");
        if (s == null) { Waiting(r); return; }
        int ix = r.X + 2, iw = r.W - 4;
        int y = r.Y + 1, maxY = r.Y + r.H - 1;
        var notice = SensorNotice(s);
        if (notice != null)
        {
            _scr.Text(ix, y++, Fit(notice, iw), C("hi_fg"));
        }
        var fans = VisibleFans(s);
        var temps = s.Temps;
        if (fans.Count == 0 && temps.Count == 0)
        {
            if (y < maxY) _scr.Text(ix, y, "no sensors", C("inactive_fg"));
            return;
        }
        bool side = iw >= 54;
        int colW = side ? (iw - 2) / 2 : iw;
        int fy = y, tyStart = side ? y : y + fans.Count + (fans.Count > 0 ? 1 : 0);
        int tx = side ? ix + colW + 2 : ix;

        foreach (var f in fans)
        {
            if (fy >= maxY) break;
            string label = SensorLabel(f);
            string val = f.Value > 0 ? $"{f.Value,5:0} rpm" : "  stopped";
            var t = new Tw(_scr, ix, fy, ix + colW);
            int barW = Math.Max(0, colW - 15 - val.Length - 1);
            t.T(Fit(label, 14).PadRight(15), C("main_fg"));
            double max = Math.Max(_fanMax.GetValueOrDefault(f.Group + "/" + f.Name), 1500);
            double pct = 100 * f.Value / max;
            if (barW > 0)
            {
                Draw.Meter(_scr, t.X, fy, barW, pct, _theme.Grad("fan"), C("meter_bg"));
                t.X += barW + 1;
            }
            t.T(val, f.Value > 0 ? _theme.Grad("fan").At(pct) : C("inactive_fg"));
            fy++;
        }
        int ty = tyStart;
        foreach (var tr in temps)
        {
            if (ty >= maxY) break;
            string label = SensorLabel(tr);
            string val = Temp(tr.Value).PadLeft(6);
            var t = new Tw(_scr, tx, ty, tx + colW);
            int lw = Math.Min(24, colW - val.Length - 6);
            int barW = Math.Max(0, colW - lw - 1 - val.Length - 1);
            t.T(Fit(label, lw).PadRight(lw + 1), C("main_fg"));
            double pct = TempPct(tr.Value);
            if (barW > 0)
            {
                Draw.Meter(_scr, t.X, ty, barW, pct, _theme.Grad("temp"), C("meter_bg"));
                t.X += barW + 1;
            }
            t.T(val, _theme.Grad("temp").At(pct));
            ty++;
        }
    }

    static string SensorLabel(Reading r)
    {
        if (r.Name is "Temperature" or "Composite Temperature") return r.Group;
        // Drive models are long: "WD_BLACK SN770 500 #1" instead of "... Temperature #1"
        if (r.Group.Length > 6 && r.Name.StartsWith("Temperature")) return r.Group + " " + r.Name["Temperature".Length..].Trim();
        string n = r.Name.Replace("Core (Tctl/Tdie)", "Tctl/Tdie").Replace("Temperature", "Temp");
        if (n.StartsWith(r.Group, StringComparison.OrdinalIgnoreCase)) return n;
        return r.Group + " " + n;
    }

    // ---- PROC

    List<ProcInfo> SortedProcs(Snapshot s)
    {
        IEnumerable<ProcInfo> q = _cfg.ProcSort switch
        {
            "mem" => s.Procs.OrderByDescending(p => p.MemBytes),
            "pid" => s.Procs.OrderBy(p => p.Pid),
            "name" => s.Procs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            "threads" => s.Procs.OrderByDescending(p => p.Threads),
            "gpu" => s.Procs.OrderByDescending(p => p.Gpu).ThenByDescending(p => p.Cpu),
            _ => s.Procs.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.MemBytes),
        };
        var list = q.ToList();
        if (_cfg.ProcReversed) list.Reverse();
        return list;
    }

    void DrawProc(Rect r, Snapshot? s)
    {
        string arrow = _cfg.ProcReversed ? "↑" : "↓";
        Box(r, "proc_box", "proc", $"sort: {_cfg.ProcSort} {arrow} (s/r)", "↑↓ select ─ k kill ─ w web ─ l all ─ ? help");
        if (s == null) { Waiting(r); return; }
        int ix = r.X + 1, iw = r.W - 2;
        int y = r.Y + 1;
        int rows = r.H - 3;
        if (rows < 1) return;

        var list = SortedProcs(s);
        if (list.Count == 0) return;

        // Keep selection on the same pid when the list reorders.
        if (_selPid >= 0 && _selIndex > 0) // at the top row, stay pinned to the top instead of following a pid down
        {
            int idx = list.FindIndex(p => p.Pid == _selPid);
            if (idx >= 0) _selIndex = idx;
        }
        _selIndex = Math.Clamp(_selIndex, 0, list.Count - 1);
        _selPid = list[_selIndex].Pid;
        if (_selIndex < _procScroll) _procScroll = _selIndex;
        if (_selIndex >= _procScroll + rows) _procScroll = _selIndex - rows + 1;
        _procScroll = Math.Clamp(_procScroll, 0, Math.Max(0, list.Count - rows));

        bool wide = iw >= 60;
        int meterW = iw >= 70 ? 6 : 0;
        int fixedW = 7 + 1 + (wide ? 5 + 1 + 6 + 1 : 0) + 8 + 1 + 5 + 1 + 6 + (meterW > 0 ? meterW + 1 : 0);
        int nameW = Math.Max(8, iw - fixedW - 3);

        var hdr = new Tw(_scr, ix + 1, y, ix + iw);
        hdr.T("Pid".PadLeft(7) + " ", C("title"), bold: true);
        hdr.T("Program".PadRight(nameW) + " ", C("title"), bold: true);
        if (wide) hdr.T("Thr".PadLeft(5) + " " + "Handle".PadLeft(6) + " ", C("title"), bold: true);
        hdr.T("MemB".PadLeft(8) + " ", C("title"), bold: true);
        hdr.T("Gpu%".PadLeft(5) + " ", C("title"), bold: true);
        if (meterW > 0) hdr.T(new string(' ', meterW + 1), C("title"));
        hdr.T("Cpu%".PadLeft(6), C("title"), bold: true);
        y++;

        for (int i = 0; i < rows && _procScroll + i < list.Count; i++)
        {
            var p = list[_procScroll + i];
            bool sel = _procScroll + i == _selIndex;
            Rgb? bg = sel ? C("selected_bg") : null;
            if (sel) _scr.SetBg(ix, y, iw, bg);
            var fg = sel ? C("selected_fg") : C("main_fg");
            var t = new Tw(_scr, ix + 1, y, ix + iw, bg);
            t.T($"{p.Pid,7} ", sel ? fg : C("proc_misc"));
            t.Name(p.Name, p.App, nameW, fg, sel ? fg : C("inactive_fg"), sel);
            t.T(" ", fg);
            if (wide) t.T($"{p.Threads,5} {p.Handles,6} ", sel ? fg : C("graph_text"));
            t.T($"{Bytes(p.MemBytes),8} ", fg);
            t.T(p.Gpu >= 0.05 ? $"{p.Gpu,5:0.0} " : "    - ", sel ? fg : p.Gpu >= 0.05 ? _theme.Grad("gpu").At(Math.Min(100, p.Gpu * 2)) : C("inactive_fg"));
            if (meterW > 0)
            {
                // scale the mini meter so one fully-busy core reads as a full bar on many-core machines
                double scaled = Math.Min(100, p.Cpu * (_cfg.ProcPerCore ? 1 : s.LogicalCores));
                Draw.Meter(_scr, t.X, y, meterW, scaled, _theme.Grad("process"), sel ? C("selected_bg") : C("meter_bg"));
                if (sel) _scr.SetBg(t.X, y, meterW, bg);
                t.X += meterW + 1;
            }
            t.T(p.Cpu >= 99.95 ? $"{p.Cpu,6:0}" : $"{p.Cpu,6:0.0}", sel ? fg : _theme.Grad("process").At(Math.Min(100, p.Cpu * 4)));
            y++;
        }
    }

    // ------------------------------------------------------------------ list overlays (all ports / all processes)

    void OpenOverlay(Overlay o)
    {
        _overlay = o;
        _ovSel = 0;
        _ovScroll = 0;
        _ovSelKey = null;
    }

    static string PortKey(PortInfo p) => $"{p.Proto}:{p.Port}:{p.Pid}";

    List<PortInfo> AllPorts(Snapshot s)
    {
        IEnumerable<PortInfo> q = s.Ports; // all ports, including UDP, regardless of ports_show_udp
        if (_cfg.PortsSort == "process")
            q = q.OrderBy(p => p.Process, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Proto).ThenBy(p => p.Port);
        return q.ToList();
    }

    /// <summary>Selected row's pid and process name in the open overlay.</summary>
    (int pid, string name, PortInfo? port)? OverlaySelection()
    {
        var s = _snap;
        if (s == null) return null;
        if (_overlay == Overlay.Procs)
        {
            var l = SortedProcs(s);
            return _ovSel < l.Count ? (l[_ovSel].Pid, WithApp(l[_ovSel].Name, l[_ovSel].App), null) : null;
        }
        var ports = AllPorts(s);
        return _ovSel < ports.Count ? (ports[_ovSel].Pid, WithApp(ports[_ovSel].Process, ports[_ovSel].App), ports[_ovSel]) : null;
    }

    bool HandleOverlayKey(ConsoleKeyInfo k)
    {
        void Move(int d) { _ovSel = Math.Max(0, _ovSel + d); _ovSelKey = null; }
        switch (k.Key)
        {
            case ConsoleKey.Escape: _overlay = Overlay.None; return true;
            case ConsoleKey.UpArrow: Move(-1); return true;
            case ConsoleKey.DownArrow: Move(1); return true;
            case ConsoleKey.PageUp: Move(-_ovPage); return true;
            case ConsoleKey.PageDown: Move(_ovPage); return true;
            case ConsoleKey.Home: Move(-1_000_000); return true;
            case ConsoleKey.End: Move(1_000_000); return true;
        }
        switch (k.KeyChar)
        {
            case 'q':
                _overlay = Overlay.None;
                return true;
            case 'o':
                if (_overlay == Overlay.Ports) _overlay = Overlay.None; else OpenOverlay(Overlay.Ports);
                return true;
            case 'l':
                if (_overlay == Overlay.Procs) _overlay = Overlay.None; else OpenOverlay(Overlay.Procs);
                return true;
            case 'k':
                if (OverlaySelection() is { } sel && sel.pid > 0)
                {
                    _confirmKillPid = sel.pid;
                    _confirmKillCreate = _snap?.Procs.FirstOrDefault(p => p.Pid == sel.pid)?.CreateTime ?? 0;
                    _confirmKillName = sel.name;
                }
                return true;
            case 'w':
                if (OverlaySelection() is { } ws) WebSearch(ws.name, ws.port);
                return true;
        }
        return false; // fall through to global keys (sort, theme, interval...)
    }

    /// <summary>Opens a Google search about the process in the user's default browser.</summary>
    void WebSearch(string name, PortInfo? port)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?") { Flash("process name unknown", 2); return; }
        string q = name.Contains(' ') ? name : $"\"{name}\"";
        string query = port != null
            ? $"{q} Windows process what is it, why does it listen on {port.Proto} port {port.Port}"
            : $"{q} Windows process what is it, why high CPU or GPU usage";
        string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        try
        {
            // explorer.exe hands the URL to the already-running shell, so the browser starts
            // unelevated even when wintop runs as administrator.
            // Full path: a bare "explorer.exe" is searched in the current directory before C:\Windows.
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, "\"" + url + "\"") { UseShellExecute = false });
            Flash($"searching the web for {name}", 2);
        }
        catch (Exception ex) { Flash("could not open browser: " + ex.Message, 4); }
    }

    void DrawOverlay()
    {
        var s = _snap;
        if (s == null) return;
        int x = 2, y = 1, w = _scr.W - 4, h = _scr.H - 2;
        if (w < 40 || h < 8) return;
        var bg = _theme.Opt("main_bg") ?? new Rgb(0, 0, 0);
        _scr.Fill(x, y, w, h, ' ', C("main_fg"), bg);

        bool procs = _overlay == Overlay.Procs;
        var procList = procs ? SortedProcs(s) : null;
        var portList = procs ? null : AllPorts(s);
        int count = procs ? procList!.Count : portList!.Count;

        // keep the selected row on the same item when the live list reorders
        if (_ovSelKey != null)
        {
            int idx = procs ? procList!.FindIndex(p => p.Pid.ToString() == _ovSelKey) : portList!.FindIndex(p => PortKey(p) == _ovSelKey);
            if (idx >= 0) _ovSel = idx;
        }
        _ovSel = Math.Clamp(_ovSel, 0, Math.Max(0, count - 1));
        if (count > 0) _ovSelKey = procs ? procList![_ovSel].Pid.ToString() : PortKey(portList![_ovSel]);

        int rows = h - 3; // borders + header
        _ovPage = Math.Max(1, rows - 1);
        if (_ovSel < _ovScroll) _ovScroll = _ovSel;
        if (_ovSel >= _ovScroll + rows) _ovScroll = _ovSel - rows + 1;
        _ovScroll = Math.Clamp(_ovScroll, 0, Math.Max(0, count - rows));

        string title = procs ? $"all processes ({count})" : $"all ports ({count})";
        string sort = procs ? $"sort: {_cfg.ProcSort} {(_cfg.ProcReversed ? "↑" : "↓")} (s/r)" : $"sort: {_cfg.PortsSort} (p)";
        Box(new Rect(x, y, w, h), procs ? "proc_box" : "ports_box", title, $"{(count == 0 ? 0 : _ovSel + 1)}/{count} ─ {sort}",
            "↑↓ PgUp PgDn Home End ─ k kill ─ w web search ─ Esc close");

        int ix = x + 2, iw = w - 5; // leave a column for the scrollbar
        var hdr = new Tw(_scr, ix, y + 1, ix + iw, bg);
        int nameW;
        if (procs)
        {
            nameW = Math.Max(10, iw - (7 + 1 + 7 + 1 + 5 + 1 + 6 + 1 + 8 + 1 + 6 + 1 + 6) - 3);
            hdr.T("Pid".PadLeft(7) + " " + "Parent".PadLeft(7) + " " + "Program".PadRight(nameW) + " " + "Thr".PadLeft(5) + " " + "Handle".PadLeft(6) + " " + "MemB".PadLeft(8) + " " + "Gpu%".PadLeft(6) + " " + "Cpu%".PadLeft(6), C("title"), bold: true);
        }
        else
        {
            nameW = Math.Max(10, iw - (4 + 6 + 40 + 1 + 7 + 1 + 6 + 1 + 8) - 3);
            hdr.T("Prt " + "Port".PadLeft(5) + " " + "Address".PadRight(40) + " " + "Pid".PadLeft(7) + " " + "Process".PadRight(nameW) + " " + "Cpu%".PadLeft(6) + " " + "MemB".PadLeft(8), C("title"), bold: true);
        }

        var byPid = procs ? null : s.Procs.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        for (int i = 0; i < rows && _ovScroll + i < count; i++)
        {
            int idx = _ovScroll + i;
            int ry = y + 2 + i;
            bool sel = idx == _ovSel;
            Rgb rowBg = sel ? C("selected_bg") : bg;
            if (sel) _scr.SetBg(x + 1, ry, w - 2, rowBg);
            var fg = sel ? C("selected_fg") : C("main_fg");
            var t = new Tw(_scr, ix, ry, ix + iw, rowBg);
            if (procs)
            {
                var p = procList![idx];
                t.T($"{p.Pid,7} ", sel ? fg : C("proc_misc"));
                t.T($"{p.ParentPid,7} ", sel ? fg : C("graph_text"));
                t.Name(p.Name, p.App, nameW, fg, sel ? fg : C("inactive_fg"), sel);
                t.T(" ", fg);
                t.T($"{p.Threads,5} {p.Handles,6} ", sel ? fg : C("graph_text"));
                t.T($"{Bytes(p.MemBytes),8} ", fg);
                t.T(p.Gpu >= 0.05 ? $"{p.Gpu,6:0.0} " : "     - ", sel ? fg : _theme.Grad("gpu").At(Math.Min(100, p.Gpu * 2)));
                t.T($"{p.Cpu,6:0.0}", sel ? fg : _theme.Grad("process").At(Math.Min(100, p.Cpu * 4)));
            }
            else
            {
                var p = portList![idx];
                t.T(p.Proto + " ", sel ? fg : p.Proto == "TCP" ? _theme.Grad("download").At(100) : _theme.Grad("upload").At(100));
                t.T($"{p.Port,5} ", fg, bold: true);
                var ac = sel ? fg : p.Address == "all" ? C("hi_fg") : p.Address == "local" ? C("inactive_fg") : C("main_fg");
                t.T(Fit(p.Address, 40).PadRight(40) + " ", ac);
                t.T($"{p.Pid,7} ", sel ? fg : C("proc_misc"));
                t.Name(p.Process, p.App, nameW, fg, sel ? fg : C("inactive_fg"), sel);
                t.T(" ", fg);
                if (byPid!.TryGetValue(p.Pid, out var pi))
                {
                    t.T($"{pi.Cpu,6:0.0} ", sel ? fg : _theme.Grad("process").At(Math.Min(100, pi.Cpu * 4)));
                    t.T($"{Bytes(pi.MemBytes),8}", fg);
                }
            }
        }

        // scrollbar
        if (count > rows)
        {
            int sx = x + w - 2, top = y + 2;
            int thumb = Math.Max(1, rows * rows / count);
            int pos = (int)Math.Round((double)_ovScroll / Math.Max(1, count - rows) * (rows - thumb));
            for (int i = 0; i < rows; i++)
            {
                bool on = i >= pos && i < pos + thumb;
                _scr.Put(sx, top + i, on ? '█' : '│', on ? C(procs ? "proc_box" : "ports_box") : C("div_line"), bg);
            }
        }
    }

    // ------------------------------------------------------------------ overlays

    void DrawHelp()
    {
        string[] lines =
        {
            "q / Esc     quit",
            "↑ ↓ PgUp PgDn Home End   select process",
            "k           kill selected process",
            "s           cycle process sort",
            "p           sort ports by port / process",
            "o           all ports (overlay)",
            "l           all processes (overlay)",
            "w           web search selected process",
            "r           reverse sort",
            "t           next theme",
            "n           next network adapter",
            "g           graph style (braille/block/tty)",
            "f           toggle °C / °F",
            "+ / -       update interval (= and _ also work)",
            "a           restart as administrator",
            "F5          reload config.json and theme",
            "",
            $"config: {_cfg.Path}",
            $"themes: {_themeDir}",
            "",
            "press any key to close",
        };
        int w = Math.Min(_scr.W - 4, lines.Max(l => l.Length) + 4);
        int h = Math.Min(_scr.H - 2, lines.Length + 2);
        int x = (_scr.W - w) / 2, y = (_scr.H - h) / 2;
        var bg = _theme.Opt("main_bg") ?? new Rgb(0, 0, 0);
        _scr.Fill(x, y, w, h, ' ', C("main_fg"), bg);
        Box(new Rect(x, y, w, h), "proc_box", "help");
        for (int i = 0; i < lines.Length && i < h - 2; i++)
            _scr.Text(x + 2, y + 1 + i, lines[i], i < Array.IndexOf(lines, "") ? C("main_fg") : C("graph_text"), w - 4, bg: bg);
    }

    void DrawDialog(string text)
    {
        int w = Math.Min(_scr.W - 2, text.Length + 4);
        int x = (_scr.W - w) / 2, y = _scr.H / 2 - 1;
        var bg = _theme.Opt("main_bg") ?? new Rgb(0, 0, 0);
        _scr.Fill(x, y, w, 3, ' ', C("main_fg"), bg);
        Box(new Rect(x, y, w, 3), "proc_box", "confirm");
        _scr.Text(x + 2, y + 1, text, C("hi_fg"), w - 4, bold: true, bg: bg);
    }

    void DrawMessage()
    {
        string m = " " + _msg + " ";
        int x = Math.Max(0, (_scr.W - m.Length) / 2);
        _scr.Text(x, _scr.H - 1, m, C("selected_fg"), _scr.W, bold: true, bg: C("selected_bg"));
    }

    // ------------------------------------------------------------------ formatting

    string Temp(double c, bool unit = true)
    {
        double v = _cfg.TempUnit == "F" ? c * 9 / 5 + 32 : c;
        return unit ? $"{v:0}°{_cfg.TempUnit}" : $"{v:0}°";
    }

    static double TempPct(double c) => Math.Clamp((c - 25) / (95 - 25) * 100, 0, 100);

    static string Bytes(long b) => Bytes((double)b);

    static string Bytes(double b)
    {
        string[] u = { "B", "K", "M", "G", "T", "P" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0}B" : b >= 100 ? $"{b:0}{u[i]}" : b >= 10 ? $"{b:0.0}{u[i]}" : $"{b:0.00}{u[i]}";
    }

    static string Rate(double bps, bool bits)
    {
        if (bits)
        {
            double v = bps * 8;
            string[] u = { "b/s", "Kb/s", "Mb/s", "Gb/s" };
            int i = 0;
            while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
            return i == 0 || v >= 100 ? $"{v:0} {u[i]}" : $"{v:0.0} {u[i]}";
        }
        else
        {
            double v = bps;
            string[] u = { "B/s", "KB/s", "MB/s", "GB/s" };
            int i = 0;
            while (v >= 1000 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 || v >= 100 ? $"{v:0} {u[i]}" : $"{v:0.0} {u[i]}";
        }
    }

    static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}" : $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";

    static string WithApp(string name, string? app) => app == null ? name : $"{app} {name}";

    static string Fit(string s, int w) => w <= 0 ? "" : s.Length <= w ? s : w <= 1 ? s[..w] : s[..(w - 1)] + "…";

    static string ShortGpu(string n) => n.Replace("AMD ", "").Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("Radeon(TM) ", "Radeon ").Replace(" Series", "");

    public void Dispose()
    {
        _sampler.Dispose();
        RestoreConsole();
    }
}

/// <summary>Sequential text writer on one line, clipped at maxX.</summary>
sealed class Tw
{
    readonly Screen _s;
    readonly int _y, _maxX;
    readonly Rgb? _bg;
    public int X;

    public Tw(Screen s, int x, int y, int maxX, Rgb? bg = null)
    {
        _s = s;
        X = x;
        _y = y;
        _maxX = maxX;
        _bg = bg;
    }

    public void T(string text, Rgb fg, bool bold = false)
    {
        int room = _maxX - X;
        if (room <= 0) return;
        X = _s.Text(X, _y, text, fg, room, bold, _bg);
    }

    /// <summary>Process name plus its owning app ("node.exe Raycast") in a dimmer color, fitted and padded to w.</summary>
    public void Name(string name, string? app, int w, Rgb fg, Rgb dim, bool bold = false)
    {
        if (w <= 0) return;
        string full = app == null ? name : name + " " + app;
        string fit = full.Length <= w ? full : w <= 1 ? full[..w] : full[..(w - 1)] + "…";
        int nl = Math.Min(name.Length, fit.Length);
        T(fit[..nl], fg, bold);
        if (fit.Length > nl) T(fit[nl..], dim);
        if (fit.Length < w) T(new string(' ', w - fit.Length), fg);
    }
}
