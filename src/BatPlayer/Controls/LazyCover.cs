using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BatPlayer.Controls;

/// <summary>
/// Ленивая загрузка обложек карточек списков (Library/SoundCloud/VK/Яндекс Музыка/Downloads/
/// Artists/ArtistProfile/Playlist). Вешается attached-свойством <see cref="Path"/> на элемент,
/// показывающий обложку (Border с ImageBrush в Background или Image), а сам файл декодируется
/// НЕ на UI-потоке и не всеми карточками разом:
///
/// - карточка реализуется виртуализирующей панелью (в видимое окно ± буфер) → Loaded →
///   запрос ставится в глобальную очередь; контейнер уехал из окна → Unloaded → запрос
///   отменяется, картинка освобождается — при скролле грузятся только видимые обложки;
/// - декод выполняют 8 воркеров глобальной ПРИОРИТЕТНОЙ очереди: первым берётся самый
///   свежий запрос — то, что пользователь видит сейчас (в т.ч. после скролла), уходит
///   вперёд хвостов ушедших карточек;
/// - одновременно декодируется не более <see cref="DecodeParallelism"/> картинок;
/// - готовые BitmapImage кэшируются по пути (лимит <see cref="MaxCacheEntries"/>, LRU) и
///   шарятся между всеми списками — обратная прокрутка мгновенна;
/// - файла нет на диске → зовётся зарегистрированный <see cref="SetFileLoader"/> (сетевые
///   обложки качаются по требованию, видимые — первыми), не скачалось — плейсхолдер;
/// - состояние элемента для шаблонов: <see cref="IsLoading"/> (спиннер загрузки) и
///   <see cref="HasImage"/> (скрыть иконку-плейсхолдер, когда картинка применена).
///
/// DecodePixelWidth/CacheOption.OnLoad/Freeze — как в PathToImageConverter: файл не держится
/// открытым, замороженный BitmapImage потокобезопасен (создаётся на пуле, применяется на UI).
/// </summary>
public static class LazyCover
{
    /// <summary>Сколько обложек декодируется одновременно. 8 параллельных декодов
    /// давали всплески аллокаций (GC-паузы = микро-фризы скролла), 6 — незаметно
    /// медленнее по throughput, но ровнее по кадрам. 4 — при массовой загрузке сеток
    /// (стартовые 400+ карточек, прогрузка прокруткой) GC давит UI-поток заметно;
    /// visual-разницы в скорости прогрузки нет, кадры ровнее.</summary>
    private const int DecodeParallelism = 4;

    /// <summary>Ширина декодирования: карточки сетки ≤ ~450px (как PathToImageConverter).</summary>
    private const int DecodePixelWidth = 400;

    /// <summary>Лимит кэша готовых картинок; при переполнении выкидываются самые старые
    /// записи. Кэш меньше числа карточек в паре экранов = обратный скролл постоянно
    /// перекодировал те же обложки (лишние аллокации → GC-паузы → рывки). 150 записей
    /// покрывает 4-5 экранов сетки (≈90 МБ пиково на квадратных 400px-декодах):
    /// прокрученное остаётся в памяти, повторный скролл мгновенен и без декода.</summary>
    private const int MaxCacheEntries = 150;

    /// <summary>Сколько живёт невостребованная картинка в кэше: разгрузка памяти — долго
    /// не показываемые обложки выгружаются, обратная прокрутка просто перекодирует.
    /// 5 минут сбрасывали кэш между соседними страницами — поднято до 20.</summary>
    private static readonly TimeSpan CacheEntryTtl = TimeSpan.FromMinutes(20);

    /// <summary>Как часто метла обходит кэш.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Готовые картинки + тик последнего использования (Environment.TickCount64).</summary>
    private static readonly ConcurrentDictionary<string, (BitmapImage Bmp, long LastUse)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UI-диспетчер для применения результата (строго UI-поток).</summary>
    private static readonly Dispatcher Ui =
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    // ===== Приоритетная очередь декода =====
    // FIFO на SemaphoreSlim декодировал сначала ранние запросы — в том числе карточек,
    // уже уехавших из окна при быстром скролле. Воркеры берут самый свежий запрос:
    // видимые сейчас обложки обслуживаются первыми, хвосты — по остаточному принципу.
    private static readonly object Pulse = new();
    private static readonly List<string> Pending = new();   // [0] — следующий на декод
    private static readonly Dictionary<string, TaskCompletionSource<BitmapImage?>> Waiting = new();

    /// <summary>Фабрика «гарантировать файл по пути» для обложек, которых ещё нет на диске
    /// (сетевые кэши: SoundCloud artworks и т.п.). true — файл появился, можно декодировать.
    /// Повторный вызов для отсутствующего файла дешёв (сам загрузчик дедуплицирует).</summary>
    private static Func<string, Task<bool>>? _fileLoader;
    public static void SetFileLoader(Func<string, Task<bool>>? loader) => _fileLoader = loader;

