using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

public sealed class ProcessSupervisorTests
{
  internal static string HelperPath()
  {
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "BookSplice.slnx")))
      root = root.Parent;
    Assert.NotNull(root);
    return Path.Combine(root.FullName, "tests", "BookSplice.LeaseHost", "bin", "Release",
      "net10.0-windows", "BookSplice.LeaseHost.exe");
  }

  [Fact]
  public async Task StartsExactApphostAndStopsRetainedProcess()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var capability = VerifiedExecutable.PinFile(HelperPath());
    var dataRoot = Path.Combine(Path.GetTempPath(), "BookSpliceSupervisor", Guid.NewGuid().ToString("N"));
    using var child = ProcessSupervisor.Start(capability, [dataRoot, "gui"],
      new Dictionary<string, string>());
    Assert.Equal(capability.Path, child.Identity.ExecutablePath, ignoreCase: true);
    Assert.True(child.Identity.StartTimeUtcTicks > 0);
    child.VerifyPeer(child.Identity.Pid);
    Assert.Throws<IOException>(() => child.VerifyPeer(child.Identity.Pid + 1));
    await child.StopAsync();
    Assert.True(child.HasExited);
  }

  [Fact]
  public void RejectsWrongStartTimeAndImageForRetainedExpectedProcess()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var current = SupervisedProcess.CaptureCurrent();
    var actual = current.Identity;
    Assert.Throws<IOException>(() => SupervisedProcess.CaptureExpected(actual with
    {
      StartTimeUtcTicks = actual.StartTimeUtcTicks + 1
    }));
    Assert.Throws<IOException>(() => SupervisedProcess.CaptureExpected(actual with
    {
      ExecutablePath = HelperPath()
    }));
  }
}
