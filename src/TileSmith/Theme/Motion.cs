using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace TileSmith.Theme;

/// <summary>
/// One place for motion, like DailyDash's MacroMotion: quick-out eases for entrances, critically damped springs for
/// anything the user moves. Animations run on the compositor through UIElement.StartAnimation (Translation / Scale /
/// Opacity), so they never touch layout and coexist with XAML property transitions.
/// </summary>
public static class Motion
{
    /// <summary>Cinema-Info's --ease: cubic-bezier(0.22, 1, 0.36, 1) — quick out, soft landing.</summary>
    public static readonly Vector2 EaseP1 = new(0.22f, 1f);
    public static readonly Vector2 EaseP2 = new(0.36f, 1f);

    public static TimeSpan Fast => Scaled(140);
    public static TimeSpan Normal => Scaled(260);
    public static TimeSpan Slow => Scaled(380);

    /// <summary>Settings › Look and feel › Reduce motion: shorter, smaller movement, no cascades.</summary>
    public static bool Reduced { get; set; }

    private static TimeSpan Scaled(int ms) => TimeSpan.FromMilliseconds(Reduced ? ms * 0.6 : ms);

    private static float Distance(float d) => Reduced ? d * 0.35f : d;

    private static readonly ConditionalWeakTable<UIElement, List<CompositionAnimation>> Loops = new();

    public static Compositor Compositor => CompositionTarget.GetCompositorForCurrentThread();

    public static CubicBezierEasingFunction Ease(Compositor c) => c.CreateCubicBezierEasingFunction(EaseP1, EaseP2);

    /// <summary>Scale changes animate automatically (XAML property transition).</summary>
    public static void EnableTransitions(UIElement element, TimeSpan? duration = null)
    {
        // Scale only: Opacity and Translation are animated explicitly (StartAnimation), and a property can't have both.
        element.ScaleTransition = new Vector3Transition { Duration = duration ?? Fast };
    }

    /// <summary>Keeps the scale centre in the middle of the element.</summary>
    public static void CenterScale(FrameworkElement element)
    {
        void Update() => element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        element.SizeChanged += (_, _) => Update();
        Update();
    }

