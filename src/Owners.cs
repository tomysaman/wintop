using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinTop;

/// <summary>
/// Works out which application a generic host process (node.exe, python.exe, svchost.exe ...)
/// belongs to, from its image path, command line and parent. Results are cached per process
/// instance, so each process is opened at most once.
/// </summary>
static class OwnerResolver
{
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "python.exe", "pythonw.exe", "py.exe", "java.exe", "javaw.exe", "dotnet.exe",
        "deno.exe", "bun.exe", "ruby.exe", "rubyw.exe", "perl.exe", "php.exe", "svchost.exe",
        "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
    };

    // Parents that say nothing about which app started a process.
    static readonly HashSet<string> NeutralParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "userinit.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "WindowsTerminal.exe", "OpenConsole.exe",
        "System", "smss.exe", "taskeng.exe", "taskhostw.exe", "sihost.exe", "RuntimeBroker.exe",
    };

    // Folder names that belong to a runtime install rather than to an app bundling it.
    static readonly Regex RuntimeDir = new(
        @"^(node(js)?|nvm|volta|fnm|python\d*|py|java|jdk.*|jre.*|openjdk.*|eclipse adoptium|zulu.*|dotnet.*|powershell|deno|bun|ruby\d*|perl|strawberry|php\d*|git|scoop|chocolatey|miniconda\d*|anaconda\d*|microsoft|windowsapps|common files)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Folder names too generic to identify a script's project.
    static readonly Regex VagueDir = new(
        @"^(src|dist|bin|lib|out|build|scripts?|backend|server|app|resources|node_modules|\.bin|cli|index|main|current|latest|v?\d[\d.]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Dictionary<(int pid, long create), string?> Cache = new();

    public static bool IsGeneric(string name) => Generic.Contains(name);

    /// <summary>Labels generic processes in the list; call once per sample with the full process list.</summary>
    static int _annotateStamp;

    public static void Annotate(List<ProcInfo> procs)
    {
        _annotateStamp++;
        var byPid = new Dictionary<int, ProcInfo>(procs.Count);
        foreach (var p in procs) byPid[p.Pid] = p;
        var live = new HashSet<(int, long)>();

        foreach (var p in procs)
        {
            if (!Generic.Contains(p.Name)) continue;
            var key = (p.Pid, p.CreateTime);
            live.Add(key);
            if (!Cache.TryGetValue(key, out var app))
            {
                try { app = Resolve(p, byPid); } catch { app = null; }
                Cache[key] = app;
            }
            p.App = app;
        }
        if (Cache.Count > live.Count + 64)
            foreach (var k in Cache.Keys.Where(k => !live.Contains(k)).ToList()) Cache.Remove(k);
    }

    static string? Resolve(ProcInfo p, Dictionary<int, ProcInfo> byPid)
    {
        var (path, cmd) = Query(p.Pid);
        var args = cmd != null ? SplitArgs(cmd) : Array.Empty<string>();
        string n = p.Name.ToLowerInvariant();

        // System svchosts can't be opened without admin; the service manager still says what runs in them.
        if (n == "svchost.exe")
            return ArgAfter(args, "-s") ?? ServicesIn(p.Pid) ?? ArgAfter(args, "-k");

        if (n == "rundll32.exe")
        {
            var dll = args.Skip(1).FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith('-'));
            if (dll != null) return Path.GetFileNameWithoutExtension(dll.Split(',')[0].Trim('"'));
        }

        // 1. The runtime is bundled inside an application folder (Raycast\backend\node.exe).
        if (path != null && AppFromPath(path) is { } fromPath) return fromPath;

        // 2. The script or package being run (node ...\vite\bin\vite.js, java -jar foo.jar).
        if (ScriptFromArgs(n, args, cmd ?? "") is { } fromScript) return fromScript;

        // 3. The process that started it (Code.exe -> node.exe).
        if (byPid.TryGetValue(p.ParentPid, out var parent) && parent.CreateTime <= p.CreateTime
            && !NeutralParents.Contains(parent.Name))
            return parent.App ?? Path.GetFileNameWithoutExtension(parent.Name);

        return null;
    }

    static string? ArgAfter(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static string? AppFromPath(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int Find(string s) => Array.FindIndex(parts, x => x.Equals(s, StringComparison.OrdinalIgnoreCase));

        // MSIX: ...\WindowsApps\Publisher.Name_1.2.3.0_x64__hash\...
        int wa = Find("WindowsApps");
        if (wa >= 0 && wa + 2 < parts.Length)
        {
            string family = parts[wa + 1].Split('_')[0];
            int dot = family.IndexOf('.');
            string name = dot > 0 ? family[(dot + 1)..] : family;
            return RuntimeDir.IsMatch(name.Split('.')[0]) ? null : name;
        }

        string? Under(int i) => i >= 0 && i + 2 < parts.Length && !RuntimeDir.IsMatch(parts[i + 1]) ? parts[i + 1] : null;

        int pf = Array.FindIndex(parts, x => x.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase));
        if (pf >= 0) return Under(pf);

        int ad = Find("AppData");
        if (ad >= 0 && ad + 1 < parts.Length)
        {
            int start = ad + 1; // Local / Roaming
            if (start + 1 < parts.Length && parts[start + 1].Equals("Programs", StringComparison.OrdinalIgnoreCase)) start++;
            return Under(start);
        }
        return null;
    }

    static readonly string[] ScriptExt = { ".js", ".mjs", ".cjs", ".ts", ".mts", ".py", ".pyw", ".jar", ".ps1", ".vbs", ".bat", ".cmd", ".rb", ".pl", ".php", ".dll" };

    static readonly HashSet<string> PackageRunners = new(StringComparer.OrdinalIgnoreCase) { "npx", "pnpx", "bunx", "uvx", "pipx", "npm", "pnpm", "yarn", "corepack" };
    static readonly HashSet<string> RunnerVerbs = new(StringComparer.OrdinalIgnoreCase) { "exec", "x", "dlx", "run", "run-script", "start" };

    static string? ScriptFromArgs(string exe, string[] args, string cmd)
    {
        if (exe == "cmd.exe")
        {
            // cmd /c "npx -y some-package"  ->  some-package
            var m = Regex.Match(cmd, @"\s/[ckCK]\s+(.*)$");
            if (!m.Success) return null;
            var tok = m.Groups[1].Value.Replace("^", "").Replace("\"", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) return null;
            string tool = Path.GetFileNameWithoutExtension(tok[0]);
            if (PackageRunners.Contains(tool)) return NextPackage(tok, 1) ?? tool;
            return tok[0].Contains('\\') && ScriptExt.Any(e => tok[0].EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? Project(tok[0]) : tool;
        }

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (exe is "python.exe" or "pythonw.exe" or "py.exe" && a == "-m" && i + 1 < args.Length) return args[i + 1];
            if (exe is "java.exe" or "javaw.exe" && a == "-jar" && i + 1 < args.Length) return Path.GetFileNameWithoutExtension(args[i + 1]);
            if (exe is "powershell.exe" or "pwsh.exe" && a.Equals("-File", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return Project(args[i + 1]);
            if (a.StartsWith('-') || a.StartsWith('/')) continue;
            if (a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(a);
            if (ScriptExt.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                string proj = Project(a);
                // node ...\npm\bin\npx-cli.js -y @scope/pkg  ->  @scope/pkg
                return PackageRunners.Contains(proj) ? NextPackage(args, i + 1) ?? proj : proj;
            }
            if (exe is "java.exe" or "javaw.exe" && a.Contains('.') && !a.Contains('\\')) return a.Split('.').Last(); // main class
        }
        return null;
    }

    static string? NextPackage(string[] tok, int from)
    {
        for (int i = from; i < tok.Length; i++)
            if (!tok[i].StartsWith('-') && !RunnerVerbs.Contains(tok[i])) return tok[i];
        return null;
    }

    /// <summary>Names the project a script belongs to: the npm package, or the nearest meaningful folder.</summary>
    static string Project(string script)
    {
        // Resolve "." and ".." (node_modules\.bin\..\vite\bin\vite.js).
        var parts = new List<string>();
        foreach (var x in script.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (x == ".") continue;
            if (x == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(x);
        }
        if (parts.Count == 0) return script;
        int nm = parts.FindLastIndex(x => x.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
        if (nm >= 0 && nm + 1 < parts.Count - 1)
            return parts[nm + 1].StartsWith('@') && nm + 2 < parts.Count - 1 ? parts[nm + 1] + "/" + parts[nm + 2] : parts[nm + 1];
        for (int i = parts.Count - 2; i >= 0; i--)
        {
            if (parts[i].EndsWith(':') || RuntimeDir.IsMatch(parts[i])) break;
            if (!VagueDir.IsMatch(parts[i])) return parts[i].TrimStart('.');
        }
        return Path.GetFileNameWithoutExtension(parts[^1]);
    }

    // ---------------- services ----------------
    static Dictionary<int, List<string>>? _services;
    static int _servicesStamp = -1;

    static string? ServicesIn(int pid)
    {
        // One service-manager snapshot per sample at most.
        if (_services == null || _servicesStamp != _annotateStamp)
        {
            _services = EnumServices();
            _servicesStamp = _annotateStamp;
        }
        if (!_services.TryGetValue(pid, out var names) || names.Count == 0) return null;
        return names.Count == 1 ? names[0] : $"{names[0]} +{names.Count - 1}";
    }

    const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint type, uint state, IntPtr buf, int size,
        out int needed, out int returned, ref int resume, string? group);

    static Dictionary<int, List<string>> EnumServices()
    {
        var map = new Dictionary<int, List<string>>();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return map;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int resume = 0;
            EnumServicesStatusExW(scm, 0, 0x30, 1, IntPtr.Zero, 0, out int needed, out _, ref resume, null); // SERVICE_WIN32, ACTIVE
            if (needed <= 0) return map;
            int size = needed + 4096;
            buf = Marshal.AllocHGlobal(size);
            resume = 0;
            if (!EnumServicesStatusExW(scm, 0, 0x30, 1, buf, size, out _, out int count, ref resume, null)) return map;
            // ENUM_SERVICE_STATUS_PROCESSW: name ptr, display ptr, SERVICE_STATUS_PROCESS (pid = 8th DWORD)
            int stride = IntPtr.Size * 2 + 9 * 4;
            stride = (stride + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
            for (int i = 0; i < count; i++)
            {
                IntPtr e = buf + i * stride;
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e));
                int pid = Marshal.ReadInt32(e, IntPtr.Size * 2 + 7 * 4);
                if (name == null || pid == 0) continue;
                if (!map.TryGetValue(pid, out var l)) map[pid] = l = new List<string>();
                l.Add(name);
            }
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseServiceHandle(scm);
        }
        return map;
    }

    // ---------------- native ----------------
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, char[] name, ref int size);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string cmd, out int argc);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    static (string? path, string? cmd) Query(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (null, null);
        try
        {
            string? path = null, cmd = null;
            var buf = new char[1024];
            int size = buf.Length;
            if (QueryFullProcessImageNameW(h, 0, buf, ref size)) path = new string(buf, 0, size);

            int len = 4096;
            for (int attempt = 0; attempt < 3 && cmd == null; attempt++)
            {
                IntPtr mem = Marshal.AllocHGlobal(len);
                try
                {
                    int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, mem, len, out int ret);
                    if (st == 0)
                    {
                        // UNICODE_STRING { ushort Length; ushort Max; PWSTR Buffer } followed by the text
                        int bytes = Marshal.ReadInt16(mem) & 0xFFFF;
                        IntPtr text = Marshal.ReadIntPtr(mem, IntPtr.Size);
                        cmd = text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
                    }
                    else if (ret > len) len = ret + 64;
                    else break;
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            return (path, cmd);
        }
        finally { CloseHandle(h); }
    }

    static string[] SplitArgs(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return Array.Empty<string>();
        IntPtr argv = CommandLineToArgvW(cmd, out int argc);
        if (argv == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var r = new string[argc];
            for (int i = 0; i < argc; i++) r[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return r;
        }
        finally { LocalFree(argv); }
    }
}
