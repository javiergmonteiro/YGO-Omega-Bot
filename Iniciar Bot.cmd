@echo off
title Bot YGO Omega
cd /d "%~dp0BotTest"

echo Compilando el bot...
dotnet build -nologo -v q
if errorlevel 1 (
  echo.
  echo No se pudo compilar. Revisa los errores de arriba.
  pause
  exit /b 1
)

echo.
echo Abriendo el panel en el navegador: http://localhost:58966/control.html
echo Deja esta ventana abierta mientras jugas. Para apagar el bot, cerrala.
echo.
start "" "http://localhost:58966/control.html"
dotnet run --no-build --no-launch-profile --urls http://localhost:58966
pause
