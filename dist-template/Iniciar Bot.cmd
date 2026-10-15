@echo off
title Bot YGO Omega
cd /d "%~dp0"
echo Abriendo el panel en el navegador: http://localhost:58966/control.html
echo Deja esta ventana abierta mientras jugas. Para apagar el bot, cerrala.
echo.
start "" "http://localhost:58966/control.html"
BotTest.exe --urls http://localhost:58966 --logs logs
pause
