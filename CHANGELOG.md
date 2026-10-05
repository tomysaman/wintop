# Changelog

Each version's section becomes the notes of its GitHub Release. Add a section before tagging a release.

## [1.0.0] - 2026-10-05

First release.

- **CPU:** total and per-thread load, history graph, average and per-core temperature, per-core clock, package power, uptime.
- **Memory:** used, available, cached, free, swap.
- **GPU:** load and history per GPU, VRAM, core and hotspot temperature, clock, power, fan, and the processes using the GPU most.
- **Disks:** used %, read and write rates, temperature.
- **Network:** download and upload graphs, totals, IP, Wi-Fi signal strength.
- **Ports:** listening TCP and bound UDP ports with the owning process; an overlay (`o`) lists all of them.
- **Power:** CPU + GPU watts, energy used since start, average and peak.
- **Sensors:** every fan and temperature sensor LibreHardwareMonitor finds.
- **Processes:** CPU %, GPU %, memory, threads, handles; sort, kill, web search; an overlay (`l`) lists all of them. Generic hosts (node.exe, svchost.exe, ...) show the app or service they belong to.
- Themes in btop's `.theme` format: default (btop-like), htop, nord.
- `config.json` and the `theme\` folder are created next to the exe on first run.
- Restart as administrator from inside the app (`a`), including in Windows Terminal.
