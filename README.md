# wintop

A btop/htop-style system monitor for Windows in one portable `wintop.exe`. It needs no installer and no .NET runtime.

## Download

Get `wintop.exe` from the [latest release](../../releases/latest). Every version is listed on the [Releases page](../../releases), with its changes in [CHANGELOG.md](CHANGELOG.md).

Windows SmartScreen may warn on first launch because the exe isn't code-signed. Choose **More info → Run anyway**.

## Run

Copy `wintop.exe` anywhere and run it. On first start it creates these files next to the exe:

```
wintop.exe
config.json        settings (JSON)
theme\
  default.theme    btop-like (default)
  htop.theme
  nord.theme
```

**Run as administrator** to see CPU temperature, package power, per-core clocks, motherboard fans and temps, and disk SMART temps. Press `a` inside the app to restart elevated, or set `"require_admin": true`. Fan and CPU sensors need the [PawnIO](https://pawnio.eu) driver, which LibreHardwareMonitor uses. wintop warns when PawnIO is missing.

> **PowerToys FancyZones:** Windows doesn't let a program without admin rights move or resize an admin window. If PowerToys runs without admin rights (its default), Shift+drag snapping into FancyZones won't work on wintop while wintop runs as administrator. To fix it, open PowerToys Settings → General, turn on **"Always run as administrator"**, and restart PowerToys. Without admin rights, wintop snaps into zones normally.

## Layout

```
┌──────────── cpu ────────────┐
│ mem          │ gpu          │
│ disks        │ power        │
│ net          │ sensors      │
│              │ (power + sen-│
│              │  sors = disks│
│              │  + net)      │
│ ports        │ proc         │
└──────────────┴──────────────┘
```

The net graph stays 3–5 rows each for download and upload. A taller window gives the extra rows to ports and proc. Hide a box by removing it from `boxes`; if a row then has only one box, that box takes the full width.

## Metrics

| Box | Shows |
|---|---|
| cpu | total load graph, per-thread load, avg temp, per-core temp (if the CPU reports it), per-core clock, package power, uptime, process/thread count, battery |
| mem | used, available, cached (standby + modified), pagefile, commit charge |
| gpu | per-GPU load + history, VRAM, core/hotspot temp, clock, power, fan RPM, top GPU-using processes |
| disks | per volume: used %, used/total, read/write per second, active time % |
| net | download/upload rates, totals, graphs, IP; Wi-Fi SSID, signal %, RSSI, standard, channel, link rate |
| ports | listening TCP ports and bound UDP ports: protocol, port, address (`all` = reachable from the network, `local` = this PC only), PID, process; counts of TCP/UDP ports and established TCP connections |
| power | CPU package + GPU = total W with a meter against the peak, energy used since start, average and peak W |
| sensors | every fan (RPM) and temperature sensor (CPU, GPU, motherboard, drives) |
| proc | processes: pid, name, threads, handles, private memory, GPU %, CPU %; sort, select, kill |

Generic host processes get the app they belong to after their name, in dim text, in proc, ports and both overlays: `node.exe Raycast`, `node.exe vite`, `cmd.exe @modelcontextprotocol/server-pdf`, `svchost.exe Dnscache`, `dotnet.exe VBCSCompiler`. Hosts covered: node, python, java, dotnet, deno, bun, ruby, perl, php, svchost, rundll32, cmd, powershell/pwsh, wscript/cscript. The label comes from, in order: the app folder the runtime is bundled in, the script or package being run (npx/npm packages included), then the parent process. For svchost it's the service(s) inside it, read from the service manager, which works without admin. `w` includes the label in the web search.

The power total covers only the CPU and GPU, the parts with power sensors. RAM, drives, fans, the motherboard and PSU losses aren't measured, so wall power is higher. Energy is the measured total added up over time since wintop started. Without admin rights only GPU power is available, so the total and energy count the GPU only.

AMD Zen 2/3 CPUs only expose Tctl/Tdie and CCD temperatures, not one temperature per core. In that case the core list shows clocks.

## Keys

- `q` / `Esc`: quit
- `↑` `↓` `PgUp` `PgDn` `Home` `End`: select a process
- `k`: kill the selected process (asks for confirmation)
- `s`: cycle the process sort column
- `r`: reverse the sort order
- `p`: sort the ports box by port number or by process name
- `o`: all ports overlay; `l`: all processes overlay. Inside an overlay: `↑` `↓` `PgUp` `PgDn` `Home` `End` scroll, `k` kills the selected process, `w` web-searches it, `Esc` closes
- `w`: web search (Google) for the selected process: what it is, why it uses CPU/GPU, why it listens on a port. The browser opens as you, even when wintop runs as admin
- `t`: next theme
- `n`: next network adapter
- `g`: graph style (braille / block / tty)
- `f`: toggle °C / °F
- `+` or `=` / `-` or `_`: increase / decrease the update interval (works with or without Shift)
- `a`: restart as administrator
- `F5`: reload config.json and theme
- `?`: help

## config.json

In-app changes (sort, theme, interval, and so on) are saved back to `config.json`, which rewrites the file and drops any `//` comments. If the file fails to parse, wintop uses defaults and does not overwrite it. Fix the file and press `F5`.

```jsonc
{
  "theme": "default",            // theme\<name>.theme or theme\<name>\*.theme
  "update_ms": 1000,             // 250..10000
  "graph_symbol": "braille",     // braille | block | tty
  "temp_unit": "C",              // C | F
  "boxes": ["cpu","mem","gpu","disk","net","ports","power","sensors","proc"],  // remove to hide
  "rounded_corners": true,
  "theme_background": true,      // false = keep terminal background
  "proc_sort": "cpu",            // cpu | gpu | mem | pid | name | threads
  "proc_reversed": false,
  "proc_per_core": false,        // true = htop-style % of one core
  "net_interface": "",           // adapter name, "" = auto (default route)
  "net_bits": false,             // show bits/s
  "ports_show_udp": true,        // false = only TCP listening ports in the ports box
  "ports_sort": "port",          // port | process
  "disk_hide": [],               // e.g. ["Z:"]
  "hide_idle_fans": false,
  "require_admin": false,        // true = restart as admin at startup (UAC prompt); needed for CPU temp/power, fans, disk temps
  "hardware": { "cpu": true, "gpu": true, "motherboard": true, "storage": true,
                "memory": true, "controller": true, "psu": true, "battery": true }
}
```

## Themes

Themes use btop's `.theme` format, so btop themes work when copied into `theme\`. A theme is either `theme\name.theme` or a folder `theme\name\` that contains a `.theme` file. Each line has the form `theme[key]="#rrggbb"`. Values can also be `"#gg"` (gray), `"r g b"`, or `""` (unset).

wintop adds these keys beyond btop's: `gpu_box`, `disk_box`, `sensors_box`, `power_box`, `ports_box`, `gpu_*`, `disk_*`, `fan_*`, `swap_*`, `signal_*` (each gradient has `_start`/`_mid`/`_end`). If a theme leaves them out, they fall back to related btop keys. See `theme\default.theme` for every key.

## Diagnostics

- `wintop --dump` prints every detected metric and the raw sensor list.
- `wintop --themes` lists themes.
- `wintop -c other.json` uses another config file.

## Build

Requires the .NET 8 SDK.

```
dotnet publish src/WinTop.csproj -c Release -o dist
```

This produces a self-contained, compressed single-file `dist\wintop.exe` (~37 MB). `build\` and `dist\` are not committed. It is built on [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

## Releasing

1. Add a `## [x.y.z] - date` section to `CHANGELOG.md` and commit it.
2. Tag and push:
   ```
   git tag v1.1.0
   git push origin main v1.1.0
   ```

The `release` workflow (`.github/workflows/release.yml`) builds `wintop.exe` with that version, checks that `wintop --version` matches, and creates the GitHub Release with `wintop.exe`, `wintop.exe.sha256` and the changelog section as notes. A tag with a suffix (`v1.1.0-beta.1`) becomes a pre-release.

## License

MIT, see [LICENSE](LICENSE). The exe bundles [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), which is MPL-2.0.
