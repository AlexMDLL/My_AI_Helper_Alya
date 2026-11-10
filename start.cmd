@echo off
setlocal
rem Запуск Джарвиса без консоли. Папка проекта = папка этого файла.
set "ELECTRON_RUN_AS_NODE="
cd /d "%~dp0"
if not exist "out\main\index.js" (
  echo Сначала собери приложение: pnpm build
  pause
  exit /b 1
)
start "" "node_modules\electron\dist\electron.exe" .
