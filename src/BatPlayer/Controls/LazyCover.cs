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
/// Lazy loading of list card covers (Library/SoundCloud/VK/Yandex Music/Downloads/
/// Artists/ArtistProfile/Playlist). Attached via the <see cref="Path"/> property to the
/// element showing the cover (a Border with an ImageBrush background or an Image); the
/// file is decoded NOT on the UI thread and not by all cards at once:
///
/// - a card is realized by the virtualizing panel (visible window ± buffer) → Loaded →
///   the request enters the global queue; the container leaves the window → Unloaded →
///   the request is canceled and the image freed — scrolling loads only visible covers;
/// - decoding is done by the workers of a global PRIORITY queue: the freshest request
///   goes first — what the user sees now (including after scrolling) beats the tails
///   of departed cards;
/// - at most <see cref="DecodeParallelism"/> images decode simultaneously;
/// - ready BitmapImages are cached by path (limit <see cref="MaxCacheEntries"/>, LRU) and
///   shared across all lists — scrolling back is instant;
/// - no file on disk → the registered <see cref="SetFileLoader"/> is called (network
///   covers are downloaded on demand, visible ones first); on failure — placeholder;
/// - element state for templates: <see cref="IsLoading"/> (loading spinner) and
///   <see cref="HasImage"/> (hide the placeholder icon once the image is applied).
///
/// DecodePixelWidth/CacheOption.OnLoad/Freeze — as in PathToImageConverter: the file is
/// not kept open and a frozen BitmapImage is thread-safe (created on a pool, applied on UI).
/// </summary>
public static class LazyCover
{
    /// <summary>How many covers decode at once. 8 parallel decodes caused allocation spikes
    /// (GC pauses = scroll micro-freezes); 6 was imperceptibly slower in throughput but
    /// smoother frame-wise. 4 keeps GC pressure off the UI thread during mass grid loads
    /// (400+ initial cards, scroll-driven loading): no visible difference in loading speed,
    /// smoother frames.</summary>
    private const int DecodeParallelism = 4;

    /// <summary>Decode width: grid cards are ≤ ~450px (as in PathToImageConverter).</summary>
    private const int DecodePixelWidth = 400;

    /// <summary>Cache limit for ready images; on overflow the oldest entries are evicted.
    /// A cache smaller than the cards of a couple of screens meant reverse scrolling kept
    /// re-decoding the same covers (extra allocations → GC pauses → jank). 150 entries
    /// cover 4-5 screens of the grid (≈90 MB peak for square 400px decodes): scrolled
    /// content stays in memory and re-scrolling is instant, with no decoding.</summary>
    private const int MaxCacheEntries = 150;

    /// <summary>How long an unused image lives in the cache: a memory unload — covers not
    /// shown for a long time are evicted and reverse scrolling simply re-decodes.
    /// 5 minutes dropped the cache between neighboring pages — raised to 20.</summary>
    private static readonly TimeSpan CacheEntryTtl = TimeSpan.FromMinutes(20);

    /// <summary>How often the sweeper walks the cache.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Ready images + last-use tick (Environment.TickCount64).</summary>
    private static readonly ConcurrentDictionary<string, (BitmapImage Bmp, long LastUse)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UI dispatcher to apply the result (strictly the UI thread).</summary>
    private static readonly Dispatcher Ui =
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    // ===== Decode priority queue =====
    // FIFO on a SemaphoreSlim served the earliest requests first — including cards that
    // had already left the window after fast scrolling. Workers take the freshest request:
    // currently visible covers are served first, the tails get the leftovers.
    private static readonly object Pulse = new();
    private static readonly List<string> Pending = new();   // [0] is next to decode
    private static readonly Dictionary<string, TaskCompletionSource<BitmapImage?>> Waiting = new();

    /// <summary>"Ensure the file exists at path" factory for covers not yet on disk
    /// (network caches: SoundCloud artworks etc.). true — the file appeared and can be
    /// decoded. A repeated call for a missing file is cheap (the loader deduplicates itself).</summary>
    private static Func<string, Task<bool>>? _fileLoader;
    public static void SetFileLoader(Func<string, Task<bool>>? loader) => _fileLoader = loader;

