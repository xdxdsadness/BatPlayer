# Расширение Bat Player

## Добавить новый раздел в боковую панель

1. Создай новый `PageViewModel` в `ViewModels/`:
```csharp
public partial class StatsViewModel : PageViewModel
{
    public StatsViewModel() { Title = "Статистика"; }
}
```

2. Создай `StatsView.xaml` UserControl в `Views/`.

3. Зарегистрируй DataTemplate в `App.xaml`:
```xml
<DataTemplate DataType="{x:Type vm:StatsViewModel}">
    <v:StatsView/>
</DataTemplate>
```

4. Добавь RadioButton в `MainWindow.xaml` в sidebar:
```xml
<RadioButton Style="{StaticResource NavButton}" GroupName="Nav"
             Content="Статистика"
             Command="{Binding NavigateCommand}"
             CommandParameter="Stats"/>
```

5. В `MainViewModel.Navigate` добавь case:
```csharp
"Stats" => Stats
```

## Добавить новый аудиоэффект

1. Создай класс, реализующий `ISampleProvider`, например `ReverbSampleProvider`:
```csharp
public sealed class ReverbSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    // ... reverb DSP implementation
    public WaveFormat WaveFormat => _source.WaveFormat;
    public int Read(float[] buffer, int offset, int count) { /* ... */ }
}
```

2. В `AudioEngine.Open` вставь в цепочку:
```csharp
_equalizer = new EqualizerSampleProvider(_sampleProvider);
var reverb = new ReverbSampleProvider(_equalizer); // <-- new effect
_volumeProvider = new VolumeSampleProvider(reverb);
_output.Init(_volumeProvider);
```

3. Добавь свойство включения в `AppSettings` и UI в `SettingsView`.

## Добавить поддержку нового аудиоформата

NAudio через `MediaFoundationReader` уже поддерживает большинство форматов. Если нужен специфичный кодек:

1. В `AudioEngine.CreateReader` добавь новый case:
```csharp
".myformat" => new MyCustomReader(filePath),
```

2. Добавь расширение в `Helpers.FileHelpers.AudioExtensions`.

3. Добавь формат в фильтр диалога в `MainViewModel.AddFilesAsync`.

## Добавить новый язык локализации

1. Создай `Resources/Strings.{lang}.resx` (например, `Strings.de.resx`).
2. Скопируй все ключи из `Strings.ru.resx` и переведи значения.
3. В `SettingsView` добавь `ComboBoxItem` для нового языка.

## Добавить глобальную горячую клавишу

1. В `Models/AppSettings` добавь свойство:
```csharp
public HotkeyBinding MyAction { get; set; } = new() { Key = 80, Modifiers = 6 };
```

2. В `MainWindow.OnGlobalKeyDown` добавь проверку:
```csharp
else if (MatchKey(s.MyAction, key, modifiers)) { MyCommand(); e.Handled = true; }
```

3. Для глобальной (OS-level) регистрации — добавь вызов в `GlobalHotkeyService.Initialize`:
```csharp
RegisterHotkey(3, vkCode, modifiers, () => MyAction());
```

## Добавить тест

1. В `tests/BatPlayer.Tests/` создай новый класс:
```csharp
public class MyServiceTests
{
    [Fact]
    public async Task MyScenario_WorksExpected()
    {
        // Arrange
        // Act
        // Assert
    }
}
```

2. Запусти: `dotnet test`.

## Создать новый визуальный стиль кнопки

1. Добавь стиль в `Themes/DarkTheme.xaml`:
```xml
<Style x:Key="MyButton" TargetType="Button" BasedOn="{StaticResource PrimaryButton}">
    <Setter Property="Background" Value="{StaticResource MyBrandBrush}"/>
</Style>
```

2. Используй:
```xml
<Button Style="{StaticResource MyButton}" Content="Click me"/>
```
