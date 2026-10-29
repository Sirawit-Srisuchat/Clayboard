using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Clayboard.UI;

/// <summary>
/// A clickable row in a list (a binding, a group title, a profile) that also works without a mouse:
/// it takes keyboard focus, Enter or Space activates it, and screen readers see a named list item
/// they can invoke. A plain Border has none of that.
/// </summary>
public sealed class ListRow : Border
{
    public ListRow()
    {
        Focusable = true;
    }

    /// <summary>Enter, Space, or a screen reader's invoke. Mouse clicks are handled by the row's own handlers.</summary>
    public event RoutedEventHandler? Invoked;

    public static readonly DependencyProperty HoverBackgroundProperty =
        DependencyProperty.Register(nameof(HoverBackground), typeof(SolidColorBrush), typeof(ListRow));

    /// <summary>
    /// Faded in under the pointer. Its color is read on each hover, so a theme switch (which recolors the shared
    /// brush) shows on the next one. A style trigger can't do that: it would bake the color in when the style loads.
    /// </summary>
    public SolidColorBrush? HoverBackground
    {
        get => (SolidColorBrush?)GetValue(HoverBackgroundProperty);
        set => SetValue(HoverBackgroundProperty, value);
    }

    // This row's own brush for the fade, so animating it never touches the shared theme brush.
    private SolidColorBrush? _hoverFill;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        FadeHover(show: true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        FadeHover(show: false);
    }

    private void FadeHover(bool show)
    {
        if (HoverBackground is not { } hover)
            return;
        var color = hover.Color;
        var clear = Color.FromArgb(0, color.R, color.G, color.B);
        if (_hoverFill is null || !ReferenceEquals(Background, _hoverFill))
        {
            _hoverFill = new SolidColorBrush(clear);
            Background = _hoverFill;
        }
        var target = show ? color : clear;
        if (!Motion.Enabled)
        {
            _hoverFill.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _hoverFill.Color = target;
            return;
        }
        _hoverFill.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(show ? 120 : 250)));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Only keys pressed on the row itself; a focused switch or button inside handles its own.
        if (ReferenceEquals(e.OriginalSource, this) && e.Key is Key.Enter or Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            Invoked?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ListRowAutomationPeer(this);

    private sealed class ListRowAutomationPeer(ListRow owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetClassNameCore() => nameof(ListRow);

        protected override bool IsKeyboardFocusableCore() => true;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke() => owner.Invoked?.Invoke(owner, new RoutedEventArgs());
    }
}
