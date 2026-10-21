using System.Diagnostics;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Tests.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class UpdateCleanupTests : IDisposable
{
  private static readonly FileIdentity Fake = new(1, "0123456789ABCDEF0123456789ABCDEF");
  private readonly PackageStagerTests.Fixture _fixture = new();
  private readonly AtomicStateStore _state;
  private readonly UpdateJournal _journal;
  private readonly OwnedDirectoryStore _creator;
  private readonly string _ownershipPath;

  public UpdateCleanupTests()
  {
    (_, _journal, _state, _ownershipPath) = _fixture.CreateDurableStager(beginJournal: false);
    var recorder = new DurableOwnershipRecorder(_state, _ownershipPath, Layout.Records,
      Layout.ManagedRoot);
    _creator = new OwnedDirectoryStore(Path.GetDirectoryName(Layout.OriginalApplicationPath)!,
      recorder.Record);
    SetMapping(null, null);
    WriteJournal(UpdatePhase.Committed);
  }

  private InstallationLayout Layout => _fixture.Layout;
  private string CurrentPath => Path.Combine(Layout.Records.Path, "current.json");
  private string TrashPath => Path.Combine(Layout.Records.Path, "trash.json");
  private string JournalPath => Path.Combine(Layout.Records.Path, "journal.json");

  public void Dispose() => _fixture.Dispose();

  private OwnedDirectory AddDirectory(string kind = "version", OwnedDirectory? parent = null,
    string? name = null)
  {
    var leaf = name ?? "0.3.0-" + Guid.NewGuid().ToString("N");
    var directory = _creator.CreateNamedExclusive(parent ?? Layout.Versions, leaf, kind,
      Layout.InstallationId, Guid.NewGuid());
    Directory.CreateDirectory(Path.Combine(directory.Path, "tools"));
    File.WriteAllText(Path.Combine(directory.Path, "BookSplice.Gui.exe"), "gui");
    File.WriteAllText(Path.Combine(directory.Path, "tools", "ffmpeg.exe"), "ffmpeg");
    return directory;
  }

  private static OwnedOrOriginalTarget? Managed(OwnedDirectory? directory) => directory is null ? null :
    new(TargetKind.Managed, Path.Combine(directory.Path, "BookSplice.Gui.exe"), directory.Identity,
      Fake, PersistedPackage.FromSnapshot(new PackageSnapshot(directory.Identity, [],
        [new PackageFile("BookSplice.Gui.exe", 1, new string('a', 64), Fake)])),
      "0.3.0", 1, directory);

  private OwnedOrOriginalTarget Original() => new(TargetKind.Original,
    Path.Combine(Layout.OriginalApplicationPath, "BookSplice.Gui.exe"), Layout.OriginalIdentity,
    Layout.OriginalIdentity, null, "0.1.0", 1, null);

  private void SetMapping(OwnedDirectory? current, OwnedDirectory? previous) =>
    _state.Write(CurrentPath, new CurrentTarget(1, Layout.InstallationId,
      Managed(current) ?? Original(), Managed(previous)));

  private void WriteJournal(UpdatePhase phase)
  {
    var old = Original();
    var mapping = new CurrentTarget(1, Layout.InstallationId, old, null);
    var target = phase is UpdatePhase.Preparing or UpdatePhase.Failed ? null : old with { Version = "9.9.9" };
    _state.Write(JournalPath, new UpdateTransaction(1, Layout.InstallationId, Guid.NewGuid(), phase,
      old, target, null, true, 1, new string('A', 64), [], null, null, mapping));
  }

  private UpdateCleanup Cleanup(IActivityLeaseStore? activity = null, string? trashName = null) =>
    new(_state, _journal, _creator, activity ?? new NoOpActivityLeaseStore())
    {
      TrashName = trashName is null ? () => "t-" + Guid.NewGuid().ToString("N") : () => trashName
    };

  private void WriteIntent(OwnedDirectory source, string trashPath) =>
    _state.Write(TrashPath, new TrashJournal(1, Layout.InstallationId,
      [new TrashIntent(Guid.NewGuid(), source, trashPath)]));

  private int IntentCount() => _state.Read<TrashJournal>(TrashPath).Value?.Items.Count ?? 0;

  private bool Recorded(OwnedDirectory directory) => _state.Read<OwnershipState>(_ownershipPath)
    .Value!.Entries.Any(entry => entry.Path == directory.Path);

  private static void Mklink(string link, string target)
  {
    using var process = Process.Start(new ProcessStartInfo("cmd.exe")
    {
      ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
      UseShellExecute = false,
      CreateNoWindow = true
    })!;
    process.WaitForExit();
    Assert.Equal(0, process.ExitCode);
  }

  [Fact]
  public void RemovesOnlyUnprotectedOwnedDirectoriesAndKeepsEverythingElse()
  {
    var current = AddDirectory();
    var previous = AddDirectory();
    var old = AddDirectory();
    var stage = AddDirectory("stage", Layout.Staging, Guid.NewGuid().ToString("D"));
    var payload = AddDirectory("payload", stage, "payload");
    var lookalike = Directory.CreateDirectory(Path.Combine(
      Path.GetDirectoryName(Layout.OriginalApplicationPath)!, "0.0.1-" + Guid.NewGuid().ToString("N")));
    var unowned = Directory.CreateDirectory(Path.Combine(Layout.Versions.Path, "9.9.9-unowned"));
    SetMapping(current, previous);

    var report = Cleanup().Run(Layout);

    Assert.Equal(new[] { old.Path, stage.Path }.Order(), report.Removed.Order());
    Assert.Empty(report.Refused);
    Assert.True(Directory.Exists(current.Path) && Directory.Exists(previous.Path));
    Assert.True(Directory.Exists(Layout.OriginalApplicationPath) && lookalike.Exists && unowned.Exists);
    Assert.False(Directory.Exists(old.Path) || Directory.Exists(stage.Path));
    Assert.Empty(Directory.GetFileSystemEntries(Layout.Trash.Path));
    Assert.True(Recorded(current) && Recorded(previous) && !Recorded(old) && !Recorded(stage) && !Recorded(payload));
    Assert.Equal(0, IntentCount());
  }

  [Fact]
  public void NothingIsRemovedWhileATransactionIsUnfinished()
  {
    var old = AddDirectory();
    WriteJournal(UpdatePhase.Prepared);

    var report = Cleanup().Run(Layout);

    Assert.Empty(report.Removed);
    Assert.True(Directory.Exists(old.Path) && Recorded(old));
  }

  [Fact]
  public void CleanupIsSkippedWhileAdmissionIsHeld()
  {
    var old = AddDirectory();
    var leases = new ActivityLeaseStore(Layout.DataRoot);
    using (leases.TryEnterJob())
    {
      var report = Cleanup(leases).Run(Layout);
      Assert.True(report.Skipped);
    }
    Assert.True(Directory.Exists(old.Path));
  }

  [Theory]
  [InlineData("ownership.json")]
  [InlineData("journal.json")]
  [InlineData("trash.json")]
  [InlineData("current.json")]
  public void CorruptRecordsRefuseEveryAction(string file)
  {
    var old = AddDirectory();
    File.WriteAllText(Path.Combine(Layout.Records.Path, file), "{ not json");

    var report = Cleanup().Run(Layout);

    Assert.Empty(report.Removed);
    Assert.NotEmpty(report.Refused);
    Assert.True(Directory.Exists(old.Path));
  }

  [Fact]
  public void ReplacementAtTheExactRecordedTrashPathSurvives()
  {
    var source = AddDirectory();
    var trashPath = Path.Combine(Layout.Trash.Path, "t-" + Guid.NewGuid().ToString("N"));
    Directory.Move(source.Path, trashPath);
    Directory.CreateDirectory(source.Path);
    Directory.Delete(trashPath, true);
    Directory.CreateDirectory(trashPath);
    File.WriteAllText(Path.Combine(trashPath, "replacement.txt"), "keep");
    WriteIntent(source, trashPath);

    var report = Cleanup().Run(Layout);

    Assert.Contains(report.Refused, item => item.Path == trashPath);
    Assert.Equal("keep", File.ReadAllText(Path.Combine(trashPath, "replacement.txt")));
    Assert.Equal(1, IntentCount());
  }

  [Fact]
  public void StaleIdentityIsRefusedAndTheReplacementSurvives()
  {
    var old = AddDirectory();
    Directory.Delete(old.Path, true);
    Directory.CreateDirectory(old.Path);
    File.WriteAllText(Path.Combine(old.Path, "replacement.txt"), "keep");

    var report = Cleanup().Run(Layout);

    Assert.Contains(report.Refused, item => item.Path == old.Path);
    Assert.Equal("keep", File.ReadAllText(Path.Combine(old.Path, "replacement.txt")));
    Assert.Equal(0, IntentCount());
  }

  [Fact]
  public void JunctionAtAnOwnedPathIsRefusedAndItsTargetSurvives()
  {
    var old = AddDirectory();
    var outside = Directory.CreateDirectory(Path.Combine(Layout.DataRoot, "outside-" + Guid.NewGuid().ToString("N")));
    File.WriteAllText(Path.Combine(outside.FullName, "keep.txt"), "keep");
    Directory.Delete(old.Path, true);
    Mklink(old.Path, outside.FullName);

    var report = Cleanup().Run(Layout);

    Assert.Contains(report.Refused, item => item.Path == old.Path);
    Assert.Equal("keep", File.ReadAllText(Path.Combine(outside.FullName, "keep.txt")));
    Directory.Delete(old.Path);
  }

  [Fact]
  public void JunctionInsideATrashedDirectoryIsUnlinkedWithoutFollowingIt()
  {
    var old = AddDirectory();
    var outside = Directory.CreateDirectory(Path.Combine(Layout.DataRoot, "outside-" + Guid.NewGuid().ToString("N")));
    File.WriteAllText(Path.Combine(outside.FullName, "keep.txt"), "keep");
    Mklink(Path.Combine(old.Path, "link"), outside.FullName);

    var report = Cleanup().Run(Layout);

    Assert.Equal([old.Path], report.Removed);
    Assert.Equal("keep", File.ReadAllText(Path.Combine(outside.FullName, "keep.txt")));
  }

  [Fact]
  public void CollisionAtTheTrashDestinationIsNeverReplaced()
  {
    var old = AddDirectory();
    var collision = Directory.CreateDirectory(Path.Combine(Layout.Trash.Path, "t-fixed"));
    File.WriteAllText(Path.Combine(collision.FullName, "keep.txt"), "keep");

    var report = Cleanup(trashName: "t-fixed").Run(Layout);

    Assert.Empty(report.Removed);
    Assert.Contains(report.Deferred, item => item.Path == old.Path);
    Assert.True(Directory.Exists(old.Path) && Recorded(old));
    Assert.Equal("keep", File.ReadAllText(Path.Combine(collision.FullName, "keep.txt")));
    Assert.Equal(0, IntentCount());
  }

  [Fact]
  public void InUseFileDefersAndALaterRunFinishes()
  {
    var old = AddDirectory();
    var held = Path.Combine(old.Path, "tools", "ffmpeg.exe");
    using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
      var report = Cleanup().Run(Layout);
      Assert.Empty(report.Removed);
      Assert.Contains(report.Deferred, item => item.Path == old.Path);
      Assert.True(Recorded(old));
    }

    var retry = Cleanup().Run(Layout);

    Assert.Equal([old.Path], retry.Removed);
    Assert.False(Directory.Exists(old.Path));
    Assert.False(Recorded(old));
    Assert.Equal(0, IntentCount());
  }

  [Fact]
  public void PartialDeletionResumesFromTheRecordedIntent()
  {
    var old = AddDirectory();
    var trashPath = Path.Combine(Layout.Trash.Path, "t-" + Guid.NewGuid().ToString("N"));
    Directory.Move(old.Path, trashPath);
    WriteIntent(old, trashPath);
    var held = Path.Combine(trashPath, "tools", "ffmpeg.exe");
    using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
      var report = Cleanup().Run(Layout);
      Assert.Contains(report.Deferred, item => item.Path == trashPath);
      Assert.False(File.Exists(Path.Combine(trashPath, "BookSplice.Gui.exe")));
      Assert.Equal(1, IntentCount());
      Assert.True(Recorded(old));
    }

    var retry = Cleanup().Run(Layout);

    Assert.Equal([old.Path], retry.Removed);
    Assert.False(Directory.Exists(trashPath));
    Assert.False(Recorded(old));
    Assert.Equal(0, IntentCount());
  }

  [Fact]
  public void IntentWithoutARenameIsDroppedAndTheSourceIsReevaluated()
  {
    var current = AddDirectory();
    SetMapping(current, null);
    WriteIntent(current, Path.Combine(Layout.Trash.Path, "t-" + Guid.NewGuid().ToString("N")));

    var report = Cleanup().Run(Layout);

    Assert.Empty(report.Removed);
    Assert.True(Directory.Exists(current.Path) && Recorded(current));
    Assert.Equal(0, IntentCount());
  }

  [Fact]
  public void FabricatedIntentForAnUnrecordedDirectoryIsRefused()
  {
    var old = AddDirectory();
    var trashPath = Path.Combine(Layout.Trash.Path, "t-" + Guid.NewGuid().ToString("N"));
    Directory.Move(old.Path, trashPath);
    WriteIntent(old with { Kind = "stage" }, trashPath);

    var report = Cleanup().Run(Layout);

    Assert.Contains(report.Refused, item => item.Path == trashPath);
    Assert.True(Directory.Exists(trashPath));
  }
}
