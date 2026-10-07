# Configuration

`settings.json` is created automatically on first launch:

```
%LocalAppData%\BatPlayer\settings.json
```

Full example: [example.settings.json](example.settings.json).

## Key fields

| Field | Description | Example |
|---|---|---|
| `language` | UI language (`ru` / `en`) | `"en"` |
| `libraryFolders` | Music folders | `["C:\\Music"]` |
| `audioOutputDevice` | Output device FriendlyName | `""` = system default |
| `useWasapiExclusive` | WASAPI exclusive mode | `false` |
| `gaplessPlayback` | No gaps between tracks | `false` |
| `crossfadeEnabled` / `crossfadeDurationMs` | Crossfade | `false` / `3000` |
| `equalizerEnabled` / `currentEqualizerPreset` | Equalizer | `false` / `"Flat"` |
| `dpiBypassEnabled` | Built-in packet-level DPI bypass | `true` |
| `soundCloudProxy` | `""` = auto, `"off"` = direct only, or an explicit proxy | `"socks5://127.0.0.1:1080"` |
| `windowWidth/Height` | Window size | `1280`, `800` |

## Database

SQLite file: `%LocalAppData%\BatPlayer\obsidian.db`. All tables are created on first
launch; the schema lives in `src/BatPlayer/Database/DatabaseContext.cs`.

## Logs

```
%LocalAppData%\BatPlayer\logs\app_YYYYMMDD.log
```

Line format: `HH:mm:ss.fff [LEVEL] message`.
