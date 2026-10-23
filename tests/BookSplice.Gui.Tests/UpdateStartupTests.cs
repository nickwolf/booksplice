using BookSplice.Gui.Updates;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Gui.Tests;

public sealed class UpdateStartupTests
{
  private static readonly LaunchedImage Original = new(@"C:\Apps\BookSplice\BookSplice.Gui.exe",
    new FileIdentity(1, "11111111111111111111111111111111"));
  private static readonly LaunchedImage Managed = new(@"C:\Apps\.m\versions\0.2.0-x\BookSplice.Gui.exe",
    new FileIdentity(1, "22222222222222222222222222222222"));

  [Fact]
  public void HeldLeaseShowsMessageAndWritesNothing()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-lease-" + Guid.NewGuid().ToString("N"), "BookSplice");
    try
    {
      using var first = GuiStartup.AcquireLease(root, _ => Assert.Fail("first start must not notify"));
      Assert.NotNull(first);
      string? shown = null;
      Assert.Null(GuiStartup.AcquireLease(root, message => shown = message));
      Assert.Equal("BookSplice is already running.", shown);
      Assert.Equal(["gui-instance.lock"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
    }
    finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
  }

  [Fact]
  public void NoMappingOrMatchingMappingRunsNormally()
  {
    var starts = 0;
    Assert.False(GuiStartup.TryForward(Original, () => null, () => starts++));
    Assert.False(GuiStartup.TryForward(Original, () => Original, () => starts++));
    Assert.Equal(0, starts);
  }

  [Fact]
  public void DifferentMappingStartsTheLauncherOnceAndTheMappedImageDoesNotForwardAgain()
  {
    var starts = 0;
    Assert.True(GuiStartup.TryForward(Original, () => Managed, () => starts++));
    Assert.Equal(1, starts);
    Assert.False(GuiStartup.TryForward(Managed, () => Managed, () => starts++));
    Assert.Equal(1, starts);
  }

  [Fact]
  public void RegistryOrLauncherFailuresRunNormally()
  {
    Assert.False(GuiStartup.TryForward(Original, () => throw new InvalidDataException("bad"), () => Assert.Fail("no launch")));
    Assert.False(GuiStartup.TryForward(Original, () => Managed, () => throw new IOException("gone")));
  }

  [Fact]
  public void PreparedUpdateStartsLauncherHoldsAdmissionThenShutsDown()
  {
    var admission = new Admission();
    IDisposable? held = null;
    var order = new List<string>();
    var outcome = UpdateComposition.HandOff(Prepared(admission), () => order.Add("start"),
      () => order.Add("failed"), value => { held = value; order.Add("hold"); }, () => order.Add("shutdown"));
    Assert.Equal(UpdateOutcomeKind.Prepared, outcome.Kind);
    Assert.Equal(["start", "hold", "shutdown"], order);
    Assert.Same(admission, held);
    Assert.False(admission.Disposed);
  }

  [Fact]
  public void LauncherStartFailureMarksFailedReleasesAdmissionAndKeepsRunning()
  {
    var admission = new Admission();
    var marked = 0;
    var outcome = UpdateComposition.HandOff(Prepared(admission), () => throw new IOException("no launcher"),
      () => marked++, _ => Assert.Fail("admission must not be held"), () => Assert.Fail("must not shut down"));
    Assert.Equal(UpdateOutcomeKind.Failed, outcome.Kind);
    Assert.Contains("no launcher", outcome.Reason);
    Assert.Equal(1, marked);
    Assert.True(admission.Disposed);
  }

  [Fact]
  public void UnpreparedOutcomesDoNothing()
  {
    var deferred = new PreparedUpdate(new UpdateOutcome(UpdateOutcomeKind.Deferred, "busy"));
    var outcome = UpdateComposition.HandOff(deferred, () => Assert.Fail("no start"),
      () => Assert.Fail("no fail"), _ => Assert.Fail("no hold"), () => Assert.Fail("no shutdown"));
    Assert.Equal(UpdateOutcomeKind.Deferred, outcome.Kind);
  }

  private static PreparedUpdate Prepared(Admission admission) =>
    new(new UpdateOutcome(UpdateOutcomeKind.Prepared), Guid.NewGuid(), admission);

  private sealed class Admission : IDisposable
  {
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
  }
}
