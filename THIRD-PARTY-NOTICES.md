# Third-party software

The installer `VPS-Tunnel-Setup.exe` includes, unmodified:

| Component | Version | License | Source |
| --- | --- | --- | --- |
| sing-box (`sing-box.exe`, `libcronet.dll`) | 1.14.1, windows-amd64 | GPL-3.0-or-later | https://github.com/SagerNet/sing-box/tree/v1.14.1 — release archive: https://github.com/SagerNet/sing-box/releases/tag/v1.14.1 |

sing-box is installed to `C:\sing-box\sing-box-1.14.1-windows-amd64` together with its `LICENSE`. VPS Tunnel starts it as a separate program; VPS Tunnel's own code is licensed under the MIT License (see `LICENSE`).

The application uses Microsoft .NET 10 (MIT License), which users install separately as the .NET Desktop Runtime.

At runtime the application queries `api.ipify.org` (public IP) and `ipapi.co` (country) to show the connection status.
