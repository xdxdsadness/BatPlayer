# Installer

## Requirements

- Inno Setup 6.2+ — [download](https://jrsoftware.org/isinfo.php)
- A built `publish/` directory (see [BUILD.md](BUILD.md))

## Steps

```bash
# 1. Publish the app (see BUILD.md)
dotnet publish src/BatPlayer/BatPlayer.csproj -c Release -r win-x64 --self-contained true -o publish

# 2. Compile the installer
iscc installer\bat_player.iss
```

Result: `installer/dist/BatPlayer-Setup-1.0.0.exe`.

## What the installer creates

- `%LocalAppData%\Programs\Bat Player\` (program files, per-user, no admin required)
- Start Menu folder and optional desktop shortcut / startup entry
- An entry in "Programs and Features"

## Uninstall

Via "Programs and Features" (`appwiz.cpl`). User data in `%LocalAppData%\BatPlayer\`
(library, accounts, settings, caches) is preserved by default; to wipe it too,
uncomment the `{localappdata}\BatPlayer` line in the `[UninstallDelete]` section
of `bat_player.iss`.

## Silent install

```bash
BatPlayer-Setup-1.0.0.exe /VERYSILENT /NORESTART /CURRENTUSER
```

## Customization

In `installer/bat_player.iss`: `#define MyAppVersion`, `#define MyAppPublisher`,
`Compression`, `WizardStyle`.
