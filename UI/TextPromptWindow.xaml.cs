using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Clayboard.UI;

/// <summary>A small dialog that asks for one line of text, such as a group name.</summary>
public partial class TextPromptWindow : Window
{
    private readonly Func<string, string?> _accept;

    /// <param name="accept">Applies the text; returns an error to show, or null to close.</param>
    public TextPromptWindow(string title, string placeholder, string initial, Func<string, string?> accept)
    {
        InitializeComponent();
        _accept = accept;
        HeaderText.Text = title;
        Input.Tag = placeholder;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Motion.ZoomIn(Body);
            Input.Focus();
            Input.SelectAll();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCorners.Round(this);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_accept(Input.Text) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            Motion.FadeIn(ErrorText, fromY: 4);
            return;
        }
        DialogResult = true;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnDragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
