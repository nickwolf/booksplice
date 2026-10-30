using System.Windows;
using BookSplice.Core.Presets;

namespace BookSplice.Gui.Views;

public partial class PresetNameWindow : Window
{
  public PresetNameWindow(string title, string prompt, string initialName, bool offerOptional, bool templateDefault = true, bool folderDefault = false)
  {
    InitializeComponent();
    Title = title;
    Prompt.Text = prompt;
    NameBox.Text = initialName;
    IncludeTemplate.IsChecked = templateDefault;
    IncludeFolder.IsChecked = folderDefault;
    IncludeTemplate.Visibility = IncludeFolder.Visibility = offerOptional ? Visibility.Visible : Visibility.Collapsed;
    Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
  }

  public string EnteredName { get; private set; } = "";
  public bool IncludeOutputFolder => IncludeFolder.IsChecked == true;
  public bool IncludeOutputTemplate => IncludeTemplate.IsChecked == true;

  private void Save_Click(object sender, RoutedEventArgs e)
  {
    if (PresetNames.Check(NameBox.Text) is { } problem) { Problem.Text = problem; return; }
    EnteredName = NameBox.Text.Trim();
    DialogResult = true;
  }
}
