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
    // Hero-zone geometry captured BEFORE HeroCurrent changes (in PropertyChanging —
    // bindings haven't swapped the slots yet): covers fly between slots. A collapsed
    // slot (no prev/next) is mirrored from the opposite slot (columns are symmetric):
    // the slot appears in this transition and the incoming card must know where to land.
    private readonly struct HeroSnapshot
    {
        public readonly Rect Center, Left, Right;

        public HeroSnapshot(Rect center, Rect left, Rect right)
        {
            Center = center; Left = left; Right = right;
        }
    }

    /// <summary>Slide of the new card and of the outgoing slot's caption (px, ms) — the same
    /// gesture in both transition directions so captions never look motionless.</summary>
    private const double SlideShift = 48;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(320);

    private WaveCard? _heroChangingFrom;
    private HeroSnapshot? _snapshot;
    private bool _heroFlightActive;

    public WaveView()
    {
        InitializeComponent();

        // The page's DataContext is the WaveViewModel (ContentControl content). It can
        // arrive before the XAML loads — starting states are applied on Loaded.
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
        // A change during an unfinished flight: reset it (elements snap into slots)
        // and start a new flight with clean geometry.
        if (_heroFlightActive) ClearHeroFlight();
        _heroChangingFrom = _vm?.HeroCurrent;
        _snapshot = CaptureHeroSnapshot();
    }

    private void OnWavePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WaveViewModel.HeroCurrent))
        {
            AnimateHeroChange(); // carousel cover flight or a soft panel fade
        }
    }

    /// <summary>Soft hero-panel fade — for transitions with no flight: first appearance,
    /// list jumps, a new mix, an empty neighbor slot.</summary>
    private void PlayHeroFade()
    {
        var anim = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        HeroPanel.BeginAnimation(OpacityProperty, anim);
    }

    // ===================== Carousel cover flight =====================

    /// <summary>Wave track change: the old card (cover + caption) leaves the center for the
    /// freed side slot (shrinking and dimming to 0.38), the new one flies in from the
    /// neighbor slot to the center (growing and brightening), and the new side card fades
    /// in at the freed slot. Elements fly "by themselves" — slot content has already been
    /// swapped by bindings, so nothing needs cloning or hiding.</summary>
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
            PlayHeroFade(); // not adjacent cards — no flight, soft fade
            return;
        }

        var s = snap.Value;
        var forward = iTo > iFrom;
        var slotFrom = forward ? s.Right : s.Left;      // where the new center flies in from
        var slotTo = forward ? s.Left : s.Right;        // where the old track goes
        if (slotFrom.IsEmpty || slotTo.IsEmpty)
        {
            PlayHeroFade(); // neighbor slot is empty/collapsed — nowhere to fly
            return;
        }

        // A slot that just appeared in this transition (at the list edge there was no
        // prev/next) must be measured BEFORE the flight starts: bindings set its
        // visibility right before this call, and layout is deferred to render. Without
        // this sync the outgoing card is invisible for the first frames and "sticks"
        // into the slot mid-flight — the mix's first transition looked off.
        HeroPanel.UpdateLayout();

        _heroFlightActive = true;
        var duration = TimeSpan.FromMilliseconds(450);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var stop = FillBehavior.Stop; // on completion restore base values (they match)

        // 1) Old track: center -> side slot, shrinking; brightness 1.0 -> 0.38.
        //    0.38 is the slot's base dimness, so it lands without a flash.
        var outCover = forward ? HeroPrevCover : HeroNextCover;
        FlyToIdentity(forward ? PrevCoverScale : NextCoverScale,
                      forward ? PrevCoverShift : NextCoverShift,
                      slotTo, s.Center, duration, ease, stop);
        outCover.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, 0.38, duration) { EasingFunction = ease, FillBehavior = stop });

        // 1a) The outgoing track's caption rides with the cover: while the cover is in
        //     flight the text is invisible, then rises into place in sync with its landing
        //     (0.38 is the slot's base dimness) — the same gesture as the center caption.
        //     The text used to swap instantly: the slot showed a caption before the cover
        //     arrived, and the two cards' texts looked out of sync.
        RiseToPlaceDelayed(forward ? HeroPrevText : HeroNextText,
                           forward ? PrevTextShift : NextTextShift,
                           0.0, 0.38, TextSyncDelay, duration, ease, stop);

        // 2) New track: side slot -> center, growing; 0.38 -> 1.0.
        //    ZIndex for the flight: the center lifts off the card fading in beneath it.
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

        // 2a) New track's caption: fades in and rises as the cover reaches
        //     the center — together with it.
        RiseToPlaceDelayed(HeroCurrentText, CurTextShift,
                           0.38, 1.0, TextSyncDelay, duration, ease, stop);

        // 3) A new card fades in at the freed slot: cover+caption (body) fade while the
        //    slot caption is visible from the first frame and slides to "meet" it.
        //    The first 100ms the body is invisible — the slot must clear of the outgoing caption.
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

        // 3b) New card slide — comes from the side that was clicked
        if (hasIncoming)
            (forward ? NextButtonShift : PrevButtonShift).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(forward ? SlideShift : -SlideShift, 0, SlideDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = stop
                });
    }

    /// <summary>Caption delay: the cover is still in flight for the first ~40% — the text
    /// waits in place and "floats" in with it, finishing at the same moment.</summary>
    private static readonly TimeSpan TextSyncDelay = TimeSpan.FromMilliseconds(180);

    /// <summary>Vertical caption rise on track change (px): the caption lifts and settles
    /// in sync with the cover. Vertical — so the text motion doesn't depend on the
    /// transition direction.</summary>
    private const double TextRise = 14;

    /// <summary>Track caption: holds in place until TextSyncDelay, then rises (bottom-up)
    /// with a smooth brightness change. No scaling — the font keeps its own size.
    /// Finishes exactly at total (the end of the cover flight).</summary>
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

    /// <summary>Element flight: starts from the fromRect rectangle (in HeroPanel coordinates)
    /// and arrives at its normal place. Returns the horizontal shift animation —
    /// Completed is attached to it.</summary>
    private static DoubleAnimation FlyToIdentity(ScaleTransform scale, TranslateTransform shift,
        Rect ownRect, Rect fromRect, TimeSpan duration, IEasingFunction ease, FillBehavior stop)
    {
        // Scale and shift are computed around the top-left corner (RenderTransformOrigin 0,0),
        // so the rectangles interpolate into each other linearly and without overlaps
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

    /// <summary>Reset the active flight: all slot animations back to their base values.</summary>
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

    /// <summary>Cover rectangles in HeroPanel coordinates. Captured BEFORE the content swap:
    /// slot sizes don't depend on content, so the geometry stays valid after the change.
    /// A collapsed slot (no prev/next) is mirrored from the opposite one — it appears in
    /// this transition and the incoming card must know where.</summary>
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

    /// <summary>Rectangle mirrored about the panel's center.</summary>
    private Rect MirrorX(Rect rect, double panelWidth)
        => new(panelWidth - rect.X - rect.Width, rect.Y, rect.Width, rect.Height);

    private Rect RectOf(FrameworkElement el)
        => el.ActualWidth > 0 && el.ActualHeight > 0
            ? el.TransformToVisual(HeroPanel).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight))
            : Rect.Empty;



}
