# Повышенный хелпер антиблокировки SoundCloud (запускается приложением через UAC).
# Протокол через файлы в этой папке:
#   command.txt = "preset:<base64 winws-аргументов>" — убить старый процесс, запустить новый
#   command.txt = "stop"                              — убить процесс (хелпер остаётся жить)
#   status.txt  = "busy" | "applied" | "dead" | "stopped"
# Хелпер завершается сам, когда процесс плеера (BatPlayer) больше не запущен.
#
# Запускается не winws.exe, а его брендированная копия "SoundCloud lock bypass.exe"
# (иконка и FileDescription Bat Player — см. branding/brand_winws.ps1), чтобы в
# диспетчере задач процесс выглядел побочным инструментом плеера, а не отдельным exe.
$ErrorActionPreference = 'SilentlyContinue'
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $base 'bin'
$cmdFile = Join-Path $base 'command.txt'
$statusFile = Join-Path $base 'status.txt'
$bypassName = 'SoundCloud lock bypass'   # имя процесса = имя exe без расширения
$bypassExe = Join-Path $bin ($bypassName + '.exe')

# Фолбэк на оригинальный winws.exe, если брендированная копия ещё не собрана.
if (-not (Test-Path $bypassExe)) {
    $bypassExe = Join-Path $bin 'winws.exe'
    $bypassName = 'winws'
}
Set-Location $bin

function Test-AppAlive {
    return [bool](Get-Process -Name 'BatPlayer' -ErrorAction SilentlyContinue)
}

function Stop-Bypass {
    # Брендированный процесс + легаси-имя winws (например, от автономных .bat).
    Stop-Process -Name $bypassName -Force -ErrorAction SilentlyContinue
    Stop-Process -Name 'winws' -Force -ErrorAction SilentlyContinue
}

while ($true) {
    if (-not (Test-AppAlive)) {
        Stop-Bypass
        Remove-Item $cmdFile -ErrorAction SilentlyContinue
        Remove-Item $statusFile -ErrorAction SilentlyContinue
        break
    }

    if (Test-Path $cmdFile) {
        $cmd = (Get-Content $cmdFile -Raw -ErrorAction SilentlyContinue)
        if ($cmd) {
            $cmd = $cmd.Trim()
            Set-Content -Path $statusFile -Value 'busy'

            if ($cmd -eq 'stop') {
                Stop-Bypass
                Set-Content -Path $statusFile -Value 'stopped'
                Remove-Item $cmdFile -ErrorAction SilentlyContinue
            }
            elseif ($cmd.StartsWith('preset:')) {
                $b64 = $cmd.Substring(7).Trim()
                $argsStr = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
                # {BASE} -> реальная папка Tools/zapret (пресеты машинно-независимы)
                $argsStr = $argsStr.Replace('{BASE}', $base)
                Stop-Bypass
                Start-Sleep -Milliseconds 400
                Start-Process -FilePath $bypassExe -ArgumentList $argsStr `
                    -WorkingDirectory $bin -WindowStyle Hidden
                Start-Sleep -Milliseconds 1800
                $alive = Get-Process -Name $bypassName -ErrorAction SilentlyContinue
                Set-Content -Path $statusFile -Value $(if ($alive) { 'applied' } else { 'dead' })
                Remove-Item $cmdFile -ErrorAction SilentlyContinue
            }
        }
    }

    Start-Sleep -Milliseconds 250
}
