# VPS Tunnel installer

`VPS-Tunnel-Setup.exe` is the only installer: for a first install, for updates and for giving the program to other people.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

The script publishes the GUI and the service **from the source code in this repository**, takes the sing-box engine from `C:\sing-box\sing-box-1.14.1-windows-amd64`, and writes `installer\dist\VPS-Tunnel-Setup.exe`. It never includes `config.json` or other connection settings, so the EXE can be shared.

## What the setup does

- Shows one page: shortcut in the Start menu, shortcut on the desktop, start the app afterwards.
- Installs the GUI to `C:\Program Files\VPS Tunnel` and the engine to `C:\sing-box` (an existing `config.json` is left untouched).
- Registers `VpsTunnelService` (LocalSystem, Manual start, startable by the installing user) and leaves it stopped.
- Registers the program in Settings → Apps (Add/Remove Programs) with its version, icon and size.
- Updating over an existing installation closes the GUI and stops the tunnel first; settings and the program list are kept.
- Starts the GUI as the normal (unelevated) user.
- Requires Windows 10/11 x64 and the Microsoft .NET 10 Desktop Runtime (x64); offers the download page when it is missing.
- `--repair-service` (used by the GUI's "Настроить" button) only re-registers the service for the current user.

## Uninstall

Settings → Apps → VPS Tunnel → Uninstall. The uninstaller continues from a temporary copy (a program cannot delete its own folder), closes the GUI, removes the service, shortcuts, autostart entry, the application folder and the engine. It asks whether to also delete the connection settings (`config.json`). User settings and logs in `%LOCALAPPDATA%` are kept.

sing-box is licensed under GPL-3.0; its `LICENSE` is installed with the engine. Source: https://github.com/SagerNet/sing-box.
