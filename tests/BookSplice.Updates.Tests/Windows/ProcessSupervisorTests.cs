using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;
using System.Diagnostics;

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
    }, current.ImageIdentity, current.ImageSha256));
    Assert.Throws<IOException>(() => SupervisedProcess.CaptureExpected(actual with
    {
      ExecutablePath = HelperPath()
    }, current.ImageIdentity, current.ImageSha256));
    Assert.Throws<IOException>(() => SupervisedProcess.CaptureExpected(actual,
      current.ImageIdentity with { FileIdHex = new string('F', 32) }, current.ImageSha256));
    Assert.Throws<IOException>(() => SupervisedProcess.CaptureExpected(actual,
      current.ImageIdentity, new string('F', 64)));
  }

  [Fact]
  public async Task AcquisitionBoundaryRejectsAHandleFromAnotherProcess()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var expected = SupervisedProcess.CaptureCurrent();
    using var child = Process.Start(new ProcessStartInfo(HelperPath())
    {
      ArgumentList = { "hold" },
      UseShellExecute = false,
      CreateNoWindow = true
    })!;
    try
    {
      Assert.Throws<IOException>(() => ProcessSupervisor.ValidateExpectedHandle(child.SafeHandle,
        expected.Identity, expected.ImageIdentity, expected.ImageSha256));
    }
    finally
    {
      if (!child.HasExited) child.Kill();
      await child.WaitForExitAsync();
    }
  }

  [Fact]
  public async Task FailedTerminationRetainsTheExactUnverifiedChildHandle()
  {
    if (!OperatingSystem.IsWindows()) return;
    var process = Process.Start(new ProcessStartInfo(HelperPath())
    {
      ArgumentList = { "hold" },
      UseShellExecute = false,
      CreateNoWindow = true
    })!;
    var error = new IOException("Injected image verification failure.");
    var failure = Assert.Throws<UnverifiedChildException>(() =>
      ProcessSupervisor.ResolveFailedStart(process, error, _ => false));
    using var retained = failure.Child;
    Assert.Same(error, failure.InnerException);
    Assert.Equal(process.Id, retained.Pid);
    Assert.False(retained.HasExited);
    await retained.StopAndWaitAsync();
    Assert.True(retained.HasExited);
  }

  [Fact]
  public async Task CancelledStopDoesNotInitiateTermination()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var capability = VerifiedExecutable.PinFile(HelperPath());
    using var child = ProcessSupervisor.Start(capability, ["hold"],
      new Dictionary<string, string>());
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      child.StopAsync(cancellation.Token));
    Assert.False(child.HasExited);
    await child.StopAsync();
  }
}
