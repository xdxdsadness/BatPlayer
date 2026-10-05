# BatPlayer

WPF-плеер (Windows, .NET 8) с локальной библиотекой и поддержкой стриминговых сервисов: SoundCloud, VK Музыка, Яндекс Музыка, Spotify.

## Возможности

- **SoundCloud** — поиск, лайки, стриминг; встроенная антиблокировка (zapret/WinDivert) против DPI-провайдеров
- **VK Музыка / Яндекс Музыка / Spotify** — авторизация, импорт и стриминг
- Локальная библиотека (SQLite) с обложками, плейлистами, историей и статистикой
- Эквалайзер, визуализация волны, горячие клавиши, трей, GIF-фоны интерфейса
- Single-instance, автообновление обложек, кэш стримов

## Сборка

Требуется .NET SDK 8+.

```bash
dotnet publish src/BatPlayer/BatPlayer.csproj -c Release -r win-x64 --self-contained true -o publish
```

Готовый exe: `publish/BatPlayer.exe`. Для отладки — открыть `BatPlayer.sln` в Visual Studio / Rider или `dotnet build`.

## Интеграция zapret (антиблокировка SoundCloud)

Плеер сам поднимает и закрывает winws.exe как побочный процесс:

- watchdog раз в минуту проверяет доступ к `soundcloud.com` и `api-v2.soundcloud.com`;
- если доступ заблокирован провайдером — запускается повышенный хелпер (`Tools/zapret/helper.ps1`, один UAC-запрос за сессию), который перебирает стратегии из `Tools/zapret/presets.json`;
- рабочая стратегия сохраняется в `%LocalAppData%\BatPlayer\zapret_last_preset.txt` и применяется первой в следующих сессиях;
- при закрытии плеера helper завершает winws и удаляется из памяти;
- если SoundCloud доступен напрямую (или включён VPN) — ничего не запускается.

winws.exe при этом запускается не напрямую, а как брендированная копия **«SoundCloud lock bypass.exe»** (иконка и описание Bat Player) — в диспетчере задач процесс отображается побочным инструментом плеера, а не отдельным exe. После обновления winws.exe пересоберите копию: `Tools/zapret/branding/brand_winws.ps1`.

Включается/выключается настройкой `SoundCloudZapretEnabled` в настройках плеера.

## Структура

```
BatPlayer.sln
src/BatPlayer/           приложение (WPF, MVVM)
  Audio/                 движок воспроизведения (NAudio)
  Controls/              переиспользуемые контролы (виртуализирующая сетка, волна, ленивые обложки)
  Database/              SQLite-репозитории
  Services/              интеграции платформ, кэши
  Services/SoundCloud/   API, стриминг, zapret-интеграция
  ViewModels/            MVVM-модели страниц
  Views/                 окна и страницы
  Tools/                 сторонние инструменты (в git не входят — см. ниже)
tests/BatPlayer.Tests/   unit-тесты (xUnit)
docs/                    архитектура, сборка, конфигурация, инсталлятор
installer/               Inno Setup скрипт
assets/                  иконки
```



## Сторонние инструменты (не входят в репозиторий)

Бинарники в `src/BatPlayer/Tools/` исключены из git — скачайте их отдельно перед запуском:

| Файл | Откуда | Зачем |
|---|---|---|
| `Tools/ffmpeg.exe` | [ffmpeg.org](https://ffmpeg.org/download.html) (full build) | конвертация аудио для кэша |
| `Tools/zapret/bin/` | [github.com/Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) | winws.exe, WinDivert — антиблокировка |

Конфиги zapret (`presets.json`, `helper.ps1`, `lists/`, `branding/`) — часть репозитория.

## Установка

Соберите publish-версию и скомпилируйте инсталлятор: `iscc installer/bat_player.iss` (Inno Setup 6.2+). Подробности — `docs/INSTALLER.md`.

## Лицензия

MIT — см. [LICENSE](LICENSE). В `Tools/zapret` используются сторонние бинарники [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) (winws.exe из [zapret](https://github.com/bol-van/zapret)) и [WinDivert](https://reqrypt.org/windivert.html) — на их действие распространяются лицензии соответствующих проектов.
