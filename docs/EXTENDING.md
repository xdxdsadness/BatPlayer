# Extending

## Add a sidebar page

1. Create a `PageViewModel` in `ViewModels/`:

```csharp
public partial class StatsViewModel : PageViewModel
{
    public StatsViewModel() { Title = "Stats"; }
}
```

2. Create `StatsView.xaml` (UserControl) in `Views/`.

3. Register a DataTemplate in `App.xaml`:

```xml
<DataTemplate DataType="{x:Type vm:StatsViewModel}">
    <v:StatsView/>
</DataTemplate>
```

4. Add a RadioButton to the sidebar in `MainWindow.xaml`:

```xml
<RadioButton Style="{StaticResource NavButton}" GroupName="Nav"
             Content="Stats"
             Command="{Binding NavigateCommand}"
             CommandParameter="Stats"/>
```

5. Add a `"Stats" => Stats` case to `MainViewModel.Navigate`.

## Add an audio effect

1. Implement `ISampleProvider`, e.g. `ReverbSampleProvider`.
2. Insert it into the chain in `AudioEngine.Open` between the equalizer and the volume provider.
3. Add the toggle to `AppSettings` and the Settings UI.

## Add an audio format

NAudio already handles most formats via `MediaFoundationReader`. For a custom codec:

1. Add a case in `AudioEngine.CreateReader`.
2. Add the extension to `Helpers.FileHelpers.AudioExtensions`.
3. Add it to the file dialog filter in `MainViewModel.AddFilesAsync`.

## Add a localization language

1. Create `Resources/Strings.{lang}.resx` (e.g. `Strings.de.resx`).
2. Copy the keys from `Strings.resx` and translate the values.
3. Add a ComboBoxItem for the language in the Settings UI.

## Add a global hotkey

1. Add a `HotkeyBinding` property to `Models/AppSettings`.
2. Handle it in `MainWindow.OnGlobalKeyDown`.
3. For OS-level registration add a call in `GlobalHotkeyService.Initialize`.

## Add a test

Create a class under `tests/BatPlayer.Tests/` and run `dotnet test`.

## Add a control style

Add a keyed style in `Themes/DarkTheme.xaml` and reference it with `{StaticResource ...}`.
