using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BookSplice.Core.Discovery;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.Diagnostics;
using BookSplice.Gui.Notifications;
using BookSplice.Gui.Updates;
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
  private bool _showingOrder;
  private AppSettings _settings;
  private readonly MainWindowViewModel? _model;
  private readonly GuiOperationGate _gate;
  private UpdatesViewModel? _updates;
  private bool _closeForUpdate;
  private BookSplice.Gui.History.ConversionHistoryReader? _history;
  private FirstLaunchWindow? _settingsWindow;
  private readonly PresetsViewModel _presets;
  public MainWindow(ISettingsStore store, AppSettings settings, GuiServices? services = null)
  {
    InitializeComponent();
    _store = store;
    _services = services;
    _settings = settings;
    _gate = new GuiOperationGate(Dispatcher);
    if (services is not null) DataContext = _model = new MainWindowViewModel(services.Analyzer, services.Conversion, settings, services.Planner, _gate, services.Profiles,
      new WindowsToastNotifier(Environment.ProcessPath, key => Dispatcher.BeginInvoke(() => ShowResult(key))), coverPayloads: services.CoverPayloads);
    WindowsToastNotifier.Sync(settings.NotificationsEnabled, Environment.ProcessPath);
    Destination.Text = $"Output folder: {settings.OutputDirectory}";
    _presets = new PresetsViewModel(new Core.Presets.PresetStore(Path.Combine(Path.GetDirectoryName(store.SettingsPath)!, Core.Presets.PresetStore.FileName)), services?.Profiles);
    PresetPicker.ItemsSource = _presets.Presets;
    _presets.Reload();
    SelectedBookPanel.DataContextChanged += SelectedBook_DataContextChanged;
    UpdateTrackButtons();
  }

  internal UpdatesViewModel? Updates => _updates;

  public void UseUpdates(IUpdateBackend backend, UpdatePreferencesStore store, UpdatePreferences preferences,
    string installedVersion, string installLocation)
  {
    _updates = new UpdatesViewModel(backend, store, preferences, installedVersion, installLocation, _gate,
      () => _model?.Items.Count ?? 0, () => { _closeForUpdate = true; Application.Current.Shutdown(); });
    _updates.PropertyChanged += (_, _) => ShowUpdateButton();
    _ = _updates.InitializeAsync();
    _updates.StartSchedule();
  }

  // There is no automatic popup: the toolbar button is the notice, and it follows the Updates view model.
  private void ShowUpdateButton()
  {
    var available = _updates is { HasAvailable: true, AvailableVersion: { } version } ? version : null;
    UpdateAvailableButton.Visibility = available is null ? Visibility.Collapsed : Visibility.Visible;
    if (available is not null) UpdateAvailableButton.Content = $"BookSplice {available.Replace("_", "__", StringComparison.Ordinal)} a_vailable";
  }

  private void UpdateAvailable_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsSection.Updates);

  // Reads the saved queue and keeps it saved. Problems show in the queue notice, never in a dialog.
  public void UseQueue(string dataDirectory)
  {
    if (_model is not null) _ = _model.AttachQueueAsync(new BookSplice.Core.Queue.QueueSnapshotStore(dataDirectory));
  }

  public void UseHistory(string logDirectory)
  {
    _history = new(logDirectory);
  }

  private void ApplyPreset_Click(object sender, RoutedEventArgs e)
  {
    if (!_presets.Apply(Queue.SelectedItem as BookQueueItemViewModel, (PresetPicker.SelectedItem as PresetEntryViewModel)?.Name))
      MessageBox.Show(this, _presets.Message, "Apply preset");
  }

  // Replaced in tests so no modal dialog opens.
  internal Func<Window, string, string?> ChooseFolder { get; set; } = (owner, initial) =>
  {
    var dialog = new OpenFolderDialog { Title = "Choose the destination folder", InitialDirectory = initial };
    return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
  };

  private void BrowseDestination_Click(object sender, RoutedEventArgs e)
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel { CanEdit: true } item) return;
    if (ChooseFolder(this, item.OutputDirectory) is { } folder) item.OutputDirectory = folder;
  }

  // Replaced in tests so no modal dialog opens.
  internal Func<MetadataEditorWindow, bool?> ShowMetadataEditor { get; set; } = editor => editor.ShowDialog();

  private void EditMetadata_Click(object sender, RoutedEventArgs e)
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel { CanEdit: true } item) return;
    var editor = new MetadataEditorWindow(item.Name, item.Metadata) { Owner = this };
    if (ShowMetadataEditor(editor) == true) editor.Apply();
  }

  private void SavePreset_Click(object sender, RoutedEventArgs e)
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel item) return;
    var prompt = new PresetNameWindow("Save as preset", "Name the preset. It keeps this book's quality, channels, chapters, validation, metadata profile and name template.", "", offerOptional: true) { Owner = this };
    if (prompt.ShowDialog() != true) return;
    if (!_presets.SaveNew(prompt.EnteredName, item.Settings, prompt.IncludeOutputFolder, prompt.IncludeOutputTemplate)) MessageBox.Show(this, _presets.Message, "Save as preset");
  }

  // Returns null, after telling the user, while an update is being prepared.
  private IDisposable? TryEnter(string operation)
  {
    if (!_gate.IsFrozen) return _gate.Enter(operation);
    MessageBox.Show(this, "BookSplice is preparing an update. Try again if the update is cancelled.", "BookSplice update");
    return null;
  }

  // Runs only while the window exists: brings it forward and selects the finished book, and never starts work.
  private void ShowResult(string? key)
  {
    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    _ = Activate();
    if (key is not null && _model?.Items.FirstOrDefault(item => item.Source == key) is { } item) Queue.SelectedItem = item;
  }

  private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsSection.Settings);

  // Presets, History and Updates stay usable while books convert; only the settings themselves wait for idle jobs.
  // The window does not hold the operation gate, because installing an update needs it free.
  private void ShowSettings(SettingsSection section)
  {
    if (_settingsWindow is { } open)
    {
      open.ShowSection(section);
      open.Activate();
      return;
    }
    if (_gate.IsFrozen)
    {
      MessageBox.Show(this, "BookSplice is preparing an update. Try again if the update is cancelled.", "BookSplice update");
      return;
    }
    var locked = _model?.Items.Any(item => item.IsBusy) == true ? "Wait for active jobs to finish before changing settings." : null;
    _ = _updates?.InitializeAsync();
    using var history = _history is null ? null : new BookSplice.Gui.History.ConversionHistoryViewModel(_history);
    var pages = new SettingsPages(_presets, () => Queue.SelectedItem as BookQueueItemViewModel, history, _updates);
    var model = new FirstLaunchViewModel(new FirstLaunchService(_store, profiles: _services?.Profiles), _settings, _services?.Profiles);
    var window = _settingsWindow = new FirstLaunchWindow(model, pages, locked) { Owner = this, Title = "BookSplice settings" };
    window.ShowSection(section);
    bool saved;
    try { saved = window.ShowDialog() == true; }
    finally { _settingsWindow = null; }
    if (!saved) return;
    _settings = model.SavedSettings!;
    if (_model is not null) _model.Settings = _settings;
    WindowsToastNotifier.Sync(_settings.NotificationsEnabled, Environment.ProcessPath);
    Destination.Text = $"Output folder: {_settings.OutputDirectory} (applies to newly added books)";
  }

  private async void AddFolder_Click(object sender, RoutedEventArgs e)
  {
    using var operation = TryEnter("add folder");
    if (operation is null || _model is null) return;
    var dialog = new OpenFolderDialog { Title = "Choose audiobook folders", Multiselect = true };
    if (dialog.ShowDialog(this) != true) return;
    await _model.AddFoldersAsync(dialog.FolderNames, ReviewBooks);
    Queue.SelectedItem ??= _model.Items.FirstOrDefault();
  }

  // The folder was already scanned to decide that it holds several books, so the preview reuses that scan.
  private IReadOnlyList<BookGroup>? ReviewBooks(string folder, BookGroupPlan plan)
  {
    var preview = new BookGroupPreviewWindow(new BookGroupPreviewViewModel(folder, (_, _, _) => Task.FromResult(plan))) { Owner = this };
    return preview.ShowDialog() == true ? preview.AcceptedGroups : null;
  }

  private async void AddFiles_Click(object sender, RoutedEventArgs e)
  {
    using var operation = TryEnter("add files");
    if (operation is null) return;
    var dialog = new OpenFileDialog { Title = "Choose individual audiobook files", Multiselect = true, Filter = "Audio files|*.mp3;*.aac;*.m4a;*.m4b;*.flac;*.ogg;*.opus;*.wma" };
    if (dialog.ShowDialog(this) != true || _model is null) return;
    await _model.AddFilesAsync(dialog.FileNames, files =>
    {
      var choice = new ImportChoiceWindow(files) { Owner = this };
      return choice.ShowDialog() == true ? choice.Choice : FileImportChoice.Cancel;
    });
    Queue.SelectedItem ??= _model.Items.FirstOrDefault();
  }

  private async Task AddPathsAsync(IEnumerable<string> paths)
  {
    if (_model is null) return;
    await _model.AddManyAsync(paths);
    Queue.SelectedItem ??= _model.Items.FirstOrDefault();
  }

  private async void Window_Drop(object sender, DragEventArgs e)
  {
    e.Handled = true;
    if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || _gate.IsFrozen) return;
    using var operation = _gate.Enter("drop");
    await AddPathsAsync(paths);
  }

  private void Window_DragOver(object sender, DragEventArgs e)
  {
    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    e.Handled = true;
  }

  private async void Order_Click(object sender, RoutedEventArgs e)
  {
    if (_model is not null && Queue.SelectedItem is BookQueueItemViewModel item)
      await _model.UseTrackListAsync(item, (OrderChoice.SelectedItem as OrderChoiceViewModel)?.Id);
  }

  private void SelectedBook_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
  {
    if (e.OldValue is BookQueueItemViewModel old)
    {
      old.PropertyChanged -= SelectedBook_PropertyChanged;
      old.IncludedTracks.CollectionChanged -= TrackLists_Changed;
      old.ExcludedTracks.CollectionChanged -= TrackLists_Changed;
    }
    if (e.NewValue is BookQueueItemViewModel item)
    {
      item.PropertyChanged += SelectedBook_PropertyChanged;
      item.IncludedTracks.CollectionChanged += TrackLists_Changed;
      item.ExcludedTracks.CollectionChanged += TrackLists_Changed;
    }
    ShowOrderChoice(e.NewValue as BookQueueItemViewModel);
  }

  private void SelectedBook_PropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (e.PropertyName == nameof(BookQueueItemViewModel.Analysis) && sender is BookQueueItemViewModel item) ShowOrderChoice(item);
  }

  private void TrackLists_Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateTrackButtons();

  // Fills the dropdown and shows the book's current order in it. This is not a user pick, so it must never reload the track list.
  private void ShowOrderChoice(BookQueueItemViewModel? item)
  {
    var choices = item?.OrderChoices ?? [];
    var preselected = item?.PreselectedOrder;
    _showingOrder = true;
    try
    {
      OrderChoice.ItemsSource = choices;
      OrderChoice.SelectedItem = choices.FirstOrDefault(choice => choice.Id == preselected);
    }
    finally { _showingOrder = false; }
    UpdateOrderPlaceholder();
    UpdateTrackButtons();
  }

  private void UpdateOrderPlaceholder()
  {
    var needsChoice = OrderChoice.Items.Count > 0 && OrderChoice.SelectedItem is null;
    OrderPlaceholder.Visibility = needsChoice ? Visibility.Visible : Visibility.Collapsed;
    System.Windows.Automation.AutomationProperties.SetHelpText(OrderChoice, needsChoice ? "Choose a track order" : "");
  }

  private void OrderChoice_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
  {
    if (_showingOrder) return;
    UpdateOrderPlaceholder();
    if (Queue.SelectedItem is BookQueueItemViewModel { CanEdit: true } item && OrderChoice.SelectedItem is OrderChoiceViewModel choice)
      item.LoadTrackList(choice.Candidate.FileIds, []);
  }

  private void TrackList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateTrackButtons();

  private void UpdateTrackButtons()
  {
    var index = IncludedList.SelectedIndex;
    TrackUpButton.IsEnabled = index > 0;
    TrackDownButton.IsEnabled = index >= 0 && index < IncludedList.Items.Count - 1;
    TrackExcludeButton.IsEnabled = index >= 0;
    TrackIncludeButton.IsEnabled = ExcludedList.SelectedIndex >= 0;
  }

  private void TrackUp_Click(object sender, RoutedEventArgs e) => ApplyTrackAction(TrackListAction.MoveUp, IncludedList);
  private void TrackDown_Click(object sender, RoutedEventArgs e) => ApplyTrackAction(TrackListAction.MoveDown, IncludedList);
  private void TrackExclude_Click(object sender, RoutedEventArgs e) => ApplyTrackAction(TrackListAction.Exclude, IncludedList);
  private void TrackInclude_Click(object sender, RoutedEventArgs e) => ApplyTrackAction(TrackListAction.Include, ExcludedList);
  private void IncludedList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) => HandleTrackKey(e, IncludedList, false);
  private void ExcludedList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) => HandleTrackKey(e, ExcludedList, true);

  private void HandleTrackKey(System.Windows.Input.KeyEventArgs e, System.Windows.Controls.ListBox list, bool excluded)
  {
    var action = TrackListKeys.Resolve(e.Key, e.SystemKey, System.Windows.Input.Keyboard.Modifiers, excluded);
    if (action == TrackListAction.None) return;
    e.Handled = true;
    ApplyTrackAction(action, list);
  }

  private void ApplyTrackAction(TrackListAction action, System.Windows.Controls.ListBox list)
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel item || list.SelectedItem is not string track) return;
    var index = list.SelectedIndex;
    var changed = item.ApplyTrackAction(action, track);
    if (!changed) return;
    var target = action switch
    {
      TrackListAction.MoveUp or TrackListAction.MoveDown => item.IncludedTracks.IndexOf(track),
      _ => Math.Min(index, list.Items.Count - 1),
    };
    list.SelectedIndex = target;
    (list.ItemContainerGenerator.ContainerFromIndex(target) as System.Windows.Controls.ListBoxItem)?.Focus();
  }

  private async void Cover_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
  {
    var version = ++_coverVersion;
    if (CoverImage is null) return;
    CoverImage.Source = null;
    if (_services is null || _gate.IsFrozen || sender is not System.Windows.Controls.ComboBox { SelectedItem: Core.Covers.CoverCandidate cover }) return;
    using var operation = _gate.Enter("cover read");
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

  private async void RetryFailed_Click(object sender, RoutedEventArgs e)
  {
    if (_model is not null) await _model.RetryFailedAsync();
  }

  private void PauseQueue_Click(object sender, RoutedEventArgs e)
  {
    if (_model is null) return;
    if (_model.IsQueuePaused) _model.ResumeQueue();
    else _model.PauseQueue();
  }

  private void ClearCompleted_Click(object sender, RoutedEventArgs e) => _model?.ClearCompleted();

  private void RowMoveUp_Click(object sender, RoutedEventArgs e) => MoveBook(RowItem(sender), -1);
  private void RowMoveDown_Click(object sender, RoutedEventArgs e) => MoveBook(RowItem(sender), 1);
  private void RowRemove_Click(object sender, RoutedEventArgs e) => RemoveBook(RowItem(sender));

  private static BookQueueItemViewModel? RowItem(object sender) => (sender as FrameworkElement)?.DataContext as BookQueueItemViewModel;

  // Alt+Up and Alt+Down move the selected book and Delete removes it, while the queue list has focus.
  private void Queue_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
  {
    if (Queue.SelectedItem is not BookQueueItemViewModel item) return;
    switch (QueueKeys.Resolve(e.Key, e.SystemKey, System.Windows.Input.Keyboard.Modifiers))
    {
      case QueueAction.MoveUp: MoveBook(item, -1); break;
      case QueueAction.MoveDown: MoveBook(item, 1); break;
      case QueueAction.Remove: RemoveBook(item); break;
      default: return;
    }
    e.Handled = true;
  }

  private void MoveBook(BookQueueItemViewModel? item, int offset)
  {
    if (_model is null || item is null) return;
    _model.Move(item, offset);
    Queue.SelectedItem = item;
    (Queue.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.ListBoxItem)?.Focus();
  }

  // A book that is running stays in the queue. The next book takes the selection so the list keeps its place.
  private void RemoveBook(BookQueueItemViewModel? item)
  {
    if (_model is null || item is null) return;
    var index = _model.Items.IndexOf(item);
    if (!_model.Remove(item)) return;
    if (Queue.SelectedItem is null && _model.Items.Count > 0) Queue.SelectedIndex = Math.Min(index, _model.Items.Count - 1);
    (Queue.ItemContainerGenerator.ContainerFromIndex(Queue.SelectedIndex) as System.Windows.Controls.ListBoxItem)?.Focus();
  }

  private void Cancel_Click(object sender, RoutedEventArgs e)
  {
    if (Queue.SelectedItem is BookQueueItemViewModel item) item.Cancel();
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

  protected override void OnClosed(EventArgs e) { _model?.FlushQueue(); _updates?.Dispose(); _model?.Dispose(); base.OnClosed(e); }

  protected override void OnClosing(CancelEventArgs e)
  {
    if (_gate.IsFrozen && !_closeForUpdate)
    {
      e.Cancel = true;
      MessageBox.Show(this, "BookSplice is preparing an update and will restart when it is ready.", "Update in progress");
      return;
    }
    if (_model?.Items.Any(item => item.IsBusy) == true)
    {
      e.Cancel = true;
      MessageBox.Show(this, "Cancel active jobs and wait for cleanup before closing BookSplice.", "Jobs are still running");
    }
    base.OnClosing(e);
  }
}
