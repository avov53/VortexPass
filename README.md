# VortexPass


WPF/.NET 8 x64 launcher for Windows 10/11 with a GitHub Releases updater.

В приложении доступны независимые профили YouTube, Discord, Telegram, Web и Game Filter, пять понятных уровней обхода, автоматическая проверка 20 внутренних стратегий, управление защитой из системного трея и безопасное восстановление изменённых сетевых параметров.

## Запуск разработки

```powershell
dotnet build .\VortexPass.sln
dotnet run --project .\src\VortexPass.App\VortexPass.App.csproj
```

Приложение запрашивает права администратора при запуске. Внешние компоненты проверяются по SHA-256 до запуска; при несовпадении VortexPass не запускает сетевые процессы и показывает точную причину в журнале.