    private static Vector3KeyFrameAnimation Vector(string target, Vector3? from, Vector3 to, TimeSpan duration, int delayMs = 0)
    {
        var c = Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        a.Target = target;
        if (from is { } f) a.InsertKeyFrame(0f, f);
        a.InsertKeyFrame(1f, to, Ease(c));
        a.Duration = duration;
        if (delayMs > 0)
        {
            a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        return a;
    }

    private static ScalarKeyFrameAnimation Scalar(string target, float? from, float to, TimeSpan duration, int delayMs = 0)
    {
        var c = Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.Target = target;
        if (from is { } f) a.InsertKeyFrame(0f, f);
        a.InsertKeyFrame(1f, to, Ease(c));
        a.Duration = duration;
        if (delayMs > 0)
        {
            a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        return a;
    }

    /// <summary>A spring on Translation or Scale. Damping 1 = no overshoot; lower values bounce a little.</summary>
    private static CompositionAnimation Spring(string target, Vector3? from, Vector3 to, float damping, float periodMs, int delayMs = 0)
    {
        var c = Compositor;
        if (Reduced)
        {
            return Vector(target, from, to, Fast, delayMs);
        }
        var a = c.CreateSpringVector3Animation();
        a.Target = target;
        a.FinalValue = to;
        if (from is { } f) a.InitialValue = f;
        a.DampingRatio = damping;
        a.Period = TimeSpan.FromMilliseconds(periodMs);
        if (delayMs > 0)
        {
            a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        return a;
    }

    /// <summary>Fade + rise into place (entrances). Delay staggers groups.</summary>
    public static void Enter(UIElement element, float fromY = 10, int delayMs = 0, TimeSpan? duration = null)
    {
        var d = duration ?? Normal;
        if (Reduced) delayMs = Math.Min(delayMs, 40);
        element.StartAnimation(Scalar("Opacity", 0f, 1f, d, delayMs));
        element.StartAnimation(Vector("Translation", new Vector3(0, Distance(fromY), 0), Vector3.Zero, d, delayMs));
    }

    /// <summary>Fade + slide in from the side (page changes).</summary>
    public static void EnterFromSide(UIElement element, float fromX, int delayMs = 0)
    {
        element.StartAnimation(Scalar("Opacity", 0f, 1f, Normal, delayMs));
        element.StartAnimation(Spring("Translation", new Vector3(Distance(fromX), 0, 0), Vector3.Zero, 0.85f, 70, delayMs));
    }

    /// <summary>Pops in from a little smaller (badges, chips, new tiles).</summary>
    public static void PopIn(FrameworkElement element, int delayMs = 0)
    {
        element.CenterPoint = new Vector3((float)element.Width / 2, (float)element.Height / 2, 0);
        element.StartAnimation(Scalar("Opacity", 0f, 1f, Fast, delayMs));
        element.StartAnimation(Spring("Scale", new Vector3(0.6f, 0.6f, 1), Vector3.One, 0.55f, 55, delayMs));
    }

    /// <summary>A quick "boing" to acknowledge a change (a toggle switched on).</summary>
    public static void Pulse(UIElement element, float amount = 1.18f)
    {
        if (Reduced) return;
        element.StartAnimation(Spring("Scale", new Vector3(amount, amount, 1), Vector3.One, 0.4f, 45));
    }

    /// <summary>FLIP: the element jumped by <paramref name="delta"/> in layout; spring back from the old spot.</summary>
    public static void Glide(UIElement element, Vector3 delta, TimeSpan? duration = null) =>
        element.StartAnimation(Spring("Translation", delta, Vector3.Zero, 0.82f, 62));

    /// <summary>Springs Translation from wherever it is now to <paramref name="to"/>.</summary>
    public static void TranslateTo(UIElement element, Vector3 to, float damping = 0.75f) =>
        element.StartAnimation(Spring("Translation", element.Translation, to, damping, 60));

    /// <summary>Springs Scale to <paramref name="to"/> (lifted tiles).</summary>
    public static void ScaleTo(UIElement element, float to, float damping = 0.6f) =>
        element.StartAnimation(Spring("Scale", null, new Vector3(to, to, 1), damping, 55));

    /// <summary>Fades an element out, then calls <paramref name="completed"/>.</summary>
    public static void FadeOut(UIElement element, Action? completed = null, float toY = 0)
    {
        var d = Fast;
        element.StartAnimation(Scalar("Opacity", null, 0f, d));
        if (toY != 0) element.StartAnimation(Vector("Translation", null, new Vector3(0, toY, 0), d));
        if (completed is null) return;
        var timer = new DispatcherTimer { Interval = d + TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completed();
        };
        timer.Start();
    }

    /// <summary>Resets what <see cref="FadeOut"/> did (elements are reused).</summary>
    public static void Reset(UIElement element)
    {
        element.StartAnimation(Scalar("Opacity", null, 1f, TimeSpan.FromMilliseconds(1)));
        element.StartAnimation(Vector("Translation", null, Vector3.Zero, TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>Material 3 "emphasized decelerate": things arriving on screen.</summary>
    private static CubicBezierEasingFunction EnterEase(Compositor c) => c.CreateCubicBezierEasingFunction(new(0.05f, 0.7f), new(0.1f, 1f));

    /// <summary>Material 3 "emphasized accelerate": things leaving the screen.</summary>
    private static CubicBezierEasingFunction ExitEase(Compositor c) => c.CreateCubicBezierEasingFunction(new(0.3f, 0f), new(0.8f, 0.15f));

    private static KeyFrameAnimation Keyframes(string target, object? from, object to, TimeSpan duration, CubicBezierEasingFunction ease)
    {
        var c = Compositor;
        switch (to)
        {
            case float t:
                var s = c.CreateScalarKeyFrameAnimation();
                s.Target = target;
                if (from is float f) s.InsertKeyFrame(0f, f);
                s.InsertKeyFrame(1f, t, ease);
                s.Duration = duration;
                return s;
            default:
                var v = c.CreateVector3KeyFrameAnimation();
                v.Target = target;
                if (from is Vector3 fv) v.InsertKeyFrame(0f, fv);
                v.InsertKeyFrame(1f, (Vector3)to, ease);
                v.Duration = duration;
                return v;
        }
    }

    /// <summary>
    /// The panel opening: it grows out of the corner it opens from (the tray) and rises a little, on one curve with no
    /// overshoot. <paramref name="element"/> must hold the surface AND its shadow so both move as one. Starts from the
    /// hidden state unless <paramref name="fromCurrent"/> (re-opened while it was still closing).
    /// </summary>
    public static void PanelIn(UIElement element, Vector2 size, bool fromBottom, bool fromRight, bool fade = false, bool fromCurrent = false)
    {
        var c = Compositor;
        var ease = EnterEase(c);
        var duration = TimeSpan.FromMilliseconds(Reduced ? 160 : 320);
        var fadeDuration = TimeSpan.FromMilliseconds(Reduced ? 100 : 150);
        element.CenterPoint = new Vector3(fromRight ? size.X : 0, fromBottom ? size.Y : 0, 0);
        element.StartAnimation(Keyframes("Opacity", fromCurrent ? null : 0f, 1f, fadeDuration, ease));
        if (fade)
        {
            // glass: the blur belongs to the window and can't move, so the content only fades in on it
            element.StartAnimation(Keyframes("Scale", null, Vector3.One, TimeSpan.FromMilliseconds(1), ease));
            element.StartAnimation(Keyframes("Translation", null, Vector3.Zero, TimeSpan.FromMilliseconds(1), ease));
            return;
        }
        var startScale = Reduced ? 0.985f : 0.95f;
        element.StartAnimation(Keyframes("Scale", fromCurrent ? null : new Vector3(startScale, startScale, 1), Vector3.One, duration, ease));
        element.StartAnimation(Keyframes("Translation", fromCurrent ? null : new Vector3(0, Distance(fromBottom ? 14 : -14), 0), Vector3.Zero, duration, ease));
    }

    /// <summary>
    /// Quick fade/shrink back towards the corner, then <paramref name="completed"/> (which hides the window). The element
    /// stays invisible afterwards: <see cref="PanelIn"/> brings it back, so a re-shown window never flashes the old frame.
    /// </summary>
    public static void PanelOut(UIElement element, bool fromBottom, Action completed)
    {
        var c = Compositor;
        var ease = ExitEase(c);
        var duration = TimeSpan.FromMilliseconds(Reduced ? 90 : 150);
        element.StartAnimation(Keyframes("Opacity", null, 0f, duration, ease));
        element.StartAnimation(Keyframes("Scale", null, new Vector3(0.97f, 0.97f, 1), duration, ease));
        element.StartAnimation(Keyframes("Translation", null, new Vector3(0, Distance(fromBottom ? 8 : -8), 0), duration, ease));
        var timer = new DispatcherTimer { Interval = duration + TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completed();
        };
        timer.Start();
    }

    /// <summary>Puts the panel in its hidden pose at once (it was hidden without <see cref="PanelOut"/>).</summary>
    public static void PanelHidden(UIElement element)
    {
        var c = Compositor;
        element.StartAnimation(Keyframes("Opacity", null, 0f, TimeSpan.FromMilliseconds(1), ExitEase(c)));
    }

    /// <summary>
    /// Reveals the top <paramref name="delta"/> pixels of a surface that just grew upward, so a taller panel slides
    /// open instead of jumping. <paramref name="followers"/> (the parts above the change) ride along.
    /// </summary>
    public static void GrowFromBottom(UIElement surface, float delta, params UIElement[] followers)
    {
        if (delta <= 1 || Reduced) return;
        var visual = ElementCompositionPreview.GetElementVisual(surface);
        var c = visual.Compositor;
        var clip = c.CreateInsetClip();
        visual.Clip = clip;
        var anim = c.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, delta);
        anim.InsertKeyFrame(1f, 0f, Ease(c));
        anim.Duration = Normal;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        clip.StartAnimation("TopInset", anim);
        batch.End();
        batch.Completed += (_, _) => visual.Clip = null;
        foreach (var f in followers) f.StartAnimation(Vector("Translation", new Vector3(0, delta, 0), Vector3.Zero, Normal));
    }

    private static void StartLoop(UIElement element, params CompositionAnimation[] animations)
    {
        StopLoop(element);
        foreach (var a in animations) element.StartAnimation(a);
        Loops.AddOrUpdate(element, [.. animations]);
    }

    private static void StopLoop(UIElement element)
    {
        if (!Loops.TryGetValue(element, out var running)) return;
        foreach (var a in running) element.StopAnimation(a);
        Loops.Remove(element);
    }

    /// <summary>Endless "ping" halo (DailyDash LivePulseDot): grows and fades, restarts.</summary>
    public static void Ping(FrameworkElement halo, TimeSpan? period = null)
    {
        var p = period ?? TimeSpan.FromMilliseconds(1400);
        halo.CenterPoint = new Vector3((float)halo.Width / 2, (float)halo.Height / 2, 0);
        var scale = Vector("Scale", Vector3.One, new Vector3(2.8f, 2.8f, 1), p);
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        var fade = Scalar("Opacity", 0.55f, 0f, p);
        fade.IterationBehavior = AnimationIterationBehavior.Forever;
        StartLoop(halo, scale, fade);
    }

    public static void StopPing(UIElement halo)
    {
        StopLoop(halo);
        halo.Opacity = 0;
    }

    /// <summary>Gentle breathing (something is working on this element).</summary>
    public static void Breathe(UIElement element, float min = 0.35f)
    {
        var c = Compositor;
        var anim = c.CreateScalarKeyFrameAnimation();
        anim.Target = "Opacity";
        anim.InsertKeyFrame(0f, 1f);
        anim.InsertKeyFrame(0.5f, min, Ease(c));
        anim.InsertKeyFrame(1f, 1f, Ease(c));
        anim.Duration = TimeSpan.FromMilliseconds(1600);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        StartLoop(element, anim);
    }

    public static void StopBreathe(UIElement element)
    {
        StopLoop(element);
        element.Opacity = 1;
    }

    /// <summary>A soft drop shadow under a rounded element, drawn by the compositor behind it.</summary>
    public static DropShadow AttachShadow(FrameworkElement host, FrameworkElement target, float blur = 28, float opacity = 0.35f, float offsetY = 10, float radius = 26)
    {
        var c = Compositor;
        var sprite = c.CreateSpriteVisual();
        var shadow = c.CreateDropShadow();
        shadow.BlurRadius = blur;
        shadow.Opacity = opacity;
        shadow.Offset = new Vector3(0, offsetY, 0);
        shadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);

        var geometry = c.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(radius);
        var shape = c.CreateSpriteShape(geometry);
        shape.FillBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        var shapeVisual = c.CreateShapeVisual();
        shapeVisual.Shapes.Add(shape);
        var surface = c.CreateVisualSurface();
        surface.SourceVisual = shapeVisual;
        shadow.Mask = c.CreateSurfaceBrush(surface);
        sprite.Shadow = shadow;

        void Sync()
        {
            var size = new Vector2((float)target.ActualWidth, (float)target.ActualHeight);
            geometry.Size = size;
            shapeVisual.Size = size;
            surface.SourceSize = size;
            sprite.Size = size;
            sprite.Offset = new Vector3((float)target.Margin.Left, (float)target.Margin.Top, 0);
        }
        target.SizeChanged += (_, _) => Sync();
        host.SizeChanged += (_, _) => Sync();
        ElementCompositionPreview.SetElementChildVisual(host, sprite);
        Sync();
        return shadow;
    }
}
