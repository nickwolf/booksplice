using System.Windows;
using System.Windows.Controls;

namespace BookSplice.Gui.Controls;

// A button that shows one glyph from the Segoe icon font. Give it a ToolTip and an AutomationProperties.Name.
public sealed class IconButton : Button
{
  public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
    nameof(Glyph), typeof(string), typeof(IconButton), new PropertyMetadata(""));

  static IconButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(IconButton), new FrameworkPropertyMetadata(typeof(IconButton)));

  public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
}
