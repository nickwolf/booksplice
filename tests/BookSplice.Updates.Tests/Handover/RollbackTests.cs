using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

[Collection("WindowsOwnedStorage")]
public sealed class RollbackTests
{
  [Fact]
  public async Task RollbackSwapsToOriginalAndBackToManagedPrevious()
  {
    using var h = new HandoverHarness();
    var settings = File.ReadAllBytes(h.SettingsPath);
    var updated = await h.RunAsync(h.Handover(), await h.PrepareUpdateAsync(),
      await h.ExitedOldAsync());
    Assert.True(updated.Kind == UpdateOutcomeKind.Committed, updated.Reason);
    var managed = h.Mapping!.Current;
    await h.StopCandidatesAsync();

    foreach (var (from, to) in new[] { (managed, h.OldTarget), (h.OldTarget, managed) })
    {
      File.Delete(h.MarkerPath);
      var id = await h.PrepareRollbackAsync();
      var prepared = h.Transaction!;
      Assert.Equal(UpdatePhase.Prepared, prepared.Phase);
      Assert.Empty(prepared.OwnedPaths);
      Assert.True(LaunchResolver.SameTarget(from, prepared.OldTarget));
      Assert.True(LaunchResolver.SameTarget(to, prepared.NewTarget));
      var markerBeforeCommit = true;
      var outcome = await h.RunAsync(h.Handover(
        afterReady: () => markerBeforeCommit = File.Exists(h.MarkerPath)), id,
        h.Candidates.Last());

      Assert.True(outcome.Kind == UpdateOutcomeKind.Committed, outcome.Reason);
      Assert.False(markerBeforeCommit);
      await h.WaitForMarkerAsync();
      Assert.True(LaunchResolver.SameTarget(to, h.Mapping!.Current));
      Assert.True(LaunchResolver.SameTarget(from, h.Mapping.Previous));
      Assert.Equal(UpdatePhase.Committed, h.Transaction!.Phase);
      Assert.Equal(settings, File.ReadAllBytes(h.SettingsPath));
      var resolved = LaunchResolver.Resolve(h.Layout,
        JournalSnapshot.Read(h.State, h.Layout.Records.Path));
      Assert.True(LaunchResolver.SameTarget(to, resolved.Target), resolved.Reason);
      await h.StopCandidatesAsync();
    }
    Assert.Empty(h.FallbackLaunches);
    Assert.Equal(h.Admission.Entered, h.Admission.Released);
  }

  [Fact]
  public async Task RollbackRefusesWithoutAValidOwnedPreviousAndMutatesNothing()
  {
    using var h = new HandoverHarness();
    var identity = new FileIdentity(1, "22222222222222222222222222222222");
    var owned = new OwnedDirectory(Path.Combine(h.Layout.Versions.Path, "9.9.9-unowned"),
      identity, h.Layout.InstallationId, Guid.NewGuid(), "version");
    var unowned = new OwnedOrOriginalTarget(TargetKind.Managed,
      Path.Combine(owned.Path, "BookSplice.Gui.exe"), identity, identity,
      PersistedPackage.FromSnapshot(new PackageSnapshot(identity, [],
        [new PackageFile("BookSplice.Gui.exe", 1, new string('a', 64), identity)])),
      "9.9.9", 1, owned);
    var mappings = new[]
    {
      new CurrentTarget(1, h.Layout.InstallationId, h.OldTarget, null),
      new CurrentTarget(1, h.Layout.InstallationId, h.OldTarget, unowned),
      new CurrentTarget(1, h.Layout.InstallationId, unowned, h.OldTarget with { Package = null })
    };
    foreach (var mapping in mappings)
    {
      h.State.Write(h.MappingPath, mapping);
      var before = File.ReadAllBytes(h.MappingPath);
      var outcome = await h.Preparation().PrepareRollbackAsync(h.Layout, CancellationToken.None);
      Assert.Equal(UpdateOutcomeKind.Refused, outcome.Outcome.Kind);
      Assert.Contains(h.Layout.OriginalApplicationPath, outcome.Outcome.Reason);
      Assert.Null(outcome.Admission);
      Assert.Equal(StateReadKind.Missing, h.Journal.Read().Kind);
      Assert.Equal(before, File.ReadAllBytes(h.MappingPath));
      Assert.Equal(h.Admission.Entered, h.Admission.Released);
    }
    var deferred = await h.Preparation(new TrackingLeaseStore(() => true))
      .PrepareRollbackAsync(h.Layout, CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Deferred, deferred.Outcome.Kind);
  }
}
