using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacroInWZa007.UI;

/// <summary>
/// Small, cheap animations: only opacity and render transforms, which WPF composes on the GPU
/// without re-running layout. Everything snaps instantly when Windows animations are turned off.
/// </summary>
internal static class Motion
{
    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseOutBack = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    /// <summary>Follows "Animation effects" in Windows settings and skips software-only rendering.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation && RenderCapability.Tier >> 16 > 0;

    /// <summary>Fades an element in while it rises <paramref name="fromY"/> pixels into place.</summary>
    public static void FadeIn(UIElement element, double fromY = 6, int ms = 180, int delayMs = 0)
    {
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        var duration = TimeSpan.FromMilliseconds(ms);
        var delay = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = EaseOut });
        Translate(element).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, duration) { BeginTime = delay, EasingFunction = EaseOut });
    }

    /// <summary>A quick grow-in with a slight overshoot, for things that just appeared, like a keycap.</summary>
    public static void Pop(UIElement element, int ms = 200)
    {
        if (!Enabled)
            return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.6, 0.6);
        element.RenderTransform = scale;
        var grow = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOutBack };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms * 0.6)));
    }

    /// <summary>Opens a dialog's content with a fade and a slight zoom from 97%.</summary>
    public static void ZoomIn(FrameworkElement element, int ms = 200)
    {
        if (!Enabled)
            return;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.97, 0.97);
        element.RenderTransform = scale;
        var grow = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    /// <summary>Glides a translate transform to <paramref name="x"/>, like a sliding selection pill.</summary>
    public static void SlideX(TranslateTransform transform, double x, int ms = 220)
    {
        if (!Enabled)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = x;
            return;
        }
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    private static TranslateTransform Translate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing && !existing.IsFrozen)
            return existing;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
