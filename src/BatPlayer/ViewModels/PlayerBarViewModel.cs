using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Helpers;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.Services.YandexMusic;

namespace BatPlayer.ViewModels;

/// <summary>
/// Нижний мини-плеер. Привязан к AudioService и CurrentTrack.
/// </summary>
public partial class PlayerBarViewModel : ObservableObject
{
    private readonly AudioService _audio;
    private readonly LibraryService _library;
    private readonly CoverCacheService _covers;
    private readonly YmService _ym;

    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    /// <summary>Id плейлиста, чья очередь играет (null — очередь не из плейлиста):
    /// карточки плейлистов по нему показывают Pause и держат оверлей.</summary>
    [ObservableProperty] private long? _currentPlaylistId;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    // double 0..100: слайдер TwoWay без округления при перетаскивании (дрожание бегунка исчезло)
    [ObservableProperty] private double _volume = 70;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isShuffle;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.None;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private BitmapImage? _coverImage;
    /// <summary>Идёт перемотка: тики позиции глушатся, пока движок не догонит цель.</summary>
    [ObservableProperty] private bool _isSeeking;

    /// <summary>Guard перемотки (чистая логика — см. SeekSyncGuard).</summary>
    private readonly SeekSyncGuard _seekGuard = new();
    /// <summary>Предохранитель: сбрасывает guard, если PositionChanged не придёт (пауза/ошибка).</summary>
    private DispatcherTimer? _seekFuseTimer;

    public double ProgressPercent => Duration.TotalSeconds > 0
        ? Position.TotalSeconds / Duration.TotalSeconds * 100 : 0;

    public PlayerBarViewModel(AudioService audio, LibraryService library, CoverCacheService covers,
                              YmService ym)
    {
        _audio = audio;
        _library = library;
        _covers = covers;
        _ym = ym;

        _audio.CurrentTrackChanged += (_, t) => OnTrackChanged(t);
        _audio.PlayStateChanged      += (_, _) => IsPlaying = _audio.IsPlaying;
        _audio.QueueSourceChanged    += (_, id) => CurrentPlaylistId = id;
        // Тик позиции с guard'ом перемотки: пока движок не догнал цель seek'а,
        // старые позиции проглатываются (иначе биндинг откатывает ползунок).
        _audio.PositionChanged       += OnAudioPositionChanged;
        _audio.VolumeChanged         += (_, v) => Volume = v;
        _audio.MuteChanged           += (_, m) => IsMuted = m;
        _audio.ShuffleChanged        += (_, s) => IsShuffle = s;
        _audio.RepeatModeChanged     += (_, r) => RepeatMode = r;
    }

    private void OnTrackChanged(Track? t)
    {
        EndSeekGuard(); // смена трека обнуляет позиции — guard перемотки больше не нужен
        CurrentTrack = t;
        Duration = t?.Duration ?? TimeSpan.Zero;
        Position = TimeSpan.Zero;
        IsFavorite = t?.IsFavorite ?? false;
        _ = LoadCoverAsync(t);
    }

    private void OnAudioPositionChanged(object? sender, TimeSpan p)
    {
        // Periodic resync: even if a PlayStateChanged notification is missed,
        // the icon can't stay out of sync with the actual engine state for long.
        IsPlaying = _audio.IsPlaying;

        if (_seekGuard.IsActive)
        {
            // Guard активен: принимаем тик только когда движок догнал цель seek'а
            // (или истёк предохранитель) — см. SeekSyncGuard.
            if (!_seekGuard.TryAccept(p, Environment.TickCount64)) return;
            _seekFuseTimer?.Stop();
            IsSeeking = false;

            // Первый тик после accept'а обязан быть у цели. Если движок отчитался
            // сильно не там (гонка чтения/сбой декодера) — ползунок не двигаем:
            // следующий тик (250 мс) покажет реальную позицию, и «отката» нет.
            if (Duration > TimeSpan.Zero &&
                Math.Abs((p - _seekGuard.Target).TotalSeconds) > 1.0)
                return;
        }

        // Фильтр мусорных тиков: позиция не может быть вне длительности трека.
        // РЫВКИ при этом НЕ фильтруем: движок перематывается не только через
        // SeekTo (resume с сохранённой позиции, RepeatOne, Previous) — после
        // таких seek'ов VM обязана принять новое значение, иначе таймлайн
        // замирает на старом месте и любой клик «откатывается» назад.
        if (Duration > TimeSpan.Zero &&
            (p < TimeSpan.Zero || p > Duration + TimeSpan.FromMilliseconds(500)))
            return;

        Position = p;
    }