    static LazyCover()
    {
        // Cache sweeper: once a minute evict images nobody touched for over 5 minutes.
        // The app lives long — the cache must not grow unbounded.
        var timer = new System.Threading.Timer(
            _ => Sweep(), null, SweepInterval, SweepInterval);
        // The timer is deliberately not disposed: a static class lives as long as the process.
        GC.SuppressFinalize(timer);

        // Workers live as long as the process; there are at most DecodeParallelism of them.
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

    /// <summary>Mark cache entry use (the tick is taken outside — on the UI thread or in decoding).</summary>
    private static void Touch(string path)
    {
        if (Cache.TryGetValue(path, out var entry))
            Cache[path] = (entry.Bmp, Environment.TickCount64);
    }

    /// <summary>Cover file path; null/empty — placeholder (ImageSource is reset).</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(LazyCover),
        new FrameworkPropertyMetadata(string.Empty, OnPathChanged));

    public static string GetPath(DependencyObject obj) => (string)obj.GetValue(PathProperty);
    public static void SetPath(DependencyObject obj, string value) => obj.SetValue(PathProperty, value);

    /// <summary>true while the element waits for its cover (queue/decode/download) —
    /// the card template attaches a spinner to it.</summary>
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.RegisterAttached(
        "IsLoading", typeof(bool), typeof(LazyCover), new PropertyMetadata(false));

    public static bool GetIsLoading(DependencyObject obj) => (bool)obj.GetValue(IsLoadingProperty);
    public static void SetIsLoading(DependencyObject obj, bool value) => obj.SetValue(IsLoadingProperty, value);

    /// <summary>true once a cover is ALREADY applied to the element — the template hides
    /// the placeholder icon (a card may have gotten an image even with an empty path
    /// in its data).</summary>
    public static readonly DependencyProperty HasImageProperty = DependencyProperty.RegisterAttached(
        "HasImage", typeof(bool), typeof(LazyCover), new PropertyMetadata(false));

    public static bool GetHasImage(DependencyObject obj) => (bool)obj.GetValue(HasImageProperty);
    public static void SetHasImage(DependencyObject obj, bool value) => obj.SetValue(HasImageProperty, value);

    /// <summary>CTS of the load bound to the element (canceled on Unloaded/Path change).</summary>
    private static readonly DependencyProperty ActiveCtsProperty = DependencyProperty.RegisterAttached(
        "ActiveCts", typeof(CancellationTokenSource), typeof(LazyCover), new PropertyMetadata(null));

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        // One subscription per element regardless of Path changes (re-realization of the
        // container while scrolling changes DataContext → the binding updates Path).
        el.Loaded -= OnLoaded;
        el.Loaded += OnLoaded;
        el.Unloaded -= OnUnloaded;
        el.Unloaded += OnUnloaded;

        // IMPORTANT: mutate nothing (including the placeholder) before Loaded — the first
        // binding fire happens DURING template instantiation; replacing the Background then
        // breaks template loading (XamlParseException, read-only ImageBrush).
        if (el.IsLoaded) Start(el);
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el) Start(el);
    }

    private static void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // Only cancel the load: don't reset the image — the container is de-realized
        // anyway and the leftover bitmap is collected by the GC.
        if (sender is FrameworkElement el) CancelActive(el);
    }

    /// <summary>Request an element's cover: cache → cancel the previous load → priority queue.</summary>
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
            bmp = null; // corrupt file/disk — the card keeps its placeholder
        }

        // Background, not Render: when a page opens, hundreds of covers arrive in a row and
        // Render priority ranked ABOVE input — the UI "froze" while cards loaded. Background
        // is below Input: input is always processed first, covers arrive in idle time.
        await Ui.InvokeAsync(() =>
        {
            // Apply the spinner-off/image only if this is still the element's ACTIVE
            // request: a newer Start may have posted its own.
            if (!ReferenceEquals(el.GetValue(ActiveCtsProperty), cts)) return;
            el.SetValue(IsLoadingProperty, false);
            Apply(el, bmp);
        }, DispatcherPriority.Background);
    }

    // ===== Queue and workers =====

    /// <summary>Enqueue the path (deduplicated) and raise its priority: a fresh request is
    /// a currently visible card, prioritized over the tail.</summary>
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

    /// <summary>Decode the file (worker background thread): no file → the loader; otherwise
    /// a BitmapImage (OnLoad, 400px, Freeze) → cache. A missing file is NOT cached: it may
    /// appear later (batch sync), and the card must get it on the next request.</summary>
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
                // LRU: evict the oldest fifth. A full Clear() was worse in memory too
                // (every decode after hitting the limit dropped ALL covers, and reverse
                // scrolling re-decoded everything — CPU spikes and lag).
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

    /// <summary>Where the image is set: the Border background's ImageBrush (rounded corners)
    /// or an Image. A brush declared in the XAML template arrives frozen (read-only) — you
    /// can't assign it an ImageSource: replace the background with a new live brush with
    /// the same Stretch.</summary>
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
