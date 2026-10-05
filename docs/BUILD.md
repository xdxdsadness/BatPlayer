# Сборка Bat Player

## Требования

| Компонент | Версия | Зачем |
|---|---|---|
| Windows | 10 1903+ / 11 | WPF требует Windows |
| .NET 8 SDK | 8.0.x | [скачать](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Inno Setup | 6.2+ | для установщика (опц.) |
| Python + Pillow | 3.9+ | для регенерации иконки (опц.) |

## 1. Сборка Debug (для разработки)

```bash
git clone <repo>
cd BatPlayer
dotnet build
```

Запуск:
```bash
dotnet run --project src/BatPlayer/BatPlayer.csproj
```

Или открыть `BatPlayer.sln` в Visual Studio 2022 и нажать F5.

## 2. Сборка Release (готовый .exe)

```bash
dotnet publish src/BatPlayer/BatPlayer.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish
```

Результат: `publish/BatPlayer.exe` — автономный `.exe`, не требует установленного .NET на целевой машине.

### Размер сборки

- self-contained, single-file: ~150 MB (включает .NET runtime + NAudio)
- после Inno Setup LZMA2: ~70 MB

## 3. Сборка установщика

```bash
# Сначала выполните шаг 2 — нужен каталог publish/

iscc installer\bat_player.iss
```

Результат: `dist/BatPlayer-Setup-1.0.0.exe`.

## 4. Регенерация иконки

```bash
pip install Pillow
python scripts/generate_icon.py
```

Иконка сохраняется в `src/BatPlayer/Resources/app.ico`. В csproj уже есть ссылка на неё.

## 5. Запуск тестов

```bash
dotnet test
```

Тесты покрывают: DatabaseContext, LibraryService, SearchService, MetadataService, SettingsService, EqualizerService.

## Частые проблемы

**Ошибка: "WPF requires Windows"** — текущая ОС не Windows. WPF нельзя собрать на Linux/macOS. Используй Windows-машину или виртуалку.

**Ошибка: "Could not find taglib-sharp"** — выполни `dotnet restore`.

**Большой размер .exe** — это норма для self-contained .NET-приложений. Чтобы уменьшить: `dotnet publish -p:PublishTrimmed=true` (но требует тщательной настройки — reflection в NAudio может ломаться).

**Приложение не запускается на старой Windows** — минимальная поддержка Windows 10 1903+. Указано в `app.manifest`.

## Сторонние инструменты

