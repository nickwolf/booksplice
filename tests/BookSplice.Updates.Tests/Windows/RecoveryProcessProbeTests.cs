using System.ComponentModel;
using System.Diagnostics;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

public sealed class RecoveryProcessProbeTests
{
  [Fact]
  public async Task AliveObservationOwnsVerifiedCapabilityAndDisposeDoesNotStopProcess()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var child = StartHelper();
    var expected = Expected(child);
    var probe = new RecoveryProcessProbe();

    var observation = probe.Observe(expected.Identity, expected.Image, expected.Sha256);
    Assert.Equal(ProcessLife.Alive, observation.Life);
    Assert.NotNull(observation.Process);
    observation.Process.VerifyPeer(child.Id);
    observation.Dispose();
    Assert.False(child.HasExited);
    await StopOwnedChild(child);
  }

  [Fact]
  public async Task MissingRecordedProcessIsExited()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var child = StartHelper();
    var expected = Expected(child);
    child.Kill();
    await child.WaitForExitAsync();

    using var observation = new RecoveryProcessProbe().Observe(expected.Identity,
      expected.Image, expected.Sha256);
    Assert.Equal(ProcessLife.Exited, observation.Life);
    Assert.Null(observation.Process);
  }

  [Fact]
  public async Task ReusedPidCreationIdentityIsExitedWithoutAdoptingReplacement()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var child = StartHelper();
    var expected = Expected(child);
    var stale = expected.Identity with { StartTimeUtcTicks = expected.Identity.StartTimeUtcTicks + 1 };

    using var observation = new RecoveryProcessProbe().Observe(stale, expected.Image,
      expected.Sha256);
    Assert.Equal(ProcessLife.Exited, observation.Life);
    Assert.Null(observation.Process);
    Assert.False(child.HasExited);
    await StopOwnedChild(child);
  }

  [Fact]
  public async Task PathAndHashMismatchAreUnknownWhileRecordedProcessRemainsAlive()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var child = StartHelper();
    var expected = Expected(child);
    var probe = new RecoveryProcessProbe();

    using var pathMismatch = probe.Observe(expected.Identity with
    {
      ExecutablePath = Path.Combine(Path.GetDirectoryName(expected.Identity.ExecutablePath)!,
        "different.exe")
    }, expected.Image, expected.Sha256);
    using var hashMismatch = probe.Observe(expected.Identity, expected.Image,
      new string('F', 64));
    using var fileIdentityMismatch = probe.Observe(expected.Identity,
      expected.Image with { FileIdHex = new string('F', 32) }, expected.Sha256);

    Assert.Equal(ProcessLife.Unknown, pathMismatch.Life);
    Assert.Equal(ProcessLife.Unknown, hashMismatch.Life);
    Assert.Equal(ProcessLife.Unknown, fileIdentityMismatch.Life);
    Assert.Null(pathMismatch.Process);
    Assert.Null(hashMismatch.Process);
    Assert.Null(fileIdentityMismatch.Process);
    Assert.False(child.HasExited);
    await StopOwnedChild(child);
  }

  [Theory]
  [InlineData("denied")]
  [InlineData("unclassified")]
  public void NativeAcquisitionFailuresAreUnknown(string failure)
  {
    if (!OperatingSystem.IsWindows()) return;
    using var current = SupervisedProcess.CaptureCurrent();
    Exception exception = failure == "denied" ? new Win32Exception(5) :
      new InvalidOperationException("Injected native acquisition failure.");
    var probe = new RecoveryProcessProbe(_ => throw exception);

    using var observation = probe.Observe(current.Identity, current.ImageIdentity,
      current.ImageSha256);
    Assert.Equal(ProcessLife.Unknown, observation.Life);
    Assert.Null(observation.Process);
  }

  [Fact]
  public void RejectsMalformedRecordedIdentityBeforeNativeAccess()
  {
    if (!OperatingSystem.IsWindows()) return;
    var probe = new RecoveryProcessProbe(_ => throw new Xunit.Sdk.XunitException(
      "Native acquisition must not run for malformed input."));
    var invalid = new ProcessIdentity(0, 1, "relative.exe");

    Assert.Throws<ArgumentException>(() => probe.Observe(invalid,
      new FileIdentity(1, new string('1', 32)), new string('1', 64)));
  }

  private static Process StartHelper() => Process.Start(new ProcessStartInfo(
    ProcessSupervisorTests.HelperPath())
  {
    ArgumentList = { "hold" },
    UseShellExecute = false,
    CreateNoWindow = true
  })!;

  private static (ProcessIdentity Identity, FileIdentity Image, string Sha256) Expected(
    Process process)
  {
    var identity = ProcessSupervisor.Identity(process.SafeHandle);
    var image = ProcessSupervisor.ImageFingerprint(identity.ExecutablePath);
    return (identity, image.Identity, image.Sha256);
  }

  private static async Task StopOwnedChild(Process child)
  {
    if (!child.HasExited) child.Kill();
    await child.WaitForExitAsync();
  }
}
