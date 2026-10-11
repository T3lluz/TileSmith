using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace TileSmith.Theme;

/// <summary>
/// One place for motion, like DailyDash's MacroMotion: smooth, no bounce, each layer animates once.
/// Animations run on the compositor through UIElement.StartAnimation (Translation / Scale / Opacity),
/// so they never touch layout and coexist with XAML property transitions.
/// </summary>
public static class Motion
{
    /// <summary>Cinema-Info's --ease: cubic-bezier(0.22, 1, 0.36, 1) — quick out, soft landing.</summary>
    public static readonly Vector2 EaseP1 = new(0.22f, 1f);
    public static readonly Vector2 EaseP2 = new(0.36f, 1f);

    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(260);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(380);

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

    /// <summary>Fade + rise into place (entrances). Delay staggers groups.</summary>
    public static void Enter(UIElement element, float fromY = 10, int delayMs = 0, TimeSpan? duration = null)
    {
        var d = duration ?? Normal;
        element.StartAnimation(Scalar("Opacity", 0f, 1f, d, delayMs));
        element.StartAnimation(Vector("Translation", new Vector3(0, fromY, 0), Vector3.Zero, d, delayMs));
    }

    /// <summary>FLIP: the element jumped by <paramref name="delta"/> in layout; glide from the old spot.</summary>
    public static void Glide(UIElement element, Vector3 delta, TimeSpan? duration = null) =>
        element.StartAnimation(Vector("Translation", delta, Vector3.Zero, duration ?? Normal));

    /// <summary>Animates Translation from wherever it is now to <paramref name="to"/>.</summary>
    public static void TranslateTo(UIElement element, Vector3 to, TimeSpan? duration = null) =>
        element.StartAnimation(Vector("Translation", element.Translation, to, duration ?? Normal));

    /// <summary>The panel opening: grows from 96.5% and rises a little, like an Android sheet.</summary>
    public static void PanelIn(FrameworkElement element, bool fromBottom)
    {
        element.CenterPoint = new Vector3((float)element.ActualWidth / 2, fromBottom ? (float)element.ActualHeight : 0, 0);
        element.StartAnimation(Vector("Scale", new Vector3(0.965f, 0.965f, 1), Vector3.One, Slow));
        element.StartAnimation(Scalar("Opacity", 0f, 1f, TimeSpan.FromMilliseconds(200)));
        element.StartAnimation(Vector("Translation", new Vector3(0, fromBottom ? 18 : -18, 0), Vector3.Zero, Slow));
    }

    /// <summary>Quick fade/shrink out, then <paramref name="completed"/>.</summary>
    public static void PanelOut(UIElement element, Action completed)
    {
        var duration = TimeSpan.FromMilliseconds(110);
        element.StartAnimation(Scalar("Opacity", null, 0f, duration));
        element.StartAnimation(Vector("Scale", null, new Vector3(0.98f, 0.98f, 1), duration));
        var timer = new DispatcherTimer { Interval = duration + TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completed();
            // reset so the next open starts clean
            element.StartAnimation(Scalar("Opacity", null, 1f, TimeSpan.FromMilliseconds(1)));
            element.StartAnimation(Vector("Scale", null, Vector3.One, TimeSpan.FromMilliseconds(1)));
        };
        timer.Start();
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
    public static void AttachShadow(FrameworkElement host, FrameworkElement target, float blur = 28, float opacity = 0.35f, float offsetY = 10, float radius = 26)
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
    }
}