    static LazyCover()
    {
        // Метла кэша: раз в минуту выгружаем картинки, к которым >5 минут никто
        // не обращался. Приложение живёт долго — кэш не должен расти бесконечно.
        var timer = new System.Threading.Timer(
            _ => Sweep(), null, SweepInterval, SweepInterval);
        // Таймер намеренно не Dispose: статический класс живёт столько же, сколько процесс.
        GC.SuppressFinalize(timer);

        // Воркеры живут столько же, сколько процесс; их не больше DecodeParallelism.
        for (var i = 0; i < DecodeParallelism; i++)
            _ = Task.Run(WorkerLoopAsync);
    }

    private static void Sweep()
    {
        var now = Environment.TickCount64;
        foreach (var (path, entry) in Cache)
        {
            if (now - entry.LastUse <= CacheEntryTtl.TotalMilliseconds) continue;
            ((ICollection<KeyValuePair<string, (BitmapImage, long)>>)Cache).Remove(
                KeyValuePair.Create(path, entry));
        }
    }

    /// <summary>Отметка использования записи кэша (тик берём снаружи — на UI-потоке или в декоде).</summary>
    private static void Touch(string path)
    {
        if (Cache.TryGetValue(path, out var entry))
            Cache[path] = (entry.Bmp, Environment.TickCount64);
    }

