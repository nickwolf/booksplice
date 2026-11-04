using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BookSplice.Gui.History;
using BookSplice.Gui.Updates;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Views;

public enum SettingsSection { Settings, Presets, History, Updates }

// What the Settings window hosts besides the settings themselves. History and Updates are null until they are attached.
public sealed record SettingsPages(PresetsViewModel Presets, Func<BookQueueItemViewModel?> SelectedBook, ConversionHistoryViewModel? History, UpdatesViewModel? Updates);

// First-run setup shows only the settings. Opened from the main window with pages, it is the Settings window with Presets, History and Updates beside them.
public partial class FirstLaunchWindow : Window
{
  private readonly FirstLaunchViewModel _model;
  private readonly SettingsPage _settings = new();
  private readonly UpdatesViewModel? _updates;
  private readonly TabControl? _tabs;
  private readonly Dictionary<SettingsSection, TabItem> _items = [];
  private readonly UpdatesPage? _updatesPage;
  private readonly string? _lockedReason;

  public FirstLaunchWindow(FirstLaunchViewModel model, SettingsPages? pages = null, string? lockedReason = null)
  {
    InitializeComponent();
    DataContext = _model = model;
    _lockedReason = lockedReason;
    _updates = pages?.Updates;
    if (pages is null) Host.Content = _settings;
    else
    {
      _settings.Welcome.Visibility = Visibility.Collapsed;
      Width = 820;
      Height = 680;
      MinWidth = 640;
      MinHeight = 480;
      _tabs = new TabControl { Margin = new Thickness(8, 8, 8, 0) };
      _tabs.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(FirstLaunchViewModel.CanEdit)));
      AddTab(SettingsSection.Settings, "Settings", _settings);
      AddTab(SettingsSection.Presets, "Presets", new PresetsPage(pages.Presets, pages.SelectedBook));
      if (pages.History is { } history) AddTab(SettingsSection.History, "History", new HistoryPage(history));
      if (_updates is not null) AddTab(SettingsSection.Updates, "Updates", _updatesPage = new UpdatesPage(_updates));
      _tabs.SelectedIndex = 0;
      _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, _tabs)) UpdateFooter(); };
      Host.Content = _tabs;
      UpdateFooter();
    }
    if (lockedReason is not null) _settings.IsEnabled = false;
    Loaded += (_, _) => { if (_tabs is null) _settings.FocusFirst(); };
  }

  private void AddTab(SettingsSection section, string header, UIElement content)
  {
    var item = new TabItem { Header = header, Content = content };
    _items[section] = item;
    _tabs!.Items.Add(item);
  }

  public SettingsSection CurrentSection => _tabs is null ? SettingsSection.Settings : _items.First(pair => pair.Value == _tabs.SelectedItem).Key;

  public IReadOnlyCollection<SettingsSection> Sections => _tabs is null ? [SettingsSection.Settings] : [.. _items.Keys];

  // A section that is not hosted (History or Updates before they are attached) leaves the current page showing.
  public void ShowSection(SettingsSection section)
  {
    if (_tabs is null || !_items.TryGetValue(section, out var item)) return;
    _tabs.SelectedItem = item;
    if (section == SettingsSection.Updates) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => _updatesPage?.FocusCheck()));
  }

  // Cancel closes without saving; on the other pages nothing is pending, so the same button just closes.
  private void UpdateFooter()
  {
    var onSettings = CurrentSection == SettingsSection.Settings;
    CloseButton.Content = onSettings ? "_Cancel" : "Cl_ose";
    SaveButton.Visibility = Error.Visibility = onSettings ? Visibility.Visible : Visibility.Collapsed;
    SaveButton.IsDefault = onSettings;
    SaveButton.IsEnabled = onSettings && _lockedReason is null;
    LockedNotice.Text = _lockedReason ?? "";
    LockedNotice.Visibility = onSettings && _lockedReason is not null ? Visibility.Visible : Visibility.Collapsed;
  }

  private async void Save_Click(object sender, RoutedEventArgs e)
  {
    if (await _model.SaveAsync()) DialogResult = true;
  }

  protected override void OnClosing(CancelEventArgs e)
  {
    // Closing while a restart is in progress would hide the status the user needs.
    if (_model.IsBusy || _updates?.Phase is UpdatesPhase.Preparing or UpdatesPhase.Restarting) e.Cancel = true;
    base.OnClosing(e);
  }
}
