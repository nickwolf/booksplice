using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using BookSplice.Gui.Updates;
using BookSplice.Gui.Views;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class UpdatesPageTests
{
  [Fact]
  public void PageExposesNamedKeyboardControlsAndAPoliteStatusRegion() => DispatcherTestHost.Run(dispatcher =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-updates-window-").FullName;
    try
    {
      var model = new UpdatesViewModel(new IdleUpdateBackend(), new UpdatePreferencesStore(root), new UpdatePreferences(), "0.1.0",
        @"C:\Apps\BookSplice", new GuiOperationGate(dispatcher), () => 0, () => { });
      var window = new UpdatesPage(model);
      window.Measure(new Size(460, 420));
      window.Arrange(new Rect(0, 0, 460, 420));
      window.UpdateLayout();
      var buttons = Descendants<Button>(window).ToArray();
      Assert.NotEmpty(buttons);
      Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetName())));
      Assert.All(buttons, button => Assert.True(button.Focusable && button.IsTabStop));
      var status = Assert.IsType<TextBlock>(window.FindName("Status"));
      Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
      Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(Assert.IsType<CheckBox>(window.FindName("Automatic")))));
      Assert.All(Descendants<TextBox>(window), input => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(input))));
      Assert.False(model.CanInstall);
      model.Dispose();
    }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
  });

  [Fact]
  public void UpToDatePageShowsTheInstalledNotesUnderAWhatsNewHeadingAndDevBuildsShowNone() => DispatcherTestHost.Run(async dispatcher =>
  {
    var root = Directory.CreateTempSubdirectory("booksplice-updates-notes-").FullName;
    try
    {
      var backend = new NotesBackend();
      var model = new UpdatesViewModel(backend, new UpdatePreferencesStore(root), new UpdatePreferences(AutomaticChecksEnabled: false), "0.1.0",
        @"C:\Apps\BookSplice", new GuiOperationGate(dispatcher), () => 0, () => { });
      var page = new UpdatesPage(model);
      page.Measure(new Size(600, 600));
      page.Arrange(new Rect(0, 0, 600, 600));
      var heading = Assert.IsType<TextBlock>(page.FindName("NotesHeading"));
      await model.CheckAsync(manual: true);
      page.UpdateLayout();
      Assert.Equal(Visibility.Visible, ((UIElement)heading.Parent).Visibility);
      Assert.Equal("What's new in 0.1.0", heading.Text);
      Assert.Equal("BookSplice 0.1.0 is up to date.", model.Message);
      Assert.Contains(Descendants<Button>(page), button => Equals(button.Content, "Open release _page") && button.IsEnabled);
      Assert.Equal(Visibility.Collapsed, ((UIElement)((FrameworkElement)Assert.IsType<Button>(page.FindName("Install")).Parent).Parent).Visibility);
      backend.Version = "v0.0.9";
      await model.CheckAsync(manual: true);
      page.UpdateLayout();
      Assert.Equal(Visibility.Collapsed, ((UIElement)heading.Parent).Visibility);
      model.Dispose();
    }
    finally { Directory.Delete(root, true); }
  });

  private sealed class NotesBackend : IUpdateBackend
  {
    public string Version { get; set; } = "v0.1.0";
    public Task<CheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
      var uri = new Uri("https://github.com/nickwolf/booksplice/releases/tag/" + Version);
      var candidate = new ReleaseCandidate(1, Version, SemanticVersion.ParseTag(Version), uri, "Installed notes.", false, false, []);
      return Task.FromResult(new CheckResult(CheckKind.Success, new SelectionResult(SelectionKind.Available, candidate, true, null,
        new ReleaseIdentity(1, Version, uri, "Installed notes.", [])), null, null));
    }
    public Task<string> ReadManifestDigestAsync(ReleaseCandidate release, CancellationToken cancellationToken) => Task.FromResult("");
    public bool HasPrevious() => false;
    public Task<UpdateOutcome> InstallAsync(ReleaseCandidate release, ReleaseIdentity identity, string manifestSha256, Action shutdown, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<UpdateOutcome> RollbackAsync(Action shutdown, CancellationToken cancellationToken) => throw new NotSupportedException();
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
