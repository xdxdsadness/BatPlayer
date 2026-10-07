@echo off
chcp 65001 > nul
:: Rebuild the publish output. Close the player first (BatPlayer.exe is locked while running).
cd /d "%~dp0"
dotnet publish src/BatPlayer/BatPlayer.csproj -c Release -r win-x64 --self-contained true -o publish
pause
