using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using BookSplice.Updates.Handover;

namespace BookSplice.Updates.Windows;

public enum ProcessLife { Alive, Exited, Unknown }

[SupportedOSPlatform("windows")]
public sealed class RecoveryProcessObservation : IDisposable
{
  private bool _disposed;

  internal RecoveryProcessObservation(ProcessLife life, SupervisedProcess? process = null)
  {
    if ((life == ProcessLife.Alive) != (process is not null))
      throw new ArgumentException("Only an alive observation can own a process capability.");
    Life = life;
    Process = process;
  }

  public ProcessLife Life { get; }
  public SupervisedProcess? Process { get; }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    Process?.Dispose();
  }
}

[SupportedOSPlatform("windows")]
public sealed class RecoveryProcessProbe
{
  private readonly Func<int, Process> _openProcess;

  public RecoveryProcessProbe() : this(Process.GetProcessById) { }

  internal RecoveryProcessProbe(Func<int, Process> openProcess) =>
    _openProcess = openProcess ?? throw new ArgumentNullException(nameof(openProcess));

  public RecoveryProcessObservation Observe(ProcessIdentity expected,
    FileIdentity expectedImageIdentity, string expectedImageSha256)
  {
    Validate(expected, expectedImageIdentity, expectedImageSha256);

    Process process;
    try
    {
      process = _openProcess(expected.Pid);
    }
    catch (ArgumentException)
    {
      return new RecoveryProcessObservation(ProcessLife.Exited);
    }
    catch (Exception)
    {
      return new RecoveryProcessObservation(ProcessLife.Unknown);
    }

    try
    {
      var handle = process.SafeHandle;
      var native = ProcessSupervisor.NativeIdentity(handle);
      if (native.Pid != expected.Pid || native.StartTimeUtcTicks != expected.StartTimeUtcTicks)
      {
        process.Dispose();
        return new RecoveryProcessObservation(ProcessLife.Exited);
      }
      if (process.HasExited)
      {
        process.Dispose();
        return new RecoveryProcessObservation(ProcessLife.Exited);
      }

      var actualPath = ProcessSupervisor.ImagePath(handle);
      if (!string.Equals(actualPath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
      {
        process.Dispose();
        return new RecoveryProcessObservation(ProcessLife.Unknown);
      }

      var image = ProcessSupervisor.ImageFingerprintOrDirect(actualPath);
      if (image.Identity != expectedImageIdentity ||
          !string.Equals(image.Sha256, expectedImageSha256, StringComparison.OrdinalIgnoreCase))
      {
        process.Dispose();
        return new RecoveryProcessObservation(ProcessLife.Unknown);
      }

      var user = ProcessSupervisor.ProcessUser(handle);
      if (WindowsIdentity.GetCurrent().User is not { } currentUser || !user.Equals(currentUser))
      {
        process.Dispose();
        return new RecoveryProcessObservation(ProcessLife.Unknown);
      }

      var confirmed = ProcessSupervisor.NativeIdentity(handle);
      if (confirmed.Pid != expected.Pid || confirmed.StartTimeUtcTicks != expected.StartTimeUtcTicks)
      {
        process.Dispose();
        return new RecoveryProcessObservation(ProcessLife.Exited);
      }
      if (process.HasExited)
      {
        process.Dispose();
        return new RecoveryProcessObservation(ProcessLife.Exited);
      }

      var identity = new ProcessIdentity(native.Pid, native.StartTimeUtcTicks, actualPath);
      return new RecoveryProcessObservation(ProcessLife.Alive,
        new SupervisedProcess(process, identity, image.Identity, image.Sha256, user));
    }
    catch (Exception)
    {
      process.Dispose();
      return new RecoveryProcessObservation(ProcessLife.Unknown);
    }
  }

  private static void Validate(ProcessIdentity expected, FileIdentity imageIdentity,
    string imageSha256)
  {
    ArgumentNullException.ThrowIfNull(expected);
    ArgumentNullException.ThrowIfNull(imageSha256);
    if (expected.Pid <= 0 || expected.StartTimeUtcTicks <= 0 ||
        !Path.IsPathFullyQualified(expected.ExecutablePath) ||
        expected.ExecutablePath.Contains('\0') || !imageIdentity.IsValid ||
        imageSha256.Length != 64 || !imageSha256.All(Uri.IsHexDigit))
      throw new ArgumentException("The expected process or image identity is invalid.");
  }
}
