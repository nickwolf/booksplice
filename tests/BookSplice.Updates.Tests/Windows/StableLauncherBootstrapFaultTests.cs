using BookSplice.Updates.Storage;
using BookSplice.Updates.Tests.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

[Collection("WindowsOwnedStorage")]
public sealed class StableLauncherBootstrapFaultTests
{
  [Fact]
  public void ReplacementInCopyToRecordGapIsNeverAdopted()
  {
    using var fixture = new Fixture();
    Assert.Throws<InvalidDataException>(() => fixture.Ensure(point =>
    {
      if (point != StableLauncherBootstrap.BootstrapPoint.AfterCopy) return;
      File.Delete(fixture.Layout.StableLauncherPath);
      File.Copy(Path.Combine(fixture.Layout.OriginalApplicationPath,
        "BookSplice.Launcher.exe"), fixture.Layout.StableLauncherPath);
    }));
    Assert.Null(fixture.State.Read<InstallationRecord>(fixture.RecordPath)
      .Value!.StableLauncherIdentity);
    Assert.Throws<IOException>(() => fixture.Ensure());
  }

  [Fact]
  public void FailedRecordLeavesAnUnadoptedOrphan()
  {
    using var fixture = new Fixture();
    var failing = new AtomicStateStore(fixture.Layout.Records,
      [InstallationRecord.Definition("installation.json", fixture.Layout.InstallationId)],
      point =>
      {
        if (point == StateWritePoint.BeforeReplace)
          throw new IOException("Injected durable record failure.");
      });
    Assert.Throws<IOException>(() => StableLauncherBootstrap.Ensure(fixture.Layout,
      fixture.Snapshot(), failing, fixture.RecordPath));
    Assert.True(File.Exists(fixture.Layout.StableLauncherPath));
    Assert.Null(fixture.State.Read<InstallationRecord>(fixture.RecordPath)
      .Value!.StableLauncherIdentity);
    Assert.Throws<IOException>(() => fixture.Ensure());
  }

  private sealed class Fixture : IDisposable
  {
    private readonly string _root;
    private readonly CurrentTarget _current;
    private readonly OwnershipState _ownership;

    internal Fixture()
    {
      _root = Path.Combine(Path.GetTempPath(), "booksplice-bootstrap-fault-" +
        Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(_root);
      var id = Guid.NewGuid();
      var original = Path.Combine(_root, "BookSplice");
      var data = Path.Combine(_root, "data");
      Directory.CreateDirectory(data);
      OriginalPackageFixture.Write(original);
      var store = new OwnedDirectoryStore(_root, _ => { });
      var managed = store.CreateNamedExclusive(_root, ".booksplice-managed-" + id,
        "managed-root", id, Guid.NewGuid());
      var versions = store.CreateNamedExclusive(managed, "versions", "versions",
        id, Guid.NewGuid());
      var staging = store.CreateNamedExclusive(managed, "staging", "staging",
        id, Guid.NewGuid());
      var trash = store.CreateNamedExclusive(managed, "trash", "trash",
        id, Guid.NewGuid());
      var records = store.CreateNamedExclusive(managed, "records", "records",
        id, Guid.NewGuid());
      Layout = new InstallationLayout(original, data, managed, records, versions, staging,
        trash, store);
      var installation = InstallationRecord.FromLayout(Layout, "1.0.0");
      _current = new CurrentTarget(1, id,
        new OwnedOrOriginalTarget(TargetKind.Original, installation.OriginalExecutablePath,
          installation.OriginalIdentity, installation.OriginalExecutableIdentity,
          installation.OriginalPackage, installation.OriginalVersion,
          installation.OriginalProtocolVersion, null), null);
      _ownership = new OwnershipState(1, id, records.Identity,
        new[] { managed, records, versions, staging, trash }.Select(OwnershipEntry.FromCreated).ToArray());
      RecordPath = Path.Combine(records.Path, "installation.json");
      State = new AtomicStateStore(records,
        [InstallationRecord.Definition("installation.json", id)]);
      State.Write(RecordPath, installation);
    }

    internal InstallationLayout Layout { get; }
    internal AtomicStateStore State { get; }
    internal string RecordPath { get; }

    internal JournalSnapshot Snapshot() => new(
      State.Read<InstallationRecord>(RecordPath),
      new StateRead<CurrentTarget>(StateReadKind.Valid, _current),
      new StateRead<UpdateTransaction>(StateReadKind.Missing),
      new StateRead<OwnershipState>(StateReadKind.Valid, _ownership));

    internal InstallationRecord Ensure(Action<StableLauncherBootstrap.BootstrapPoint>? fault = null) =>
      StableLauncherBootstrap.Ensure(Layout, Snapshot(), State, RecordPath, fault);

    public void Dispose() => Directory.Delete(_root, true);
  }
}
