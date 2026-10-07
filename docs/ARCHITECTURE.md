# Architecture

## Principles

- **MVVM**: View (XAML) <-> ViewModel (`ObservableObject`) <-> Service (singleton).
- **Dependency injection**: lightweight hand-rolled `ServiceContainer` (no Microsoft.Extensions.DependencyInjection).
- **Single AudioService instance**: one engine per app, shared by all ViewModels.
- **Async-first**: all I/O (database, metadata, scanning) is async; the UI thread never blocks.

## Layers

```
+-------------------------------------------------+
|                   Views (XAML)                  |
|  MainWindow - LibraryView - SettingsView - ...  |
+------------------------+------------------------+
                         | data binding + commands
+------------------------v------------------------+
|                  ViewModels                     |
|  MainViewModel - LibraryViewModel - ...         |
+------------------------+------------------------+
                         | method calls
+------------------------v------------------------+
|                   Services                      |
|  LibraryService - AudioService - DpiBypass - ...|
+------------+-----------------+------------------+
             |                 |
      +------v------+   +------v---------+
      |  Database   |   |     Audio      |
      | SQLite +    |   | AudioEngine    |
      | Dapper      |   | (NAudio+WASAPI)|
      +-------------+   +----------------+
```

## Application lifecycle

1. `App.OnStartup` — creates `%LocalAppData%\BatPlayer\` (database, settings, cover cache, logs), migrates data from older versions, enforces single instance.
2. `DatabaseContext.InitializeAsync` — opens SQLite, applies schema and migrations.
3. `ServiceContainer.Build` — registers all services as singletons.
4. `MainWindow` resolves services, creates `MainViewModel`, calls `InitializeAsync()`.
5. `BatPlayer.Services.DpiBypass.StartWatchdog` — background probes; starts the packet-level bypass when access is blocked.
6. On exit — `AudioService.SaveStateAsync`, `SettingsService.SaveAsync`.

## Audio pipeline

```
File -> WaveStream reader -> SampleProvider -> EqualizerSampleProvider
                                             -> VolumeSampleProvider
                                             -> WasapiOut (shared/exclusive)
                                             -> audio endpoint
```

- `EqualizerSampleProvider` — 10 BiQuad peaking filters per channel, -12..+12 dB.
- `VolumeSampleProvider` — sample scaling for smooth volume changes.
- `WasapiOut` — native Windows Core Audio output, shared or exclusive mode.

## Networking layer (streaming sources)

- `SoundCloudHttp` / `VkHttp` / `YmHttp` — transport chain per service:
  direct -> local `DpiBypassProxy` (TLS ClientHello fragmentation) -> user/system proxy.
- `BatPlayer.Services.DpiBypass` — packet-level engine (WinDivert, see `Tools/bypass/`);
  started automatically when the transport chain fails, no UI.

## Database

SQLite schema lives in `Database/DatabaseContext.cs`:

| Table | Purpose |
|---|---|
| `tracks` | main track table with metadata |
| `artists`, `albums`, `genres` | dictionaries |
| `library_folders` | watched folders |
| `playlists`, `playlist_tracks` | playlists and contents |
| `history` | listening history |
| `playback_state` | saved state (current track, queue, volume) |
| `schema_version` | schema version for migrations |

Indexes on `title`, `artist`, `album`, `genre`, `date_added`, `is_favorite`, `playlist_id` keep search fast on tens of thousands of tracks.

## Playback data flow

1. Double-click a track -> `LibraryViewModel.PlayTrackCommand` -> `AudioService.PlayTrack(track, contextQueue)`.
2. `AudioService` builds the queue and calls `AudioEngine.Open(filePath, exclusiveMode, device)`.
3. `AudioEngine` creates the reader and chains `EqualizerSampleProvider -> VolumeSampleProvider -> WasapiOut.Init`.
4. `WasapiOut.Play` starts buffered playback; a 250 ms timer updates `Position`.
5. On track end `PlaybackStopped` -> `AudioService.OnPlaybackStopped` -> `Next()`.

## State persistence

- `AudioService.SaveStateAsync` serializes playback state into the `playback_state` table.
- `SettingsService.SaveAsync` writes `settings.json` atomically (`.tmp` + `File.Move`).
- On startup `RestoreStateAsync` rebuilds the queue and resumes playback when `AutoResumePlayback=true`.
