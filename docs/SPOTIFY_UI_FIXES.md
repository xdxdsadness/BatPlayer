# Исправления визуала Spotify

## Что было исправлено:

### 1. Окно подключения (SpotifyLoginWindow)
- ✅ Создан XAML файл с правильным стилем (как у SoundCloud/VK)
- ✅ Добавлен title bar с перетаскиванием
- ✅ Использ уется PNG логотип без фона
- ✅ Добавлены правильные кнопки Connect/Cancel

### 2. Player Bar (нижняя панель)
- ✅ Убран лишний StackPanel вокруг логотипов
- ✅ Логотип Spotify теперь на одном уровне с SoundCloud и Yandex
- ✅ Все логотипы имеют одинаковый Margin="6,0,0,0"

### 3. Страница Spotify (SpotifyMusicView)
- ✅ Убран логотип из заголовка (теперь только текст)
- ✅ Стиль заголовка соответствует SoundCloud
- ✅ Логотипы на карточках используют PNG без фона

### 4. Ресурсы проекта
- ✅ Добавлены spotify.png и spotify.grey.png в BatPlayer.csproj

## Файлы изменены:
1. `src/BatPlayer/Views/SpotifyLoginWindow.xaml` - создан
2. `src/BatPlayer/Views/SpotifyLoginWindow.xaml.cs` - добавлен обработчик перетаскивания
3. `src/BatPlayer/Views/MainWindow.xaml` - исправлен PlayerBar
4. `src/BatPlayer/Views/SpotifyMusicView.xaml` - исправлен заголовок
5. `src/BatPlayer/BatPlayer.csproj` - добавлены ресурсы

## Результат:
Визуал Spotify теперь полностью соответствует стилю SoundCloud и других платформ в приложении.
