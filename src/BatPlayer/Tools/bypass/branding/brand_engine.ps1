# Rebuilds the branded engine copy: bin\engine.exe -> bin\BatPlayer bypass.exe.
# Task Manager shows the FileDescription and icon of the exe, so the engine appears
# as a Bat Player helper process. Run after every engine.exe or app.ico update.
# Tool: rcedit (https://github.com/electron/rcedit, MIT).
$ErrorActionPreference = 'Stop'
$branding = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = [System.IO.Path]::GetFullPath((Join-Path $branding '..\bin'))
$src = Join-Path $bin 'engine.exe'
$dst = Join-Path $bin 'BatPlayer bypass.exe'
$ico = [System.IO.Path]::GetFullPath((Join-Path $branding '..\..\..\Resources\app.ico'))

if (-not (Test-Path $src)) { Write-Error "engine.exe not found: $src"; exit 1 }
if (-not (Test-Path $ico)) { Write-Error "app.ico not found: $ico"; exit 1 }

Copy-Item $src $dst -Force

$rcedit = Join-Path $branding 'rcedit-x64.exe'
& $rcedit $dst `
    --set-icon $ico `
    --set-version-string "FileDescription" "BatPlayer bypass" `
    --set-version-string "ProductName" "Bat Player" `
    --set-version-string "CompanyName" "BatPlayer" `
    --set-version-string "OriginalFilename" "BatPlayer bypass.exe" `
    --set-version-string "LegalCopyright" "engine (zapret, MIT); rebranded for Bat Player" `
    --set-file-version "1.0.0.0" `
    --set-product-version "1.0.0.0" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "rcedit failed"; exit 1 }

# Windows reads the string VersionInfo block only when numeric versions are present.
& $rcedit $dst --set-file-version "1.0.0.0" | Out-Null
& $rcedit $dst --set-product-version "1.0.0.0" | Out-Null

Write-Host "Branded: $dst"
(Get-Item $dst).VersionInfo | Format-List FileDescription, ProductName, OriginalFilename
