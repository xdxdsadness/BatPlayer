# Превращает winws.exe в "SoundCloud lock bypass.exe":
# копия бинарника с иконкой Bat Player и описанием "SoundCloud lock bypass" —
# именно их диспетчер задач показывает в списке процессов (имя = FileDescription,
# иконка = ресурс иконки exe). Запускать после каждого обновления winws.exe.
# Инструмент: rcedit (https://github.com/electron/rcedit, MIT).
$ErrorActionPreference = 'Stop'
$branding = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = [System.IO.Path]::GetFullPath((Join-Path $branding '..\bin'))
$src = Join-Path $bin 'winws.exe'
$dst = Join-Path $bin 'SoundCloud lock bypass.exe'
$ico = [System.IO.Path]::GetFullPath((Join-Path $branding '..\..\..\Resources\app.ico'))

if (-not (Test-Path $src)) { Write-Error "winws.exe not found: $src"; exit 1 }
if (-not (Test-Path $ico)) { Write-Error "app.ico not found: $ico"; exit 1 }

Copy-Item $src $dst -Force

$rcedit = Join-Path $branding 'rcedit-x64.exe'
& $rcedit $dst `
    --set-icon $ico `
    --set-version-string "FileDescription" "SoundCloud lock bypass" `
    --set-version-string "ProductName" "Bat Player" `
    --set-version-string "CompanyName" "BatPlayer" `
    --set-version-string "OriginalFilename" "SoundCloud lock bypass.exe" `
    --set-version-string "LegalCopyright" "winws (c) bol-van (zapret, MIT); rebranded for Bat Player" `
    --set-file-version "1.10.3.0" `
    --set-product-version "1.10.3.0" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "rcedit failed"; exit 1 }

# Без числовых FileVersion/ProductVersion Windows не читает строковый блок VersionInfo.
& $rcedit $dst --set-file-version "1.10.3.0" | Out-Null
& $rcedit $dst --set-product-version "1.10.3.0" | Out-Null

Write-Host "Branded: $dst"
(Get-Item $dst).VersionInfo | Format-List FileDescription, ProductName, OriginalFilename
