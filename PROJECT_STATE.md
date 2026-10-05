# VPS Tunnel

## Version

v1.1.1 FINAL / VERIFIED — SELECTIVE mode, per-user connection settings, single installer (Apps registration, shortcuts, in-place update).

v1.1.1 differs from v1.1.0 only in the installer.

## Status

v1.1 adds SELECTIVE (verified on the production PC, including WSL) and connection settings entered in the GUI, so the client can be given to other users without the owner's configuration. v1.0.0 FINAL / VERIFIED remains tagged as `v1.0.0`; `v0.9-network-verified` remains available.

## Architecture

- GUI: WPF / .NET 10 (`VPS.Tunnel.App`)
- Service: Windows Service / .NET 10 (`VPS.Tunnel.Service`)
- Installer: `installer/` → `VPS-Tunnel-Setup.exe` (install, update, uninstall; see installer/README.md)
- Core: shared models (`VPS.Tunnel.Core`)
- Tunnel engine: sing-box 1.14.1
- Protocol: VLESS + Reality, XTLS Vision
- Modes: DIRECT, TUNNEL, SELECTIVE

The GUI communicates with `VpsTunnelService` through Windows Service Control Manager. Normal GUI operation does not require elevation after setup.

## Production paths

- GUI: `C:\Program Files\VPS Tunnel\VPS-Tunnel.exe`
- Service: `C:\Program Files\VPS Tunnel\Service\VPS.Tunnel.Service.exe`
- sing-box: `C:\sing-box\sing-box-1.14.1-windows-amd64\sing-box.exe`
- sing-box configuration: `C:\sing-box\config.json`

## Development path

`C:\Projects\VPS-Tunnel` is the canonical source root. The legacy `C:\VPS-Tunnel` tree has been removed after production cutover.

## Settings and logs

- Production settings: `%LOCALAPPDATA%\VPS Tunnel\settings.json`
- GUI logs: `%LOCALAPPDATA%\VPS Tunnel\Logs\`
- Service logs: `%ProgramData%\VPS Tunnel\Logs\`

The Codex package may redirect AppData and HKCU writes into its own package store. Those copies are not production settings. Verify the physical Windows state when testing autostart.

## Windows Service

- Name: `VpsTunnelService`
- Display name: `VPS Tunnel Service`
- Start type: Manual
- Account: LocalSystem

DIRECT means the service is stopped and the TUN adapter is absent. TUNNEL means the service, its sing-box child process, and the TUN route are active.

## Verified features

- DIRECT/TUNNEL switching, VLESS + Reality, TUN routing, exit IP and country check
- Graceful service stop and unelevated GUI service control
- Session timer, traffic counters, IP/country refresh
- Tray, single instance, themes, and startup settings in local tests

## Connection settings (v1.1)

- The GUI card "Подключение" → "Изменить" accepts a `vless://` link (3x-ui, Marzban, Hiddify) or the individual fields (server, port, UUID, SNI, Reality public key, short ID, fingerprint, flow). Only VLESS + Reality over TCP is supported. Advanced users can import a ready sing-box `config.json` with a TUN inbound.
- `VPS.Tunnel.Core/ConnectionProfile.cs` builds a complete sing-box 1.14 configuration (TUN `sing-box-tun`, DNS over HTTPS through the tunnel, private networks direct). Generated TUNNEL and SELECTIVE variants pass `sing-box check` 1.14.1.
- The unelevated GUI writes the file through an elevated copy of itself (`VPS-Tunnel.exe --write-config <temp file>`, one UAC prompt). It accepts only its own temp file, validates it and installs `C:\sing-box\config.json` (previous one kept as `config.json.bak`) readable only by SYSTEM and Administrators.
- `settings.json` stores only the display name and the expected exit IP — never keys. The exit IP is the server IP (resolved if a domain); for an imported config it is learned from the first verified TUNNEL. A running tunnel restarts to apply new settings.
- On a computer without a configuration the GUI opens the settings window on first launch and does not auto-connect.

## SELECTIVE mode (v1.1)

Only the listed programs (`chrome.exe`, `Telegram.exe`, ...) go through the VPS; everything else goes direct.

- The list is edited in the GUI and stored in `settings.json` (`SelectiveApplications`).
- The GUI starts `VpsTunnelService` with SCM start arguments `--selective <name.exe>...`. A plain start (no arguments) is the unchanged, verified TUNNEL.
- The service validates the names (bare `*.exe` file names only, at most 64) and derives a copy of `C:\sing-box\config.json`: a `process_name` rule to the existing proxy outbound (`route.final` of the base config), `route.final` set to a `direct` outbound (added if missing) and `auto_detect_interface: true`. `config.json` itself is never modified.
- The derived file is `C:\Program Files\VPS Tunnel\Service\Runtime\selective-config.json`, readable only by SYSTEM and Administrators (it contains the same credentials).
- Switching TUNNEL ↔ SELECTIVE, or saving a changed list while SELECTIVE is active, restarts the service.
- In SELECTIVE the GUI's own IP check goes direct, so the home IP is shown and is not treated as a route conflict.
- Optional "Весь WSL (Ubuntu) через VPS" (`SelectiveIncludeWsl`, start argument `--wsl`): a logical rule sends source `172.16.0.0/12` (WSL 2 NAT network) to the proxy, excluding the TUN inbound addresses, from which Windows' own traffic arrives. Only for WSL networking mode NAT (the default); in mirrored mode WSL cannot be told apart from Windows. Other Hyper-V/Docker VMs in that range also go through the VPS.
- Existing rules of the base config stay in front of the added rule: a base rule that already routes some traffic to the proxy still does so in SELECTIVE.

## Known pending items

- None. v1.1.x verified by the owner: SELECTIVE (programs and WSL), app pickers, connection settings and the distributable installer.

## Security

Never commit `C:\sing-box\config.json`, VLESS UUIDs, Reality private keys, mobile credentials/configuration, user settings, logs, or build/publish output.

## Build and tests

```text
dotnet build -c Release
dotnet test -c Release
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

The last command produces `installer\dist\VPS-Tunnel-Setup.exe`, used for every install and update (it replaced the separate `VPS.Tunnel.Setup` tool).

`dotnet test -c Release` runs both test programs and prints their PASS lines. The v1.0.0 FINAL / VERIFIED freeze has 60 passing local tests: 51 GUI integration tests and 9 service tests. The final reboot test also verified production autostart, minimized tray startup without UAC, DIRECT, TUNNEL, VLESS + Reality, and repeated DIRECT → TUNNEL transitions. The service tests use fake child processes, not the real tunnel.

## Deployment work after v1.0.0

The `installer/` directory is separate post-freeze deployment work. It packages the installed, verified v1.0 client files for a second Windows 11 computer while preserving the `v1.0.0` tag and the frozen production source baseline. It must not be used to modify the server, the existing production service, or its active network configuration.

## Git

- Branch: `main`
- HEAD commit SHA: resolve locally with `git rev-parse v1.0.0^{commit}`. A commit cannot contain its own SHA in a tracked file.
- Tags: `v1.1.1` (current), `v1.1.0`, `v1.0.0` (annotated)
- Prior network baseline: `v0.9-network-verified`

## Golden rule

`C:\Projects\VPS-Tunnel` is source/development. `C:\Program Files\VPS Tunnel` is the production application. `C:\sing-box` holds the tunnel engine/configuration. AppData and ProgramData hold runtime settings and logs. Never run production from `bin`, `publish`, the old source directory, or Codex outputs.
