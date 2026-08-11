# VortexPass

WPF/.NET 8 x64 launcher for Windows 10/11 with a GitHub Releases updater.

В приложении доступны независимые профили YouTube, Discord, Telegram, Web и Game Filter, пять уровней обхода, автоматический подбор подходящего уровня по сетевой диагностике, управление защитой из системного трея и безопасное восстановление изменённых сетевых параметров.

## Запуск разработки

```powershell
dotnet build .\VortexPass.sln
dotnet run --project .\src\VortexPass.App\VortexPass.App.csproj
```

Приложение запрашивает права администратора при запуске. Внешние компоненты проверяются по SHA-256 до запуска; при несовпадении VortexPass не запускает сетевые процессы и показывает точную причину в журнале.

После `dist.bat` запускайте только `dist\VortexPass.exe`. Технический updater находится в `dist\updater` и запускать его вручную не нужно.

## Внешние компоненты

Поместите предоставленные проверенные файлы в `bin\` рядом с `VortexPass.exe`, затем заполните точные SHA-256 в `assets\external-components.json`. Не оставляйте поля `sha256` пустыми в релизной сборке.

## Публикация релиза

```powershell
.\build-release.ps1 -Version 0.1.0
```

Создайте в `avov53/VortexPass` релиз с тегом `v0.1.0` и загрузите два актива из `artifacts`:

- `VortexPass-win-x64.zip`
- `VortexPass-win-x64.zip.sha256`

ZIP должен содержать файлы приложения непосредственно в корне. Проверка обновлений использует GitHub API, сравнивает тег с версией сборки и устанавливает только ZIP с корректной SHA-256.
