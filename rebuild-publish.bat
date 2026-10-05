@echo off
chcp 65001 > nul
:: Пересборка publish-версии плеера. Закройте плеер перед запуском (файл BatPlayer.exe занят, пока он работает).
cd /d "%~dp0"
dotnet publish src/BatPlayer/BatPlayer.csproj -c Release -r win-x64 --self-contained true -o publish
pause
