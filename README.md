# TFG Launcher

Минималистичный offline-лаунчер TerraFirmaGreg для Windows 10/11 x64.

## Сборка и установщик

```powershell
dotnet publish -c Release -r win-x64
./build-installer.sh
```

Готовые файлы: `bin/publish/TFG Launcher.exe` и
`artifacts/TFG-Launcher-Setup-1.1.1.exe`.

Лаунчер хранит Java, Minecraft, сборку и настройки в `%LocalAppData%\TFGLauncher`.
Версия клиента берётся из метки `[TFG:x.y.z]` в MOTD сервера. Устанавливается полный
официальный `multimc.zip`: официальный `.mrpack` 0.13.7 не содержит обязательный мод
DeaFission. Архив проверяется по SHA-256 из GitHub Releases. Для самопроверки:

Кнопка «Скин» формирует команды установленного на сервере SkinRestorer для Mojang,
Ely.by, HTTPS PNG (Classic/Slim) и сброса. Команду нужно вставить в игровой чат.
Публичная запись скина без авторизации намеренно не добавлена.

Обновления launcher объявляются через `/api/v1/launcher`, скачиваются как installer
из GitHub Releases и проверяются по размеру, SHA-256 и собственной ECDSA P-256 подписи.
Публичный ключ встроен в launcher; закрытый ключ хранится вне репозитория. Windows
по-прежнему показывает «Неизвестный издатель», так как это не Authenticode.

GitHub Actions автоматически собирает Release при отправке тега `v*`:

```bash
git tag v1.1.1
git push origin main v1.1.1
```

Перед первым релизом добавьте содержимое `update-private.pem` в GitHub Actions secret
`TFG_UPDATE_SIGNING_KEY`. Release содержит installer, `SHA256SUMS.txt` и detached-файл `.sig`.

Для подключения сначала используется публичный адрес `77.51.139.159`. Если роутер
не поддерживает NAT loopback, лаунчер автоматически использует локальный адрес
`192.168.1.78`.

```powershell
& '.\TFG Launcher.exe' --self-test
```

Используются MIT-библиотеки [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) и
[CmlLib.Core.Installer.Forge](https://github.com/CmlLib/CmlLib.Core.Installer.Forge).
