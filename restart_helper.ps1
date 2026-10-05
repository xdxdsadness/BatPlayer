Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'helper\.ps1' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Stop-Process -Name 'winws' -Force -ErrorAction SilentlyContinue
Stop-Process -Name 'SoundCloud lock bypass' -Force -ErrorAction SilentlyContinue
