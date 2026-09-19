using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.Diagnostics;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;
using Microsoft.Win32;

namespace BookSplice.Gui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The window disposes its view model in OnClosed.")]
public partial class MainWindow : Window
{
  private readonly ISettingsStore _store;
  private readonly GuiServices? _services;
  private int _coverVersion;
  private AppSettings _settings;
  private readonly MainWindowViewModel? _model;
  public MainWindow(ISettingsStore store, AppSettings settings, GuiServices? services = null)
  {
    InitializeComponent();
    _store = store;
    _services = services;
    _settings = settings;
    if (services is not null) DataContext = _model = new MainWindowViewModel(services.Analyzer, services.Conversion, settings, services.Planner);
    Destination.Text = $"Output folder: {settings.OutputDirectory}";
  }

  private void Settings_Click(object sender, RoutedEventArgs e)
  {
    if (_model?.Items.Any(item => item.IsBusy) == true)
    {
      MessageBox.Show(this, "Wait for active jobs before changing settings.", "BookSplice settings");
      return;
    }
    var model = new FirstLaunchViewModel(new FirstLaunchService(_store), _settings);
    var window = new FirstLaunchWindow(model) { Owner = this, Title = "BookSplice settings" };
    if (window.ShowDialog() != true) return;
    _settings = model.SavedSettings!;
    if (_model is not null) _model.Settings = _settings;
    Destination.Text = $"Output folder: {_settings.OutputDirectory} (applies to newly added books)";
  }

  private async void AddFolder_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFolderDialog { Title = "Choose an audiobook folder", Multiselect = true };
    if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FolderNames);
  }

  private async void AddFiles_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = "Choose individual audiobook files", Multiselect = true, Filter = "Audio files|*.mp3;*.aac;*.m4a;*.m4b;*.flac;*.ogg;*.opus;*.wma" };
    if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
  }

  private async Task AddPathsAsync(IEnumerable<string> paths)
  {
    if (_model is null) return;
    foreach (var path in paths) await _model.AddAsync(path);
    Queue.SelectedItem ??= _model.Items.FirstOrDefault();
  }

  private async void Window_Drop(object sender, DragEventArgs e)
  {
    if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths);
    e.Handled = true;
  }

  private void Window_DragOver(object sender, DragEventArgs e)
  {
    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    e.Handled = true;
  }

  private async void Order_Click(object sender, RoutedEventArgs e)
  {
    if (_model is not null && Queue.SelectedItem is BookQueueItemViewModel item && OrderChoice.SelectedItem is OrderCandidate candidate)
      await _model.ResolveOrderAsync(item, candidate.Id);
  }

  private async void Preview_Click(object sender, RoutedEventArgs e)
  {
    if (_model is not null && Queue.SelectedItem is BookQueueItemViewModel item) await _model.PreviewAsync(item);
  }

  private async void Cover_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
  {
    var version = ++_coverVersion;
    if (CoverImage is null) return;
    CoverImage.Source = null;
    if (_services is null || sender is not System.Windows.Controls.ComboBox { SelectedItem: Core.Covers.CoverCandidate cover }) return;
    try
    {
      var bitmap = await Task.Run(async () =>
      {
        await using var stream = await _services.CoverPayloads.OpenReadAsync(
          new(cover.Origin, cover.SourcePath, cover.EmbeddedPictureIndex, cover.SourceIdentity), CancellationToken.None);
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 512;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
      });
      if (version == _coverVersion) CoverImage.Source = bitmap;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException)
    {
      if (version == _coverVersion && Queue.SelectedItem is BookQueueItemViewModel item) item.Details = $"Cover preview unavailable: {exception.Message}";
    }
  }

  private async void Convert_Click(object sender, RoutedEventArgs e)
  {
    if (_model is not null && Queue.SelectedItem is BookQueueItemViewModel item) await _model.ConvertAsync(item);
  }

  private async void ConvertAll_Click(object sender, RoutedEventArgs e)
  {
    if (_model is not null) await _model.ConvertAllAsync();
  }

  private void Cancel_Click(object sender, RoutedEventArgs e)
  {
    if (Queue.SelectedItem is BookQueueItemViewModel item) item.Cancel();
  }

  private void Remove_Click(object sender, RoutedEventArgs e)
  {
    if (_model is not null && Queue.SelectedItem is BookQueueItemViewModel { IsBusy: false } item) _model.Items.Remove(item);
  }

  private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel item) return;
    try
    {
      Clipboard.SetText(DiagnosticsTextBuilder.Build(item));
      MessageBox.Show(this, "Diagnostics copied. Local paths were redacted.", "Copy diagnostics");
    }
    catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
    {
      MessageBox.Show(this, $"Could not copy diagnostics: {exception.Message}", "Copy diagnostics");
    }
  }
  private void OpenOutput_Click(object sender, RoutedEventArgs e)
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel { PublishedPath: { } path } || !File.Exists(path)) return;
    try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { Path.GetDirectoryName(path)! } }); }
    catch (Win32Exception exception) { MessageBox.Show(this, exception.Message, "Unable to open output folder"); }
  }

  protected override void OnClosed(EventArgs e) { _model?.Dispose(); base.OnClosed(e); }

  protected override void OnClosing(CancelEventArgs e)
  {
    if (_model?.Items.Any(item => item.IsBusy) == true)
    {
      e.Cancel = true;
      MessageBox.Show(this, "Cancel active jobs and wait for cleanup before closing BookSplice.", "Jobs are still running");
    }
    base.OnClosing(e);
  }
}
