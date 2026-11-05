using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;
using BookSplice.Gui.Controls;
using BookSplice.Gui.History;
using BookSplice.Gui.Updates;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class SettingsWindowTests : IDisposable
{
  private static readonly string[] RemovedSentences = ["preserved by default", "Use {title}", "Converting more books at once", "Choose where finished audiobooks go", "Full decodes the entire output", "blank uses Automatic"];
  private static readonly string[] FooterNames = ["CloseButton", "SaveButton"];
  private readonly string _root = Directory.CreateTempSubdirectory("booksplice-settings-window-").FullName;

  public void Dispose() => Directory.Delete(_root, true);

  private FirstLaunchViewModel Model() => new(new FirstLaunchService(new JsonSettingsStore(_root)), AppSettings.Defaults);

  private FirstLaunchWindow Window(System.Windows.Threading.Dispatcher dispatcher, bool history = true, bool updates = true, string? locked = null)
  {
    var presets = new PresetsViewModel(new PresetStore(Path.Combine(_root, PresetStore.FileName)));
    var historyModel = history ? new ConversionHistoryViewModel(new ConversionHistoryReader(_root)) : null;
    var updatesModel = updates
      ? new UpdatesViewModel(new IdleUpdateBackend(), new UpdatePreferencesStore(_root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0",
        @"C:\Apps\BookSplice", new GuiOperationGate(dispatcher), () => 0, () => { })
      : null;
    var window = new FirstLaunchWindow(Model(), new SettingsPages(presets, () => null, historyModel, updatesModel), locked)
    { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
    window.Measure(new Size(window.Width, window.Height));
    window.Arrange(new Rect(0, 0, window.Width, window.Height));
    window.UpdateLayout();
    return window;
  }

  [Fact]
  public void SettingsHostsPresetsHistoryAndUpdatesAsPagesAndSwitchesTheFooter() => DispatcherTestHost.Run(dispatcher =>
  {
    var window = Window(dispatcher);
    Assert.Equal([SettingsSection.Settings, SettingsSection.Presets, SettingsSection.History, SettingsSection.Updates], window.Sections);
    var tabs = Assert.Single(Descendants<TabControl>(window));
    Assert.Equal(["Settings", "Presets", "History", "Updates"], tabs.Items.OfType<TabItem>().Select(tab => tab.Header));
    Assert.IsType<SettingsPage>(((TabItem)tabs.Items[0]!).Content);
    Assert.IsType<PresetsPage>(((TabItem)tabs.Items[1]!).Content);
    Assert.IsType<HistoryPage>(((TabItem)tabs.Items[2]!).Content);
    Assert.IsType<UpdatesPage>(((TabItem)tabs.Items[3]!).Content);
    var close = Assert.IsType<Button>(window.FindName("CloseButton"));
    var save = Assert.IsType<Button>(window.FindName("SaveButton"));
    Assert.Equal("_Cancel", close.Content);
    Assert.Equal(Visibility.Visible, save.Visibility);
    window.ShowSection(SettingsSection.Updates);
    Assert.Equal(SettingsSection.Updates, window.CurrentSection);
    Assert.Equal("Cl_ose", close.Content);
    Assert.Equal(Visibility.Collapsed, save.Visibility);
    Assert.False(save.IsDefault);
    window.ShowSection(SettingsSection.Settings);
    Assert.Equal("_Cancel", close.Content);
    Assert.True(save.IsDefault);
    return Task.CompletedTask;
  });

  [Fact]
  public void HistoryAndUpdatesPagesAppearOnlyWhenAttached() => DispatcherTestHost.Run(dispatcher =>
  {
    var window = Window(dispatcher, history: false, updates: false);
    Assert.Equal([SettingsSection.Settings, SettingsSection.Presets], window.Sections);
    window.ShowSection(SettingsSection.Updates);
    Assert.Equal(SettingsSection.Settings, window.CurrentSection);
    return Task.CompletedTask;
  });

  [Fact]
  public void FirstLaunchShowsOnlyTheSettingsWithoutTabs() => DispatcherTestHost.Run(_ =>
  {
    var window = new FirstLaunchWindow(Model());
    Assert.Empty(Descendants<TabControl>(window));
    Assert.Equal([SettingsSection.Settings], window.Sections);
    Assert.Single(Descendants<SettingsPage>(window));
    return Task.CompletedTask;
  });

  [Fact]
  public void LockedSettingsDisableTheSettingsPageAndSaveButKeepTheOtherPagesUsable() => DispatcherTestHost.Run(dispatcher =>
  {
    var window = Window(dispatcher, locked: "Wait for active jobs to finish before changing settings.");
    var save = Assert.IsType<Button>(window.FindName("SaveButton"));
    Assert.False(save.IsEnabled);
    Assert.False(Assert.Single(Descendants<SettingsPage>(window)).IsEnabled);
    Assert.True(Assert.Single(Descendants<PresetsPage>(window)).IsEnabled);
    Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(window.FindName("LockedNotice")).Visibility);
    window.ShowSection(SettingsSection.History);
    Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(window.FindName("LockedNotice")).Visibility);
    return Task.CompletedTask;
  });

  [Fact]
  public void EverySettingHasAnInfoIconAndTheAlwaysVisibleExplainersAreGone() => DispatcherTestHost.Run(dispatcher =>
  {
    var window = Window(dispatcher);
    var page = Assert.Single(Descendants<SettingsPage>(window));
    // Bindings inside the collapsed Advanced settings attach once the window shows and it is expanded.
    window.Show();
    Assert.Single(Descendants<Expander>(page)).IsExpanded = true;
    window.UpdateLayout();
    var settings = Descendants<ComboBox>(page).Cast<Control>().Concat(Descendants<TextBox>(page)).Concat(Descendants<CheckBox>(page)).ToArray();
    var icons = Descendants<InfoIcon>(page).ToArray();
    Assert.Equal(12, settings.Length);
    Assert.Equal(settings.Length, icons.Length);
    AssertAccessibleIcons(icons);
    Assert.Contains(icons, icon => icon.Subject == "metadata profile");
    Assert.Contains(icons, icon => icon.Subject == "logging level");
    Assert.Contains(icons, icon => icon.Subject == "folder and file name template" && icon.Text.Contains("{title}", StringComparison.Ordinal));
    Assert.DoesNotContain(Descendants<TextBlock>(page), block => RemovedSentences.Any(sentence => block.Text.Contains(sentence, StringComparison.Ordinal)));
    Assert.DoesNotContain(Descendants<Label>(page), label => label.Content is string text && RemovedSentences.Any(sentence => text.Contains(sentence, StringComparison.Ordinal)));
    var updates = Assert.Single(Descendants<UpdatesPage>(window));
    var automatic = Assert.IsType<CheckBox>(updates.FindName("Automatic"));
    AssertAccessibleIcons(Assert.Single(Descendants<InfoIcon>(updates)));
    Assert.Same(automatic.Parent, Assert.Single(Descendants<InfoIcon>(updates)).Parent);
    window.Close();
    return Task.CompletedTask;
  });

  [Fact]
  public void PresetsPageLabelsBothListsAndExplainsPresetsBehindAnInfoIcon() => DispatcherTestHost.Run(dispatcher =>
  {
    var page = Assert.Single(Descendants<PresetsPage>(Window(dispatcher)));
    var texts = Descendants<TextBlock>(page).Select(block => block.Text).ToArray();
    Assert.Contains("Saved presets", texts);
    Assert.Contains("Selected preset", texts);
    var icon = Assert.Single(Descendants<InfoIcon>(page));
    AssertAccessibleIcons(icon);
    Assert.Contains("A preset is a named set of conversion options", icon.Text, StringComparison.Ordinal);
    Assert.Contains("Use one to switch", icon.Text, StringComparison.Ordinal);
    return Task.CompletedTask;
  });

  [Fact]
  public void MainToolbarNoLongerOffersPresetsHistoryOrUpdates() => DispatcherTestHost.Run(_ =>
  {
    var window = new MainWindow(new JsonSettingsStore(_root), AppSettings.Defaults);
    window.UseUpdates(new IdleUpdateBackend(), new UpdatePreferencesStore(_root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0", @"C:\Apps\BookSplice");
    window.UseHistory(_root);
    Assert.Null(window.FindName("PresetsButton"));
    Assert.Null(window.FindName("UpdatesButton"));
    Assert.DoesNotContain(LogicalTreeHelper.GetChildren(Assert.IsType<WrapPanel>(window.FindName("PrimaryActions"))).OfType<Button>(), button => Equals(button.Content, "Check for _updates..."));
    Assert.Null(window.FindName("HistoryButton"));
    var actions = Assert.IsType<WrapPanel>(window.FindName("PrimaryActions"));
    var labels = LogicalTreeHelper.GetChildren(actions).OfType<Button>().Select(button => (string)button.Content).ToArray();
    Assert.Contains("_Settings", labels);
    Assert.DoesNotContain(labels, label => label.Contains("presets", StringComparison.OrdinalIgnoreCase) || label.Contains("updates", StringComparison.OrdinalIgnoreCase) || label.Contains("History", StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
  });

  [Fact]
  public void UpdateAvailableButtonIsHiddenUntilAnUpdateIsAvailableAndFollowsTheCheckResult() => DispatcherTestHost.Run(async _ =>
  {
    var backend = new ScriptedBackend();
    var window = new MainWindow(new JsonSettingsStore(_root), AppSettings.Defaults);
    window.UseUpdates(backend, new UpdatePreferencesStore(_root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0", @"C:\Apps\BookSplice");
    var button = Assert.IsType<Button>(window.FindName("UpdateAvailableButton"));
    Assert.Equal(Visibility.Collapsed, button.Visibility);
    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
    await window.Updates!.CheckAsync(manual: true);
    Assert.Equal(Visibility.Collapsed, button.Visibility);
    backend.Version = "v0.2.0";
    await window.Updates.CheckAsync(manual: true);
    Assert.Equal(Visibility.Visible, button.Visibility);
    Assert.Equal("BookSplice 0.2.0 a_vailable", button.Content);
    backend.Version = "v0.1.0";
    await window.Updates.CheckAsync(manual: true);
    Assert.Equal(Visibility.Collapsed, button.Visibility);
    window.Updates.Dispose();
  });

  [Fact]
  public void SaveStaysDisabledWhileAnUpdateIsPreparing() => DispatcherTestHost.Run(async dispatcher =>
  {
    var backend = new ScriptedBackend { Version = "v0.2.0" };
    var updates = new UpdatesViewModel(backend, new UpdatePreferencesStore(_root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0",
      @"C:\Apps\BookSplice", new GuiOperationGate(dispatcher), () => 0, () => { });
    var window = new FirstLaunchWindow(Model(), new SettingsPages(new PresetsViewModel(new PresetStore(Path.Combine(_root, PresetStore.FileName))), () => null, null, updates));
    var save = Assert.IsType<Button>(window.FindName("SaveButton"));
    Assert.True(save.IsEnabled);
    await updates.CheckAsync(manual: true);
    var install = updates.InstallAsync();
    Assert.Equal(UpdatesPhase.Preparing, updates.Phase);
    Assert.False(save.IsEnabled);
    backend.Install.SetResult(new BookSplice.Updates.Handover.UpdateOutcome(BookSplice.Updates.Handover.UpdateOutcomeKind.Cancelled));
    await install;
    Assert.True(save.IsEnabled);
    window.Close();
    updates.Dispose();
  });

  private sealed class ScriptedBackend : IUpdateBackend
  {
    public string Version { get; set; } = "v0.1.0";
    public TaskCompletionSource<BookSplice.Updates.Handover.UpdateOutcome> Install { get; } = new();
    public Task<BookSplice.Updates.Policy.CheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
      var uri = new Uri("https://github.com/nickwolf/booksplice/releases/tag/" + Version);
      var candidate = new BookSplice.Updates.Policy.ReleaseCandidate(1, Version, BookSplice.Updates.Policy.SemanticVersion.ParseTag(Version), uri, "Notes.", false, false, []);
      return Task.FromResult(new BookSplice.Updates.Policy.CheckResult(BookSplice.Updates.Policy.CheckKind.Success,
        new BookSplice.Updates.Policy.SelectionResult(BookSplice.Updates.Policy.SelectionKind.Available, candidate, true, null,
          new BookSplice.Updates.Policy.ReleaseIdentity(1, Version, uri, "Notes.", [])), null, null));
    }
    public Task<string> ReadManifestDigestAsync(BookSplice.Updates.Policy.ReleaseCandidate release, CancellationToken cancellationToken) => Task.FromResult("");
    public bool HasPrevious() => false;
    public Task<BookSplice.Updates.Handover.UpdateOutcome> InstallAsync(BookSplice.Updates.Policy.ReleaseCandidate release, BookSplice.Updates.Policy.ReleaseIdentity identity,
      string manifestSha256, Action shutdown, CancellationToken cancellationToken) => Install.Task;
    public Task<BookSplice.Updates.Handover.UpdateOutcome> RollbackAsync(Action shutdown, CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  [Fact]
  public void NoTwoControlsOnTheSamePageShareAnAccessKey() => DispatcherTestHost.Run(dispatcher =>
  {
    var setup = new FirstLaunchWindow(Model());
    AssertUniqueKeys(setup, Descendants<DependencyObject>(setup).Concat(FooterButtons(setup)));
    var window = Window(dispatcher);
    foreach (var section in window.Sections)
    {
      window.ShowSection(section);
      var tabs = Assert.Single(Descendants<TabControl>(window));
      var content = (DependencyObject)((TabItem)tabs.SelectedItem).Content;
      AssertUniqueKeys(window, Descendants<DependencyObject>(content).Append(content).Concat(FooterButtons(window)));
    }
    return Task.CompletedTask;
  });

  private static IEnumerable<Button> FooterButtons(FirstLaunchWindow window)
    => FooterNames.Select(name => (Button)window.FindName(name)).Where(button => button.Visibility == Visibility.Visible);

  private static void AssertUniqueKeys(FirstLaunchWindow window, IEnumerable<DependencyObject> elements)
  {
    var keys = elements.Distinct().Select(element => element switch
    {
      ContentControl { Content: string text } => text,
      HeaderedContentControl { Header: string header } => header,
      _ => null,
    }).Where(text => text is not null && text.Contains('_', StringComparison.Ordinal))
      .Select(text => char.ToUpperInvariant(text![text.IndexOf('_', StringComparison.Ordinal) + 1])).ToArray();
    Assert.NotEmpty(keys);
    Assert.Equal(keys.Length, keys.Distinct().Count());
  }

  private static void AssertAccessibleIcons(params InfoIcon[] icons)
  {
    foreach (var icon in icons)
    {
      Assert.False(string.IsNullOrWhiteSpace(icon.Text));
      Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(icon)));
      Assert.Equal(icon.Text, AutomationProperties.GetHelpText(icon));
      Assert.True(icon.Focusable && icon.IsTabStop);
    }
  }

  private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
  {
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
    {
      if (child is T match) yield return match;
      foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
  }
}
