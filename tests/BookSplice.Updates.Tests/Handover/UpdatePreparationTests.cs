using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

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
