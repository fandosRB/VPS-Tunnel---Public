# VPS Tunnel

Клиент для Windows, который направляет интернет-трафик через **ваш собственный сервер VLESS + Reality**. Внутри работает [sing-box](https://github.com/SagerNet/sing-box).

**Режимы**
- **DIRECT** — обычный интернет, без VPS.
- **TUNNEL** — весь трафик компьютера (включая WSL) идёт через ваш сервер.
- **SELECTIVE** — через сервер идут только выбранные программы (например, браузер, Telegram, ChatGPT, Claude) и, по желанию, весь WSL/Ubuntu; остальное — напрямую.

**Возможности:** настройка сервера вставкой ссылки `vless://…` из панели (3x-ui, Marzban, Hiddify и др.), выбор программ из установленных и запущенных, значок в трее, автозапуск, светлая и тёмная тема, показ внешнего IP, страны и трафика.

## Установка

1. Нужны Windows 10/11 (64-бит) и [Microsoft .NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) — установщик сам предложит его скачать, если его нет.
2. Скачайте `VPS-Tunnel-Setup.exe` со страницы [Releases](../../releases/latest) и запустите.
3. При первом запуске вставьте ссылку `vless://…` на свой сервер.

Подробно — в [ИНСТРУКЦИЯ.md](ИНСТРУКЦИЯ.md).

> Установщик не подписан цифровой подписью, поэтому Windows может показать «Windows защитила ваш компьютер». Нажмите «Подробнее» → «Выполнить в любом случае».

Сервер в комплект не входит: программа подключается только к тому серверу, который вы укажете. Ключи хранятся только на вашем компьютере, в `C:\sing-box\config.json`, доступном лишь системе и администраторам.

## Сборка из исходников

```powershell
dotnet test -c Release
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Нужны .NET 10 SDK и sing-box 1.14.1 для Windows в `C:\sing-box\sing-box-1.14.1-windows-amd64`. Результат — `installer\dist\VPS-Tunnel-Setup.exe`; подробности в [installer/README.md](installer/README.md), устройство программы — в [PROJECT_STATE.md](PROJECT_STATE.md).

| Проект | Назначение |
| --- | --- |
| `VPS.Tunnel.App` | Окно (WPF, .NET 10) |
| `VPS.Tunnel.Service` | Служба Windows `VpsTunnelService`, запускает sing-box |
| `VPS.Tunnel.Core` | Общие модели, разбор `vless://`, генерация конфигурации sing-box |
| `installer/` | Установщик `VPS-Tunnel-Setup.exe` |

## Лицензия

Код VPS Tunnel — [MIT](LICENSE). sing-box, входящий в установщик, распространяется по GPL-3.0 — см. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Пользуйтесь программой в соответствии с законами вашей страны.
