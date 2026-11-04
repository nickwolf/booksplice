using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BookSplice.Gui.Controls;

// A small "i" glyph that opens a popup with a short explanation. Space, Enter or a click toggles it; Escape or leaving the control closes it.
public sealed class InfoIcon : ToggleButton
{
  public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
    nameof(Text), typeof(string), typeof(InfoIcon), new PropertyMetadata("", (d, e) => AutomationProperties.SetHelpText(d, (string)e.NewValue)));

  public static readonly DependencyProperty SubjectProperty = DependencyProperty.Register(
    nameof(Subject), typeof(string), typeof(InfoIcon), new PropertyMetadata("", (d, e) => AutomationProperties.SetName(d, $"About {(string)e.NewValue}")));

  static InfoIcon() => DefaultStyleKeyProperty.OverrideMetadata(typeof(InfoIcon), new FrameworkPropertyMetadata(typeof(InfoIcon)));

  public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

  // What the explanation is about, used for the accessible name.
  public string Subject { get => (string)GetValue(SubjectProperty); set => SetValue(SubjectProperty, value); }

  protected override void OnKeyDown(KeyEventArgs e)
  {
    if (e.Key == Key.Escape && IsChecked == true) { IsChecked = false; e.Handled = true; return; }
    base.OnKeyDown(e);
  }

  protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
  {
    base.OnLostKeyboardFocus(e);
    IsChecked = false;
  }
}
