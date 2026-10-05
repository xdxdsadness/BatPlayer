# Пример конфигурации

Файл `settings.json` создаётся автоматически при первом запуске в:
```
%LocalAppData%\BatPlayer\settings.json
```

Полный пример — см. [example.settings.json](example.settings.json).

## Ключевые поля

| Поле | Описание | Пример |
|---|---|---|
| `language` | Код языка (`ru` / `en`) | `"ru"` |
| `libraryFolders` | Список путей к музыке | `["C:\\Music", "D:\\Audio"]` |
| `audioOutputDevice` | FriendlyName устройства | `""` = системное по умолчанию |
| `useWasapiExclusive` | Эксклюзивный режим WASAPI | `false` |
| `gaplessPlayback` | Без пауз между треками | `false` |
| `crossfadeEnabled` | Плавный переход | `false` |
| `crossfadeDurationMs` | Длительность crossfade (мс) | `3000` |
| `equalizerEnabled` | Включить эквалайзер | `false` |
| `currentEqualizerPreset` | Активный пресет | `"Flat"` |
| `sidebarVisible` | Показать боковую панель | `true` |
| `animationsEnabled` | Анимации UI | `true` |
| `windowWidth/Height` | Размер окна при запуске | `1280`, `800` |

## Структура базы данных

SQLite-файл: `%LocalAppData%\BatPlayer\obsidian.db`

Все таблицы создаются автоматически при первом запуске. Схема — в `src/BatPlayer/Database/DatabaseContext.cs`.

## Пример содержимого БД

```sql
-- Tracks
INSERT INTO tracks (file_path, title, artist, album, genre, year, duration_ticks, bitrate, sample_rate, channels, format, date_added)
VALUES ('C:\Music\test.mp3', 'Bohemian Rhapsody', 'Queen', 'A Night at the Opera', 'Rock', 1975, 35400000000, 320, 44100, 2, 'MP3', '2026-01-01T00:00:00Z');

-- Playlists
INSERT INTO playlists (name, created_at, updated_at) VALUES ('My Favorites', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

-- Playlist items
INSERT INTO playlist_tracks (playlist_id, track_id, position) VALUES (1, 1, 0);

-- Playback state (singleton row, id=1)
INSERT INTO playback_state (id, current_track_id, last_position_ticks, volume, is_muted, is_shuffle, repeat_mode, queue_track_ids, queue_index, updated_at)
VALUES (1, 1, 12000000000, 70, 0, 0, 0, '[1]', 0, '2026-01-01T12:00:00Z');
```

## Журналы

```
%LocalAppData%\BatPlayer\logs\app_YYYYMMDD.log
```

Каждая строка: `HH:mm:ss.fff [LEVEL] message`.
