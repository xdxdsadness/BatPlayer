# Создание установщика Bat Player

## Требования

- Inno Setup 6.2+ — [скачать](https://jrsoftware.org/isinfo.php)
- Собранный `publish/` каталог (см. [BUILD.md](BUILD.md))

## Шаг 1. Публикация приложения

```bash
dotnet publish src/BatPlayer/BatPlayer.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish
```

Результат: `publish\BatPlayer.exe` (~150 MB, single-file).

## Шаг 2. Компиляция установщика

Открой `installer\bat_player.iss` в Inno Setup Studio (или запусти из CLI):

```bash
iscc installer\bat_player.iss
```

Результат: `dist\BatPlayer-Setup-1.0.0.exe` (~70 MB после LZMA2 сжатия).

## Шаг 3. Установка на целевой машине

Дважды кликни `BatPlayer-Setup-1.0.0.exe`:

1. **Приветствие** — выбор языка установщика (RU/EN).
2. **Лицензия** — MIT.
3. **Путь установки** — по умолчанию `%LocalAppData%\Programs\Bat Player` (без прав админа).
4. **Дополнительно**:
   - Создать ярлык на рабочем столе (опц.)
   - Запускать вместе с Windows (опц.)
5. **Установка** — копирование файлов, создание ярлыков в меню «Пуск».
6. **Завершение** — опциональный запуск приложения.

## Что создаёт установщик

- `%LocalAppData%\Programs\Bat Player\BatPlayer.exe`
- `%LocalAppData%\Programs\Bat Player\app.ico`
- Папку в меню «Пуск»: `Bat Player`
- Ярлык на рабочем столе (если выбран)
- Запись в `Programs and Features` для удаления

## Удаление

Через «Программы и компоненты» (appwiz.cpl). Пользовательские данные в `%LocalAppData%\BatPlayer\` (база, настройки, кэш обложек) **сохраняются** — смотри секцию `[UninstallDelete]` в `bat_player.iss`. Чтобы удалять полностью — раскомментируй строку:
```
Type: filesandordirs; Name: "{localappdata}\BatPlayer"
```

## Кастомизация установщика

В `installer/bat_player.iss` поменяй:
- `#define MyAppVersion` — версия приложения
- `#define MyAppPublisher` — издатель
- `Compression=lzma2/ultra64` — уровень сжатия
- `WizardStyle=modern` — современный или классический вид мастера

## Тихая установка (для корпоративного развёртывания)

```bash
BatPlayer-Setup-1.0.0.exe /VERYSILENT /NORESTART /CURRENTUSER
```

Это установит приложение без UI в профиль текущего пользователя.
