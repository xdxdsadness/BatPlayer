# Build

## Requirements

| Component | Version | Purpose |
|---|---|---|
| Windows | 10 1903+ / 11 | WPF requires Windows |
| .NET 8 SDK | 8.0.x | [download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Inno Setup | 6.2+ | installer only (optional) |

## Debug build

```bash
dotnet build
dotnet run --project src/BatPlayer/BatPlayer.csproj
```

Or open `BatPlayer.sln` in Visual Studio 2022 and press F5.

## Release build

```bash
dotnet publish src/BatPlayer/BatPlayer.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish
```

Result: `publish/BatPlayer.exe` — self-contained, no .NET install required on the target machine.
Size: ~150 MB single-file, ~70 MB after installer compression.

## Installer

```bash
# step 2 output is required first
iscc installer\bat_player.iss
```

Result: `installer/dist/BatPlayer-Setup-1.0.0.exe`. See [INSTALLER.md](INSTALLER.md).

## Tests

```bash
dotnet test
```

## Third-party tools

Binaries under `src/BatPlayer/Tools/` (bypass/bin) are not tracked by git
and are not required to build — only at runtime for streaming and the DPI bypass.
See "Third-party tools" in the README for sources.

## Which exe is which

| Path | What it is |
|---|---|
| `%LocalAppData%\Programs\Bat Player\BatPlayer.exe` | **Installed program** — launched by the desktop / Start Menu shortcuts; updated by reinstalling |
| `installer/dist/BatPlayer-Setup-1.0.0.exe` | **Installer** |
| `publish/BatPlayer.exe` | Intermediate release build used by the installer; safe to delete |
| `src/BatPlayer/bin/Debug/.../BatPlayer.exe` | Debug build (`dotnet build` / F5), recreated on every build |
| `Tools/bypass/bin/*` | Bypass engine helper processes — not the player, started on demand |

## Troubleshooting

- **"WPF requires Windows"** — building on Linux/macOS; use a Windows machine.
- **"Could not find taglib-sharp"** — run `dotnet restore`.
- **Large exe** — expected for self-contained .NET; `PublishTrimmed=true` can reduce it but may break NAudio reflection.
