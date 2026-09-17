using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui;

public partial class MainWindow : Window
{
  private readonly ISettingsStore _store;
  private AppSettings _settings;
  public MainWindow(ISettingsStore store, AppSettings settings)
  {
    InitializeComponent();
    _store = store;
    _settings = settings;
    Destination.Text = $"Output folder: {settings.OutputDirectory}";
  }

  private void Settings_Click(object sender, RoutedEventArgs e)
  {
    var model = new FirstLaunchViewModel(new FirstLaunchService(_store), _settings);
    var window = new FirstLaunchWindow(model) { Owner = this, Title = "BookSplice settings" };
    if (window.ShowDialog() != true) return;
    _settings = model.SavedSettings!;
    Destination.Text = $"Output folder: {_settings.OutputDirectory}";
  }
}
