@echo off
cd /d "%~dp0"
if not exist "standalone.html" call npm run build
echo VortexPass design: http://127.0.0.1:5174/
echo Keep this window open while viewing the design.
call npm run dev
pause
