using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

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
