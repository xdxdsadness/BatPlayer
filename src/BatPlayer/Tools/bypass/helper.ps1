# Elevated helper for the built-in DPI bypass (started by the player via UAC).
# File protocol in this folder:
#   command.txt = "preset:<base64 engine arguments>" - kill the old engine, start a new one
#   command.txt = "stop"                             - kill the engine (the helper keeps running)
#   status.txt  = "busy" | "applied" | "dead" | "stopped"
# The helper exits on its own once the player process (BatPlayer) is gone.

$ErrorActionPreference = 'SilentlyContinue'
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $base 'bin'
$cmdFile = Join-Path $base 'command.txt'
$statusFile = Join-Path $base 'status.txt'
$engineName = 'BatPlayer bypass'   # process name = exe name without extension
$engineExe = Join-Path $bin ($engineName + '.exe')

# Fall back to the plain engine.exe while the branded copy is not built.
if (-not (Test-Path $engineExe)) {
    $engineExe = Join-Path $bin 'engine.exe'
    $engineName = 'engine'
}
Set-Location $bin

function Test-AppAlive {
    return [bool](Get-Process -Name 'BatPlayer' -ErrorAction SilentlyContinue)
}

function Stop-Engine {
    # Current name plus names of older builds and the plain engine.
    Stop-Process -Name $engineName -Force -ErrorAction SilentlyContinue
    Stop-Process -Name 'engine' -Force -ErrorAction SilentlyContinue
    Stop-Process -Name 'winws' -Force -ErrorAction SilentlyContinue
    Stop-Process -Name 'SoundCloud lock bypass' -Force -ErrorAction SilentlyContinue
    Stop-Process -Name 'Bat lock bypass' -Force -ErrorAction SilentlyContinue
}

while ($true) {
    if (-not (Test-AppAlive)) {
        Stop-Engine
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
                Stop-Engine
                Set-Content -Path $statusFile -Value 'stopped'
                Remove-Item $cmdFile -ErrorAction SilentlyContinue
            }
            elseif ($cmd.StartsWith('preset:')) {
                $b64 = $cmd.Substring(7).Trim()
                $argsStr = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
                # {BASE} -> the real Tools/bypass folder (presets are machine-independent)
                $argsStr = $argsStr.Replace('{BASE}', $base)
                Stop-Engine
                Start-Sleep -Milliseconds 400
                Start-Process -FilePath $engineExe -ArgumentList $argsStr `
                    -WorkingDirectory $bin -WindowStyle Hidden
                Start-Sleep -Milliseconds 1800
                $alive = Get-Process -Name $engineName -ErrorAction SilentlyContinue
                Set-Content -Path $statusFile -Value $(if ($alive) { 'applied' } else { 'dead' })
                Remove-Item $cmdFile -ErrorAction SilentlyContinue
            }
        }
    }

    Start-Sleep -Milliseconds 250
}
