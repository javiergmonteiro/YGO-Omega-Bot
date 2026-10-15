@echo off
title Empaquetar Bot YGO Omega
cd /d "%~dp0"

echo Publicando el bot (autocontenido, no necesita .NET instalado)...
if exist dist\BotYGOOmega rmdir /s /q dist\BotYGOOmega
dotnet publish BotTest\BotTest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\BotYGOOmega -nologo -v q
if errorlevel 1 (
  echo No se pudo publicar. Revisa los errores de arriba.
  pause
  exit /b 1
)
del /q dist\BotYGOOmega\*.pdb dist\BotYGOOmega\web.config 2>nul

rem Launcher and README for the package (kept in dist-template\).
copy /y "dist-template\Iniciar Bot.cmd" dist\BotYGOOmega\ >nul
copy /y "dist-template\LEEME.txt" dist\BotYGOOmega\ >nul

echo Comprimiendo...
powershell -NoProfile -Command "Compress-Archive -Path 'dist\BotYGOOmega' -DestinationPath 'dist\BotYGOOmega.zip' -Force"
echo.
echo Listo: dist\BotYGOOmega.zip
pause
