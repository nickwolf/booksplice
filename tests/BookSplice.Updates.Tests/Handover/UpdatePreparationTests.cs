using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

[Collection("WindowsOwnedStorage")]
public sealed class UpdatePreparationTests
{
  private static readonly FileIdentity Identity = new(1, "0123456789ABCDEF0123456789ABCDEF");

  [Fact]
  public void ConfirmationRequiresExactReleaseIdentityAndForwardVersion()
  {
    var release = Candidate("v1.2.3");
    var identity = ReleaseSelector.Select([release]).Identity!;
    var old = new OwnedOrOriginalTarget(TargetKind.Original,
      @"C:\BookSplice\BookSplice.Gui.exe", Identity, Identity, null, "1.2.2", 1, null);
    var confirmation = new ConfirmedUpdate(release, identity, new string('a', 64),
      old, Guid.NewGuid());
    Assert.Null(UpdatePreparation.CheckConfirmation(confirmation));
    Assert.Equal(UpdateOutcomeKind.Deferred,
      UpdatePreparation.CheckConfirmation(confirmation with
      {
        ReleaseIdentity = new ReleaseIdentity(identity.Id, identity.Tag, identity.PageUri,
          "changed notes", identity.Assets)
      })!.Kind);
    Assert.Equal(UpdateOutcomeKind.Deferred,
      UpdatePreparation.CheckConfirmation(confirmation with
      {
        OldTarget = old with { Version = "1.2.3" }
      })!.Kind);
    Assert.Equal(UpdateOutcomeKind.Deferred,
      UpdatePreparation.CheckConfirmation(confirmation with
      {
        OldTarget = old with { Version = "1.3.0" }
      })!.Kind);
  }

  [Fact]
  public async Task PreparationFailuresBeforePreparedNeverChangeTheMapping()
  {
    using var h = new HandoverHarness();
    var mapping = File.ReadAllBytes(h.MappingPath);

    var deferred = await h.Preparation(new TrackingLeaseStore(() => true))
      .PrepareUpdateAsync(h.Update(), h.Layout, CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Deferred, deferred.Outcome.Kind);
    Assert.Equal(StateReadKind.Missing, h.Journal.Read().Kind);

    var changed = await h.Preparation().PrepareUpdateAsync(h.Update() with
    {
      ManifestSha256 = new string('b', 64)
    }, h.Layout, CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Deferred, changed.Outcome.Kind);
    Assert.Equal(StateReadKind.Missing, h.Journal.Read().Kind);

    using var cancellation = new CancellationTokenSource();
    var cancelled = await h.Preparation(reader: new FixedManifestReader(h.Fixture.Manifest,
      cancellation.Cancel)).PrepareUpdateAsync(h.Update(), h.Layout, cancellation.Token);
    Assert.Equal(UpdateOutcomeKind.Cancelled, cancelled.Outcome.Kind);
    Assert.Null(cancelled.Admission);
    Assert.Equal(UpdatePhase.Failed, h.Transaction!.Phase);

    File.WriteAllText(h.SettingsPath, "{\"schemaVersion\":1,\"changed\":true}");
    var settings = await h.Preparation(reader: new FixedManifestReader(h.Fixture.Manifest,
      () => File.WriteAllText(h.SettingsPath, "{\"schemaVersion\":1}")))
      .PrepareUpdateAsync(h.Update(), h.Layout, CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Failed, settings.Outcome.Kind);
    Assert.Equal(UpdatePhase.Failed, h.Transaction!.Phase);
    Assert.Equal(mapping, File.ReadAllBytes(h.MappingPath));
    Assert.Equal(h.Admission.Entered, h.Admission.Released);
  }

  [Fact]
  public async Task PreparationRequiresTheRecordedStableLauncher()
  {
    using var h = new HandoverHarness(installLauncher: false);
    var update = await h.Preparation().PrepareUpdateAsync(h.Update(), h.Layout,
      CancellationToken.None);
    var rollback = await h.Preparation().PrepareRollbackAsync(h.Layout, CancellationToken.None);
    Assert.Equal(UpdateOutcomeKind.Failed, update.Outcome.Kind);
    Assert.Equal(UpdateOutcomeKind.Failed, rollback.Outcome.Kind);
    Assert.Equal(StateReadKind.Missing, h.Journal.Read().Kind);
    Assert.Equal(h.Admission.Entered, h.Admission.Released);
  }

  private static ReleaseCandidate Candidate(string tag)
  {
    var version = tag[1..];
    var prefix = $"BookSplice-{version}-win-x64";
    var names = new[] { prefix + ".zip", prefix + ".zip.sha256",
      prefix + ".update.json", prefix + ".update.sig.json" };
    var assets = names.Select((name, index) => new ReleaseAsset(index + 1, name,
      new Uri($"https://github.com/nickwolf/booksplice/releases/download/{tag}/{name}"), 100))
      .ToArray();
    return new ReleaseCandidate(7, tag, SemanticVersion.ParseTag(tag),
      new Uri($"https://github.com/nickwolf/booksplice/releases/tag/{tag}"),
      "notes", false, false, assets);
  }
}
