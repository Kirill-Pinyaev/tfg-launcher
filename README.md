# TFG Launcher

Минималистичный offline-лаунчер TerraFirmaGreg для Windows 10/11 x64.

## Сборка и установщик

```powershell
dotnet publish -c Release -r win-x64
./build-installer.sh
```

Готовые файлы: `bin/publish/TFG Launcher.exe` и
`artifacts/TFG-Launcher-Setup-1.1.0.exe`.

Лаунчер хранит Java, Minecraft, сборку и настройки в `%LocalAppData%\TFGLauncher`.
Версия клиента берётся из метки `[TFG:x.y.z]` в MOTD сервера. Устанавливается полный
официальный `multimc.zip`: официальный `.mrpack` 0.13.7 не содержит обязательный мод
DeaFission. Архив проверяется по SHA-256 из GitHub Releases. Для самопроверки:

Кнопка «Скин» формирует команды установленного на сервере SkinRestorer для Mojang,
Ely.by, HTTPS PNG (Classic/Slim) и сброса. Команду нужно вставить в игровой чат.
Публичная запись скина без авторизации намеренно не добавлена.

Обновления launcher объявляются через `/api/v1/launcher`, скачиваются как installer
из GitHub Releases и принимаются только после проверки SHA-256, действительной
Authenticode-подписи и закреплённого SHA-256 thumbprint сертификата издателя.

Для подключения сначала используется публичный адрес `77.51.139.159`. Если роутер
не поддерживает NAT loopback, лаунчер автоматически использует локальный адрес
`192.168.1.78`.

```powershell
& '.\TFG Launcher.exe' --self-test
```

Используются MIT-библиотеки [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) и
[CmlLib.Core.Installer.Forge](https://github.com/CmlLib/CmlLib.Core.Installer.Forge).