    [RelayCommand] private void PlayPause() => _audio.PlayPauseToggle();
    [RelayCommand] private void Next()       => _audio.Next();
    [RelayCommand] private void Previous()   => _audio.Previous();
    [RelayCommand] private void ToggleShuffle() => _audio.IsShuffle = !_audio.IsShuffle;
    [RelayCommand] private void CycleRepeat()   => _audio.CycleRepeatMode();
    [RelayCommand] private void ToggleMute()    => _audio.IsMuted = !_audio.IsMuted;

    [RelayCommand]
    private void SeekTo(double percent)
    {
        if (Duration.TotalSeconds <= 0) return;
        var pos = TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(percent, 0, 1));
        BatPlayer.Services.Logger.Info($"[SEEK] target={pos.TotalSeconds:0.00}");
        _audio.Seek(pos);
        // Оптимистично показываем целевую позицию и глушим тики PositionChanged,
        // пока движок её не догонит: seek асинхронный, без этого биндинг сразу
        // откатывает ползунок таймлайна на старую позицию.
        Position = pos;
        BeginSeekGuard(pos);
    }

    private void BeginSeekGuard(TimeSpan target)
    {
        _seekGuard.Begin(target, Environment.TickCount64);
        IsSeeking = true;
        if (_seekFuseTimer == null)
        {
            // Предохранитель на случай, когда PositionChanged не придёт вовсе
            // (пауза без тиков, ошибка движка): guard не должен зависнуть навсегда.
            _seekFuseTimer = new DispatcherTimer(SeekSyncGuard.Fuse, DispatcherPriority.Background,
                (_, _) => EndSeekGuard(), Dispatcher.CurrentDispatcher);
        }
        _seekFuseTimer.Stop();
        _seekFuseTimer.Start(); // перевзводим на каждый новый seek
    }

    private void EndSeekGuard()
    {
        _seekFuseTimer?.Stop();
        _seekGuard.End();
        IsSeeking = false;
    }

    private async Task LoadCoverAsync(Track? t)
    {
        if (t == null)
        {
            CoverImage = null;
            return;
        }

        // Порядок резолва обложки:
        // 1) CoverHash → CoverCacheService (обложка из тегов локального файла);
        // 2) fallback: CoverCachePath (готовый файл в кэше) — путь SC-runtime-карточек
        //    (artwork_local_path): у них CoverHash пуст, но обложка уже скачана.
        var path = string.IsNullOrEmpty(t.CoverHash)
            ? null
            : await _covers.GetOrCreateCoverAsync(t.FilePath, t.CoverHash);
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(t.CoverCachePath)
            && File.Exists(t.CoverCachePath))
            path = t.CoverCachePath;

        // Промежуточный сброс CoverImage в null УБРАН: старая обложка остаётся
        // видимой до готовности новой — смена трека без «провала» в плейсхолдер,
        // кроссфейд-слои бара и Now Playing получают одну смену значения вместо
        // двух (null → картинка), которые выглядели как рывок.
        if (string.IsNullOrEmpty(path))
        {
            CoverImage = null;
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256; // мини-обложка плеера: полноразмерный декод не нужен
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            // Трек сменился, пока декодировали — чужую обложку не показываем.
            if (!ReferenceEquals(CurrentTrack, t)) return;
            CoverImage = bmp;
        }
        catch { /* swallow */ }
    }

    public async Task RestoreAsync(bool openPlayback = true)
    {
        await _audio.RestoreStateAsync(openPlayback);
        Volume = _audio.Volume;
        IsMuted = _audio.IsMuted;
        IsShuffle = _audio.IsShuffle;
        RepeatMode = _audio.RepeatMode;
    }

    partial void OnVolumeChanged(double value) => _audio.Volume = value;

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (CurrentTrack == null) return;
        var track = CurrentTrack;

        // Лайк YM-трека уходит в АККАУНТ Яндекс Музыки (POST/DELETE likes/tracks):
        // после синка трек появится на странице ЯМ и в «Фаворитах». Раньше сердечко
        // молча писало в локальную БД по отрицательному runtime-id — строки там не
        // было, лайк терялся («лайкаю в миксе — в фаворитах не появляется»).
        if (track.Source == Track.SourceYandex)
        {
            var target = !track.IsFavorite;
            if (!await _ym.SetTrackLikedAsync(track.ScId, target))
                return; // API не подтвердил — сердечко не переключаем
            track.IsFavorite = target;
            IsFavorite = target;
            return;
        }

        // Прочие платформенные runtime-карточки (VK/SC/Spotify): лайков в этой модели нет.
        if (track.Id <= 0) return;

        IsFavorite = !IsFavorite;
        await _library.SetFavoriteAsync(track.Id, IsFavorite);
        track.IsFavorite = IsFavorite;
    }
}