    /// <summary>Путь файла обложки; null/пусто — плейсхолдер (ImageSource сбрасывается).</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(LazyCover),
        new FrameworkPropertyMetadata(string.Empty, OnPathChanged));

    public static string GetPath(DependencyObject obj) => (string)obj.GetValue(PathProperty);
    public static void SetPath(DependencyObject obj, string value) => obj.SetValue(PathProperty, value);

    /// <summary>true, пока элемент ждёт свою обложку (очередь/декод/скачивание) —
    /// на него вешается спиннер в шаблоне карточки.</summary>
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.RegisterAttached(
        "IsLoading", typeof(bool), typeof(LazyCover), new PropertyMetadata(false));

    public static bool GetIsLoading(DependencyObject obj) => (bool)obj.GetValue(IsLoadingProperty);
    public static void SetIsLoading(DependencyObject obj, bool value) => obj.SetValue(IsLoadingProperty, value);

    /// <summary>true, когда обложка УЖЕ применена к элементу — шаблон прячет иконку-плейсхолдер
    /// (карточка могла получить картинку и без заполненного пути в данных).</summary>
    public static readonly DependencyProperty HasImageProperty = DependencyProperty.RegisterAttached(
        "HasImage", typeof(bool), typeof(LazyCover), new PropertyMetadata(false));

    public static bool GetHasImage(DependencyObject obj) => (bool)obj.GetValue(HasImageProperty);
    public static void SetHasImage(DependencyObject obj, bool value) => obj.SetValue(HasImageProperty, value);

    /// <summary>CTS загрузки, привязанной к элементу (отмена при Unloaded/смене Path).</summary>
    private static readonly DependencyProperty ActiveCtsProperty = DependencyProperty.RegisterAttached(
        "ActiveCts", typeof(CancellationTokenSource), typeof(LazyCover), new PropertyMetadata(null));

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        // Одна подписка на элемент независимо от числа смен Path (ре-реализация контейнера
        // при скролле меняет DataContext → Binding обновляет Path).
        el.Loaded -= OnLoaded;
        el.Loaded += OnLoaded;
        el.Unloaded -= OnUnloaded;
        el.Unloaded += OnUnloaded;

        // ВАЖНО: до Loaded ничего не мутируем (в т.ч. плейсхолдер) — первое срабатывание
        // биндинга случается ВО ВРЕМЯ инстанциации шаблона, подмена Background в этот
        // момент валит загрузку шаблона (XamlParseException, read-only ImageBrush).
        if (el.IsLoaded) Start(el);
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el) Start(el);
    }

    private static void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // Только отменяем загрузку: картинку не сбрасываем — контейнер всё равно
        // де-реализуется, а оставшийся bitmap соберёт GC.
        if (sender is FrameworkElement el) CancelActive(el);
    }

    /// <summary>Запрос обложки элемента: кэш → отмена прежней загрузки → приоритетная очередь.</summary>
    private static void Start(FrameworkElement el)
    {
        CancelActive(el);

        var path = GetPath(el);
        if (string.IsNullOrEmpty(path))
        {
            el.SetValue(IsLoadingProperty, false);
            Apply(el, null);
            return;
        }

        if (Cache.TryGetValue(path, out var cached))
        {
            Touch(path);
            el.SetValue(IsLoadingProperty, false);
            Apply(el, cached.Bmp);
            return;
        }

        el.SetValue(IsLoadingProperty, true);
        var cts = new CancellationTokenSource();
        el.SetValue(ActiveCtsProperty, cts);
        var task = Enqueue(path);
        _ = ApplyWhenReadyAsync(task, el, cts);
    }

    private static async Task ApplyWhenReadyAsync(
        Task<BitmapImage?> task, FrameworkElement el, CancellationTokenSource cts)
    {
        BitmapImage? bmp = null;
        try
        {
            bmp = await task.ConfigureAwait(false);
        }
        catch
        {
            bmp = null; // битый файл/диск — карточка остаётся с плейсхолдером
        }

        // Background, а не Render: при открытии страницы приходят сотни обложек
        // подряд, и Render-приоритет стоял ВЫШЕ ввода — интерфейс «залипал»,
        // пока карточки прогружались. Background ниже Input: ввод всегда
        // обрабатывается первым, обложки подтягиваются в простое.
        await Ui.InvokeAsync(() =>
        {
            // Гасим спиннер/применяем картинку только если это по-прежнему АКТИВНЫЙ
            // запрос элемента: более свежий Start уже мог поставить свой.
            if (!ReferenceEquals(el.GetValue(ActiveCtsProperty), cts)) return;
            el.SetValue(IsLoadingProperty, false);
            Apply(el, bmp);
        }, DispatcherPriority.Background);
    }

    // ===== Очередь и воркеры =====

    /// <summary>Поставить путь в очередь (дедупликация) и поднять его приоритет:
    /// свежий запрос — это видимая сейчас карточка, ей приоритет над хвостом.</summary>
    private static Task<BitmapImage?> Enqueue(string path)
    {
        lock (Pulse)
        {
            if (Waiting.TryGetValue(path, out var existing))
            {
                Pending.Remove(path);
                Pending.Insert(0, path);
                Monitor.Pulse(Pulse);
                return existing.Task;
            }

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiting[path] = tcs;
            Pending.Insert(0, path);
            Monitor.Pulse(Pulse);
            return tcs.Task;
        }
    }

    private static async Task WorkerLoopAsync()
    {
        while (true)
        {
            string path;
            lock (Pulse)
            {
                while (Pending.Count == 0) Monitor.Wait(Pulse);
                path = Pending[0];
                Pending.RemoveAt(0);
            }

            var bmp = await DecodeAsync(path).ConfigureAwait(false);

            TaskCompletionSource<BitmapImage?>? tcs;
            lock (Pulse)
            {
                Waiting.Remove(path, out tcs);
            }
            tcs?.TrySetResult(bmp);
        }
    }

    /// <summary>Декод файла (фоновый поток воркера): файла нет → загрузчик; иначе BitmapImage
    /// (OnLoad, 400px, Freeze) → кэш. Отсутствующий файл НЕ кэшируем: он может появиться позже
    /// (батч-синк), и карточка должна получить его при следующем запросе.</summary>
    private static async Task<BitmapImage?> DecodeAsync(string path)
    {
        try
        {
            if (Cache.TryGetValue(path, out var hit))
            {
                Touch(path);
                return hit.Bmp;
            }

            if (!File.Exists(path))
            {
                var loader = _fileLoader;
                if (loader == null) return null;
                var ok = await loader(path).ConfigureAwait(false);
                if (!ok || !File.Exists(path)) return null;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = DecodePixelWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            if (Cache.Count >= MaxCacheEntries)
            {
                // LRU: выкидываем пятую часть самых старых. Полный Clear() был хуже
                // и по памяти (каждый новый декод после лимита сбрасывал ВСЕ обложки,
                // обратный скролл заново декодировал всё — всплески CPU и лаги).
                var victims = Cache.OrderBy(p => p.Value.LastUse)
                    .Take(MaxCacheEntries / 5)
                    .Select(p => p.Key)
                    .ToList();
                foreach (var key in victims)
                    Cache.TryRemove(key, out _);
            }
            Cache[path] = (bmp, Environment.TickCount64);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void CancelActive(FrameworkElement el)
    {
        if (el.GetValue(ActiveCtsProperty) is CancellationTokenSource cts)
        {
            el.SetValue(ActiveCtsProperty, null);
            cts.Cancel();
        }
    }

    /// <summary>Куда ставится картинка: ImageBrush фона Border (скруглённые углы) или Image.
    /// Кисть, объявленная в XAML-шаблоне, приходит замороженной (read-only) — присваивать ей
    /// ImageSource нельзя: подменяем фон новой живой кистью с тем же Stretch.</summary>
    private static void Apply(FrameworkElement el, BitmapImage? bmp)
    {
        el.SetValue(HasImageProperty, bmp != null);
        switch (el)
        {
            case Border border:
            {
                if (border.Background is not ImageBrush brush || brush.IsFrozen)
                {
                    brush = new ImageBrush { Stretch = Stretch.UniformToFill };
                    border.Background = brush;
                }
                brush.ImageSource = bmp;
                break;
            }
            case Image image:
                image.Source = bmp;
                break;
        }
    }
}
