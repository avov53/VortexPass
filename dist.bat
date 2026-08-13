@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "DIST=%ROOT%dist"
set "STAGING=%ROOT%dist.next"
set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=0.3.0"

echo.
echo === VortexPass %VERSION% ^| win-x64 Release build ===

powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Test-ComponentManifest.ps1" -ManifestPath "%ROOT%assets\external-components.json" -ComponentsRoot "%ROOT%components\zapret"
if errorlevel 1 (
  echo ERROR: component files do not match assets\external-components.json.
  goto :error
)

where pnpm >nul 2>&1
if not errorlevel 1 (
  pushd "%ROOT%src\VortexPass.App\web-src" || goto :error
  call pnpm run build
  if errorlevel 1 (
    popd
    goto :error
  )
  popd
) else (
  if not exist "%ROOT%src\VortexPass.App\web-dist\index.html" (
    echo ERROR: pnpm was not found and the prebuilt web interface is missing.
    goto :error
  )
  echo pnpm was not found; using the checked prebuilt web interface.
)

if exist "%STAGING%" rmdir /s /q "%STAGING%"
mkdir "%STAGING%" || goto :error
mkdir "%STAGING%\updater" || goto :error
mkdir "%STAGING%\bin" || goto :error

dotnet publish "%ROOT%src\VortexPass.App\VortexPass.App.csproj" -c Release -r win-x64 --self-contained true -p:Version=%VERSION% -p:AssemblyInformationalVersion=%VERSION% -o "%STAGING%"
if errorlevel 1 goto :error

dotnet publish "%ROOT%src\VortexPass.Updater\VortexPass.Updater.csproj" -c Release -r win-x64 --self-contained true -o "%STAGING%\updater"
if errorlevel 1 goto :error

xcopy "%ROOT%components\zapret\*" "%STAGING%\bin\" /E /I /Y >nul
if errorlevel 1 goto :error

powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Test-ComponentManifest.ps1" -ManifestPath "%STAGING%\assets\external-components.json" -ComponentsRoot "%STAGING%\bin"
if errorlevel 1 (
  echo ERROR: published component files failed manifest validation.
  goto :error
)

if not exist "%STAGING%\VortexPass.exe" (
  echo ERROR: VortexPass.exe was not created.
  goto :error
)
if not exist "%STAGING%\updater\VortexPass.Updater.exe" (
  echo ERROR: VortexPass.Updater.exe was not created.
  goto :error
)
if not exist "%STAGING%\assets\external-components.json" (
  echo ERROR: component manifest was not copied.
  goto :error
)
if not exist "%STAGING%\bin\winws.exe" (
  echo ERROR: winws component files were not copied.
  goto :error
)
if not exist "%STAGING%\bin\TgWsProxyHeadless\TgWsProxyHeadless.exe" (
  echo ERROR: Telegram Desktop headless WS Proxy was not copied.
  goto :error
)
if not exist "%STAGING%\bin\files\flowseal-hosts.txt" (
  echo ERROR: pinned Flowseal hosts asset was not copied.
  goto :error
)

tasklist /fi "imagename eq VortexPass.exe" /nh | find /i "VortexPass.exe" >nul
if not errorlevel 1 (
  echo.
  echo VortexPass is currently running, so its files in dist cannot be replaced.
  echo The new build is ready here:
  echo %STAGING%
  echo Close VortexPass in Task Manager, then run dist.bat again to replace dist automatically.
  exit /b 0
)

if exist "%DIST%" rmdir /s /q "%DIST%" >nul 2>&1
if exist "%DIST%" goto :locked
move "%STAGING%" "%DIST%" >nul || goto :error

echo.
echo Build complete:
echo %DIST%
echo Start the application with: VortexPass.exe
echo.
echo Verified winws, WinDivert and Telegram Desktop headless WS Proxy files were included in dist\bin.
exit /b 0

:error
echo.
echo Build failed. The dist folder may be incomplete.
exit /b 1

:locked
echo.
echo dist could not be replaced because one of its files is still open.
echo The new build is ready in %STAGING%
exit /b 1
