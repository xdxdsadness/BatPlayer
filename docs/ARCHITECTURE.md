# Архитектура Bat Player

## Общие принципы

- **MVVM**: View (XAML) ↔ ViewModel (`ObservableObject`) ↔ Service (`singleton`).
- **Dependency Injection**: лёгкий ручной `ServiceContainer` без Microsoft.Extensions.DependencyInjection (меньше зависимостей, тот же эффект).
- **Single instance of AudioService**: один движок на всё приложение, переиспользуется всеми ViewModel.
- **Async-first**: все I/O операции (БД, метаданные, сканирование) — `async/await`, UI не блокируется.

## Слои

```
┌─────────────────────────────────────────────────┐
│                   Views (XAML)                  │
│  MainWindow · LibraryView · SettingsView ·      │
│  EqualizerView · PlaylistView · NowPlayingWindow│
└──────────────────┬──────────────────────────────┘
                   │ DataBinding + Commands
┌──────────────────▼──────────────────────────────┐
│                  ViewModels                     │
│  MainViewModel · PlayerBarViewModel ·           │
│  LibraryViewModel · SettingsViewModel ·         │
│  EqualizerViewModel · PlaylistViewModel ·       │
│  SearchViewModel                                │
└──────────────────┬──────────────────────────────┘
                   │ method calls
┌──────────────────▼──────────────────────────────┐
│                   Services                      │
│  LibraryService · AudioService ·                │
│  MetadataService · PlaylistService ·            │
│  SettingsService · HistoryService ·             │
│  SearchService · CoverCacheService ·            │
│  TrayService · GlobalHotkeyService ·            │
│  EqualizerService                               │
└──────────────────┬──────────────────────────────┘
                   │
        ┌──────────┴──────────┐
        ▼                     ▼
┌──────────────┐    ┌──────────────────┐
│  Database    │    │     Audio        │
│  SQLite +    │    │  AudioEngine     │
│  Dapper      │    │  (NAudio+WASAPI) │
└──────────────┘    │  Equalizer       │
                    │  VolumeProvider  │
                    └──────────────────┘
```

## Жизненный цикл приложения

1. `App.OnStartup` — создаёт директории `%LocalAppData%\BatPlayer\` (`obsidian.db`, `settings.json`, `cover_cache/`, `logs/`).
2. `DatabaseContext.InitializeAsync` — открывает SQLite, выполняет schema SQL, миграции.
3. `ServiceContainer.Build` — регистрирует все сервисы как singletons.
4. `MainWindow` конструктор — резолвит сервисы, создаёт `MainViewModel`, вызывает `_vm.InitializeAsync()`.
5. `MainViewModel.InitializeAsync` — инициализирует трей, загружает библиотеку, восстанавливает состояние плеера.
6. На закрытии — `AudioService.SaveStateAsync`, `SettingsService.SaveAsync`.

## Audio pipeline

```
File → WaveStream reader → SampleProvider → EqualizerSampleProvider
                                              → VolumeSampleProvider
                                              → WasapiOut (shared/exclusive)
                                              → Audio Endpoint ( Speakers / Headphones )
```

- `EqualizerSampleProvider` — 10 BiQuad-фильтров (PeakingEQ) на канал, gain -12..+12 dB.
- `VolumeSampleProvider` — масштабирование семплов для плавной регулировки громкости.
- `WasapiOut` — нативный вывод через Windows Core Audio. Поддерживает shared (default) и exclusive режим.

## База данных

SQLite-схема (см. `Database/DatabaseContext.cs`):

| Таблица | Назначение |
|---|---|
| `tracks` | основная таблица треков с метаданными |
| `artists`, `albums`, `genres` | справочники |
| `library_folders` | отслеживаемые папки |
| `playlists`, `playlist_tracks` | плейлисты и состав |
| `history` | история прослушиваний |
| `playback_state` | сохранённое состояние (текущий трек, очередь, громкость) |
| `schema_version` | версия схемы для миграций |

Индексы: на `title`, `artist`, `album`, `genre`, `date_added`, `is_favorite`, `playlist_id` — обеспечивают быстрый поиск даже на десятках тысяч треков.

## Производительность

- **Кэш обложек на диске** — SHA256 от байтов обложки как имя файла, не переизвлекается при повторном открытии.
- **Виртуализация списков** — WPF `ListView` по умолчанию виртуализирует элементы, рендерит только видимые.
- **Debounced search** — 200ms задержка перед выполнением поиска.
- **Connection pooling** — `Cache=Shared;Pooling=True` в строке подключения SQLite.
- **Фоновое сканирование** — `Task.Run` для метаданных, UI поток не блокируется.

## Поток данных при воспроизведении

1. Пользователь дабл-кликает трек → `LibraryViewModel.PlayTrackCommand` → `AudioService.PlayTrack(track, contextQueue)`.
2. `AudioService` формирует очередь, вызывает `AudioEngine.Open(filePath, exclusiveMode, device)`.
3. `AudioEngine` создаёт reader, прокидывает через `EqualizerSampleProvider → VolumeSampleProvider → WasapiOut.Init`.
4. `WasapiOut.Play` запускает буферизированное воспроизведение.
5. `AudioService._positionTimer` (250ms) обновляет `Position` через dispatcher.
6. По окончании трека `WasapiOut.PlaybackStopped` → `AudioService.OnPlaybackStopped` → `Next()`.

## Сохранение состояния

- `AudioService.SaveStateAsync` сериализует `PlaybackState` (current track, queue, volume, shuffle/repeat) в `playback_state` таблицу.
- `SettingsService.SaveAsync` пишет JSON в `settings.json` (атомарно через `.tmp` + `File.Replace`).
- На старте `RestoreStateAsync` поднимает очередь из БД и возобновляет воспроизведение если `AutoResumePlayback=true`.
