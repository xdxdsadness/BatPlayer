using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class WaveView : UserControl
{
    private WaveViewModel? _vm;
    // Геометрия hero-зоны, снятая ДО смены HeroCurrent (в PropertyChanging — биндинги
    // ещё не подменяли слоты): обложки летят между слотами. Слот без карточки (нет
    // предыдущего/следующего) свёрнут — его геометрия зеркалится из противоположного
    // слота (колонки симметричны): в этот переход слот появится, и прилетающая в него
    // карточка должна знать куда.
    private readonly struct HeroSnapshot
    {
        public readonly Rect Center, Left, Right;

        public HeroSnapshot(Rect center, Rect left, Rect right)
        {
            Center = center; Left = left; Right = right;
        }
    }

    /// <summary>Слайд новой карточки и надписи уходящего слота (px, мс) — один и тот же
    /// жест в обе стороны перехода, чтобы надписи не выглядели неподвижными.</summary>
    private const double SlideShift = 48;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(320);

    private WaveCard? _heroChangingFrom;
    private HeroSnapshot? _snapshot;
    private bool _heroFlightActive;

    public WaveView()
    {
        InitializeComponent();

        //DataContext страницы — WaveViewModel (контент ContentControl'а). Он может
        //приехать до загрузки XAML — стартовые состояния применяем на Loaded.
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => PlayHeroFade();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnWavePropertyChanged;
            _vm.PropertyChanging -= OnWavePropertyChanging;
        }
        _vm = e.NewValue as WaveViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnWavePropertyChanged;
            _vm.PropertyChanging += OnWavePropertyChanging;
        }
    }

    private void OnWavePropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (e.PropertyName != nameof(WaveViewModel.HeroCurrent)) return;
        // Смена среди незавершённого полёта: сбрасываем его (элементы прыгают в слоты)
        // и начинаем новый перелёт с чистой геометрией
        if (_heroFlightActive) ClearHeroFlight();
        _heroChangingFrom = _vm?.HeroCurrent;
        _snapshot = CaptureHeroSnapshot();
    }

    private void OnWavePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WaveViewModel.HeroCurrent))
        {
            AnimateHeroChange(); // карусельный перелёт обложек либо мягкий фейд панели
        }
    }

    /// <summary>Мягкий фейд hero-панели — для смен, где перелёта нет: первое появление,
    /// перескок по списку, новый микс, пустой соседний слот.</summary>
    private void PlayHeroFade()
    {
        var anim = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        HeroPanel.BeginAnimation(OpacityProperty, anim);
    }

    // ===================== Карусельный перелёт обложек =====================

    /// <summary>Смена трека волны: старая карточка (обложка + подпись) уезжает из центра
    /// в освободившийся боковой слот (уменьшаясь и притухая до 0.38), новая влетает из
    /// соседнего слота в центр (увеличиваясь и проявляясь), в освободившемся слоте
    /// проявляется новая боковая карточка. Элементы летят «сами» — контент слотов к этому
    /// моменту уже подменён биндингами, поэтому не нужно ни клонировать визуал, ни
    /// скрывать слоты.</summary>
    private void AnimateHeroChange()
    {
        var from = _heroChangingFrom;
        _heroChangingFrom = null;
        var snap = _snapshot;
        _snapshot = null;

        var vm = _vm;
        var to = vm?.HeroCurrent;
        if (vm == null || to == null || from == null)
        {
            PlayHeroFade();
            return;
        }

        var cards = vm.Cards;
        var iFrom = cards.IndexOf(from);
        var iTo = cards.IndexOf(to);
        if (snap == null || iFrom < 0 || Math.Abs(iTo - iFrom) != 1)
        {
            PlayHeroFade(); // не соседние карточки — перелёт не строится, мягкий фейд
            return;
        }

        var s = snap.Value;
        var forward = iTo > iFrom;
        var slotFrom = forward ? s.Right : s.Left;      // откуда влетает новый центр
        var slotTo = forward ? s.Left : s.Right;        // куда уходит старый трек
        if (slotFrom.IsEmpty || slotTo.IsEmpty)
        {
            PlayHeroFade(); // соседний слот пуст/свёрнут — лететь некуда
            return;
        }

        // Слот, который в этом переходе только что появился (у края списка не было
        // прошлого/следующего), обязан быть измерен ДО старта перелёта: биндинги
        // выставили его видимость прямо перед этим вызовом, а layout отложен до
        // рендера. Без синхронизации улетающая карточка первые кадры невидима и
        // «втыкается» в слот уже в полёте — первый переход микса выглядел не так,
        // как все последующие.
        HeroPanel.UpdateLayout();

        _heroFlightActive = true;
        var duration = TimeSpan.FromMilliseconds(450);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var stop = FillBehavior.Stop; // по завершении вернуть базовые значения (совпадают)

        // 1) Старый трек: из центра в боковой слот, уменьшаясь; яркость 1.0 → 0.38.
        //    0.38 — базовая приглушённость слота, поэтому приземление без вспышки.
        var outCover = forward ? HeroPrevCover : HeroNextCover;
        FlyToIdentity(forward ? PrevCoverScale : NextCoverScale,
                      forward ? PrevCoverShift : NextCoverShift,
                      slotTo, s.Center, duration, ease, stop);
        outCover.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, 0.38, duration) { EasingFunction = ease, FillBehavior = stop });

        // 1a) Подпись уходящего трека едет вместе с обложкой: пока обложка в пути,
        //     текст невидим, затем всплывает на место синхронно с её приземлением
        //     (0.38 — базовая приглушённость слота). Тот же жест, что у подписи
        //     выбранного трека в центре. Раньше текст подменялся мгновенно: слот
        //     показывал подпись раньше, чем туда прилетала обложка, и тексты двух
        //     карточек выглядели рассинхронизированными.
        RiseToPlaceDelayed(forward ? HeroPrevText : HeroNextText,
                           forward ? PrevTextShift : NextTextShift,
                           0.0, 0.38, TextSyncDelay, duration, ease, stop);

        // 2) Новый трек: из бокового слота в центр, увеличиваясь; 0.38 → 1.0.
        //    ZIndex — на время полёта: центр отрывается от проявляющейся под ним карточки.
        Panel.SetZIndex(HeroCurrentPanel, 10);
        var centerFlight = FlyToIdentity(HeroCoverScale, HeroCoverShift,
                                         s.Center, slotFrom, duration, ease, stop);
        HeroCoverBox.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.38, 1.0, duration) { EasingFunction = ease, FillBehavior = stop });
        centerFlight.Completed += (_, _) =>
        {
            Panel.SetZIndex(HeroCurrentPanel, 0);
            _heroFlightActive = false;
        };

        // 2a) Подпись нового трека: проявляется и «всплывает», когда обложка
        //     подъезжает к центру — одновременно с ней
        RiseToPlaceDelayed(HeroCurrentText, CurTextShift,
                           0.38, 1.0, TextSyncDelay, duration, ease, stop);

        // 3) В освободившемся слоте проявляется новая карточка: обложка+подпись (body)
        //    фейдятся, а надпись слота видна с первого кадра и слайдом «встречает» её.
        //    Первые 100 мс body невидим — слот должен освободиться от улетающей подписи.
        var hasIncoming = forward ? iTo + 1 < cards.Count : iTo > 0;
        if (hasIncoming)
        {
            var inBody = forward ? HeroNextBody : HeroPrevBody;
            var inOpacity = new DoubleAnimationUsingKeyFrames { FillBehavior = stop };
            inOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            inOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
            inOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration), ease));
            inBody.BeginAnimation(OpacityProperty, inOpacity);
        }

        // 3b) Слайд новой карточки — едет с той стороны, куда нажали
        if (hasIncoming)
            (forward ? NextButtonShift : PrevButtonShift).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(forward ? SlideShift : -SlideShift, 0, SlideDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = stop
                });
    }

    /// <summary>Задержка подписи: обложка первые ~40% перелёта ещё в пути — текст
    /// ждёт на месте и «доплывает» вместе с ней, завершаясь в тот же момент.</summary>
    private static readonly TimeSpan TextSyncDelay = TimeSpan.FromMilliseconds(180);

    /// <summary>Вертикальный всплеск подписи при смене трека (px): подпись приподнимается
    /// и садится на место синхронно с обложкой. Вертикаль — чтобы движение текста
    /// не зависело от направления перехода.</summary>
    private const double TextRise = 14;

    /// <summary>Подпись трека: держится на месте до TextSyncDelay, затем всплывает
    /// (снизу вверх) с плавной сменой яркости. Без масштаба — шрифт всегда своего
    /// размера. Финиш ровно на total (конец полёта обложки).</summary>
    private static void RiseToPlaceDelayed(FrameworkElement el, TranslateTransform shift,
        double opacityFrom, double opacityTo,
        TimeSpan hold, TimeSpan total, IEasingFunction ease, FillBehavior stop)
    {
        var shiftAnim = new DoubleAnimationUsingKeyFrames { FillBehavior = stop };
        shiftAnim.KeyFrames.Add(new LinearDoubleKeyFrame(TextRise, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shiftAnim.KeyFrames.Add(new LinearDoubleKeyFrame(TextRise, KeyTime.FromTimeSpan(hold)));
        shiftAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(total), ease));
        shift.BeginAnimation(TranslateTransform.YProperty, shiftAnim);

        var opacityAnim = new DoubleAnimationUsingKeyFrames { FillBehavior = stop };
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(opacityFrom, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(opacityFrom, KeyTime.FromTimeSpan(hold)));
        opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(opacityTo, KeyTime.FromTimeSpan(total), ease));
        el.BeginAnimation(OpacityProperty, opacityAnim);
    }

    /// <summary>Перелёт элемента: стартует из прямоугольника fromRect (в координатах
    /// HeroPanel) и приезжает в своё обычное место. Возвращает анимацию горизонтального
    /// сдвига — к ней вешается Completed.</summary>
    private static DoubleAnimation FlyToIdentity(ScaleTransform scale, TranslateTransform shift,
        Rect ownRect, Rect fromRect, TimeSpan duration, IEasingFunction ease, FillBehavior stop)
    {
        // Масштаб и сдвиг считаются вокруг левого верхнего угла (RenderTransformOrigin 0,0),
        // поэтому прямоугольники интерполируются друг в друга линейно и без перекрытий
        var k = fromRect.Width / ownRect.Width;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(k, 1, duration) { EasingFunction = ease, FillBehavior = stop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(k, 1, duration) { EasingFunction = ease, FillBehavior = stop });

        var tx = new DoubleAnimation(fromRect.X - ownRect.X, 0, duration) { EasingFunction = ease, FillBehavior = stop };
        shift.BeginAnimation(TranslateTransform.XProperty, tx);
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromRect.Y - ownRect.Y, 0, duration) { EasingFunction = ease, FillBehavior = stop });
        return tx;
    }

    /// <summary>Сброс активного перелёта: все анимации слотов к базовым значениям.</summary>
    private void ClearHeroFlight()
    {
        _heroFlightActive = false;
        Panel.SetZIndex(HeroCurrentPanel, 0);
        HeroCoverBox.BeginAnimation(OpacityProperty, null);
        HeroCurrentText.BeginAnimation(OpacityProperty, null);
        HeroPrevButton.BeginAnimation(OpacityProperty, null);
        HeroNextButton.BeginAnimation(OpacityProperty, null);
        HeroPrevBody.BeginAnimation(OpacityProperty, null);
        HeroNextBody.BeginAnimation(OpacityProperty, null);
        HeroPrevText.BeginAnimation(OpacityProperty, null);
        HeroNextText.BeginAnimation(OpacityProperty, null);
        ClearCoverFlight(HeroCoverScale, HeroCoverShift);
        ClearCoverFlight(PrevCoverScale, PrevCoverShift);
        ClearCoverFlight(NextCoverScale, NextCoverShift);
        PrevTextShift.BeginAnimation(TranslateTransform.YProperty, null);
        NextTextShift.BeginAnimation(TranslateTransform.YProperty, null);
        CurTextShift.BeginAnimation(TranslateTransform.YProperty, null);
        PrevButtonShift.BeginAnimation(TranslateTransform.XProperty, null);
        NextButtonShift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private static void ClearCoverFlight(ScaleTransform scale, TranslateTransform shift)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>Прямоугольники обложек в координатах HeroPanel. Снимается ДО подмены
    /// контента: размеры слотов от контента не зависят, так что геометрия верна и после
    /// смены. Свёрнутый слот (нет предыдущего/следующего) зеркалится из противоположного —
    /// в этот переход он появится, и прилетающая в него карточка должна знать куда.</summary>
    private HeroSnapshot? CaptureHeroSnapshot()
    {
        if (HeroPanel is not { IsLoaded: true, IsVisible: true }) return null;

        var center = RectOf(HeroCoverBox);
        if (center.IsEmpty) return null;

        var left = HeroPrevButton.IsVisible ? RectOf(HeroPrevCover) : Rect.Empty;
        var right = HeroNextButton.IsVisible ? RectOf(HeroNextCover) : Rect.Empty;

        var panelWidth = HeroPanel.ActualWidth;
        if (left.IsEmpty && !right.IsEmpty) left = MirrorX(right, panelWidth);
        else if (right.IsEmpty && !left.IsEmpty) right = MirrorX(left, panelWidth);

        return new HeroSnapshot(center, left, right);
    }

    /// <summary>Зеркальный прямоугольник относительно центра панели.</summary>
    private Rect MirrorX(Rect rect, double panelWidth)
        => new(panelWidth - rect.X - rect.Width, rect.Y, rect.Width, rect.Height);

    private Rect RectOf(FrameworkElement el)
        => el.ActualWidth > 0 && el.ActualHeight > 0
            ? el.TransformToVisual(HeroPanel).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight))
            : Rect.Empty;



}
