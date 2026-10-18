using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Storage;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

[SupportedOSPlatform("windows")]
public sealed class VerifiedExecutable : IDisposable
{
  private readonly Action _revalidate;
  private readonly IDisposable _inventory;
  private bool _disposed;

  private VerifiedExecutable(string path, FileIdentity identity, IDisposable inventory,
    Action revalidate)
  {
    Path = path;
    Identity = identity;
    _inventory = inventory;
    _revalidate = revalidate;
  }

  public string Path { get; }
  public FileIdentity Identity { get; }

  public static VerifiedExecutable FromManaged(PackageInventory inventory,
    OwnedOrOriginalTarget target)
  {
    ArgumentNullException.ThrowIfNull(inventory);
    ArgumentNullException.ThrowIfNull(target);
    if (target.Kind != TargetKind.Managed || !target.IsValid() ||
        target.Package is null || target.OwnedDirectory is null ||
        target.OwnedDirectory != inventory.Destination ||
        target.Package.Restore().Sha256 != inventory.Snapshot().Sha256)
      throw new PackageException("The managed launch target differs from its retained inventory.");
    inventory.ValidatePackage(target.Version);
    var file = inventory.Snapshot().Files.Single(x =>
      string.Equals(x.RelativePath, "BookSplice.Gui.exe", StringComparison.Ordinal));
    if (file.Identity != target.ExecutableIdentity ||
        !string.Equals(target.ExecutablePath,
          System.IO.Path.Combine(inventory.Destination.Path, file.RelativePath),
          StringComparison.OrdinalIgnoreCase))
      throw new PackageException("The managed executable differs from the launch target.");
    return new VerifiedExecutable(System.IO.Path.Combine(inventory.Destination.Path, file.RelativePath),
      file.Identity, inventory, inventory.Revalidate);
  }

  public static VerifiedExecutable FromOriginal(OriginalPackageInventory inventory,
    OwnedOrOriginalTarget target)
  {
    ArgumentNullException.ThrowIfNull(inventory);
    ArgumentNullException.ThrowIfNull(target);
    inventory.Revalidate();
    if (target.Kind != TargetKind.Original || !target.IsValid() || target.Package is null ||
        target.Package.Restore().Sha256 != inventory.Snapshot.Sha256 ||
        target.DirectoryIdentity != inventory.Snapshot.RootIdentity)
      throw new PackageException("The original launch target differs from its retained inventory.");
    inventory.ValidatePackage(target.Version);
    var file = inventory.Snapshot.Files.Single(x =>
      string.Equals(x.RelativePath, "BookSplice.Gui.exe", StringComparison.Ordinal));
    if (file.Identity != target.ExecutableIdentity ||
        !string.Equals(target.ExecutablePath,
          System.IO.Path.Combine(inventory.RootPath, file.RelativePath),
          StringComparison.OrdinalIgnoreCase))
      throw new PackageException("The original executable differs from the launch target.");
    return new VerifiedExecutable(System.IO.Path.Combine(inventory.RootPath, file.RelativePath),
      file.Identity, inventory, inventory.Revalidate);
  }

  internal static VerifiedExecutable PinFile(string path)
  {
    var directory = SafeDirectoryHandle.OpenPath(System.IO.Path.GetDirectoryName(path)!, false);
    SafeFileHandle? handle = null;
    try
    {
      handle = directory.OpenFile(System.IO.Path.GetFileName(path));
      SafeDirectoryHandle.RejectReparseFile(handle);
      var identity = NativeFileIdentity.Read(handle);
      var length = RandomAccess.GetLength(handle);
      var digest = PackageInventory.HashHandle(handle);
      var guard = new PinnedFile(directory, handle);
      return new VerifiedExecutable(path, identity, guard, () =>
      {
        if (directory.Identity != guard.DirectoryIdentity)
          throw new PackageException("The executable parent changed before launch.");
        if (NativeFileIdentity.Read(handle) != identity || RandomAccess.GetLength(handle) != length ||
            PackageInventory.HashHandle(handle) != digest)
          throw new PackageException("The executable changed before launch.");
      });
    }
    catch { handle?.Dispose(); directory.Dispose(); throw; }
  }

  private sealed class PinnedFile(SafeDirectoryHandle directory, SafeFileHandle file) : IDisposable
  {
    public FileIdentity DirectoryIdentity { get; } = directory.Identity;

    public void Dispose()
    {
      file.Dispose();
      directory.Dispose();
    }
  }

  internal void Revalidate()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _revalidate();
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    _inventory.Dispose();
  }
}

[SupportedOSPlatform("windows")]
public sealed class SupervisedProcess : IDisposable
{
  private readonly Process _process;
  private readonly FileIdentity _imageIdentity;
  private readonly string _imageSha256;
  private readonly SecurityIdentifier _user;
  private bool _disposed;

  internal SupervisedProcess(Process process, ProcessIdentity identity, FileIdentity imageIdentity,
    string imageSha256, SecurityIdentifier user)
  {
    _process = process;
    Identity = identity;
    _imageIdentity = imageIdentity;
    _imageSha256 = imageSha256;
    _user = user;
  }

  public ProcessIdentity Identity { get; }
  public FileIdentity ImageIdentity => _imageIdentity;
  public string ImageSha256 => _imageSha256;
  public bool HasExited => _process.HasExited;

  public static SupervisedProcess CaptureCurrent()
  {
    var process = Process.GetCurrentProcess();
    try
    {
      var handle = process.SafeHandle;
      var native = ProcessSupervisor.NativeIdentity(handle);
      var path = ProcessSupervisor.ImagePath(handle);
      var image = ProcessSupervisor.ImageFingerprint(path);
      return new SupervisedProcess(process,
        new ProcessIdentity(native.Pid, native.StartTimeUtcTicks, path),
        image.Identity, image.Sha256,
        ProcessSupervisor.ProcessUser(handle));
    }
    catch { process.Dispose(); throw; }
  }

  public static SupervisedProcess CaptureExpected(ProcessIdentity expected,
    FileIdentity expectedImageIdentity, string expectedImageSha256)
  {
    ArgumentNullException.ThrowIfNull(expected);
    if (expected.Pid <= 0 || expected.StartTimeUtcTicks <= 0 ||
        !System.IO.Path.IsPathFullyQualified(expected.ExecutablePath) ||
        !expectedImageIdentity.IsValid || expectedImageSha256.Length != 64 ||
        !expectedImageSha256.All(Uri.IsHexDigit))
      throw new ArgumentException("The expected process identity is invalid.");
    var process = Process.GetProcessById(expected.Pid);
    try
    {
      var handle = process.SafeHandle;
      var actual = ProcessSupervisor.ValidateExpectedHandle(handle, expected,
        expectedImageIdentity, expectedImageSha256);
      if (process.HasExited) throw new IOException("The expected process exited.");
      var user = ProcessSupervisor.ProcessUser(handle);
      if (WindowsIdentity.GetCurrent().User is not { } currentUser || !user.Equals(currentUser))
        throw new IOException("The expected process user differs.");
      return new SupervisedProcess(process, actual, expectedImageIdentity,
        expectedImageSha256, user);
    }
    catch { process.Dispose(); throw; }
  }

  internal void VerifyPeer(int pid)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (pid != Identity.Pid || ProcessSupervisor.Identity(_process.SafeHandle) != Identity ||
        _process.HasExited)
      throw new IOException("The pipe peer is not the retained process.");
    var image = ProcessSupervisor.ImageFingerprint(Identity.ExecutablePath);
    if (image.Identity != _imageIdentity ||
        !string.Equals(image.Sha256, _imageSha256, StringComparison.OrdinalIgnoreCase) ||
        !ProcessSupervisor.ProcessUser(_process.SafeHandle).Equals(_user))
      throw new IOException("The pipe peer image or user changed.");
  }

  public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    cancellationToken.ThrowIfCancellationRequested();
    if (_process.HasExited) return;
    var native = ProcessSupervisor.NativeIdentity(_process.SafeHandle);
    if (native.Pid != Identity.Pid || native.StartTimeUtcTicks != Identity.StartTimeUtcTicks)
      throw new IOException("The retained process identity changed.");
    if (!ProcessSupervisor.TerminateProcess(_process.SafeHandle, 1))
    {
      if (!_process.HasExited)
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop the retained process.");
    }
    await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    _process.Dispose();
  }
}

[SupportedOSPlatform("windows")]
public sealed class UnverifiedChild : IDisposable
{
  private readonly Process _process;
  private bool _disposed;

  internal UnverifiedChild(Process process) => _process = process;

  public int Pid => ProcessSupervisor.NativeIdentity(_process.SafeHandle).Pid;
  public bool HasExited => _process.HasExited;

  public async Task StopAndWaitAsync(CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    cancellationToken.ThrowIfCancellationRequested();
    if (!_process.HasExited && !ProcessSupervisor.TerminateProcess(_process.SafeHandle, 1) &&
        !_process.HasExited)
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop the unverified child.");
    await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    _process.Dispose();
  }
}

[SupportedOSPlatform("windows")]
public sealed class UnverifiedChildException(string message, UnverifiedChild child, Exception inner)
  : IOException(message, inner)
{
  public UnverifiedChild Child { get; } = child;
}

[SupportedOSPlatform("windows")]
public static class ProcessSupervisor
{
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, int flags,
    char[] path, ref int length);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern uint GetProcessId(SafeProcessHandle handle);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation,
    out long exit, out long kernel, out long user);

  [DllImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool OpenProcessToken(SafeProcessHandle process, uint access,
    out SafeAccessTokenHandle token);

  internal static string ImagePath(SafeProcessHandle process)
  {
    var chars = new char[32768];
    var length = chars.Length;
    if (!QueryFullProcessImageNameW(process, 0, chars, ref length))
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the process image.");
    return new string(chars, 0, length);
  }

  internal static (int Pid, long StartTimeUtcTicks) NativeIdentity(SafeProcessHandle handle)
  {
    var pid = GetProcessId(handle);
    if (pid == 0 || !GetProcessTimes(handle, out var created, out _, out _, out _))
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the retained process.");
    return (checked((int)pid), DateTime.FromFileTimeUtc(created).Ticks);
  }

  internal static ProcessIdentity Identity(SafeProcessHandle handle)
  {
    var native = NativeIdentity(handle);
    return new ProcessIdentity(native.Pid, native.StartTimeUtcTicks, ImagePath(handle));
  }

  internal static ProcessIdentity ValidateExpectedHandle(SafeProcessHandle handle,
    ProcessIdentity expected, FileIdentity imageIdentity, string imageSha256)
  {
    var actual = Identity(handle);
    if (actual != expected) throw new IOException("The retained process handle differs.");
    var image = ImageFingerprint(actual.ExecutablePath);
    if (image.Identity != imageIdentity ||
        !string.Equals(image.Sha256, imageSha256,
          StringComparison.OrdinalIgnoreCase))
      throw new IOException("The retained process handle differs from the expected identity or image.");
    return actual;
  }

  internal static (FileIdentity Identity, string Sha256) ImageFingerprint(string path)
  {
    using var directory = SafeDirectoryHandle.OpenPath(System.IO.Path.GetDirectoryName(path)!, false);
    using var file = directory.OpenFile(System.IO.Path.GetFileName(path));
    SafeDirectoryHandle.RejectReparseFile(file);
    return (NativeFileIdentity.Read(file), PackageInventory.HashHandle(file));
  }

  internal static SecurityIdentifier ProcessUser(SafeProcessHandle process)
  {
    if (!OpenProcessToken(process, 0x8, out var token))
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the process user.");
    using (token)
    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
      return identity.User ?? throw new IOException("The process has no user SID.");
  }

  public static SupervisedProcess Start(VerifiedExecutable verifiedExecutable,
    IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
  {
    ArgumentNullException.ThrowIfNull(verifiedExecutable);
    ArgumentNullException.ThrowIfNull(arguments);
    ArgumentNullException.ThrowIfNull(environment);
    verifiedExecutable.Revalidate();
    var start = new ProcessStartInfo(verifiedExecutable.Path)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      WorkingDirectory = System.IO.Path.GetDirectoryName(verifiedExecutable.Path)!
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    foreach (var key in start.Environment.Keys.ToArray())
      if (key.StartsWith("BOOKSPLICE_", StringComparison.OrdinalIgnoreCase) ||
          key.StartsWith("FFMPEG_", StringComparison.OrdinalIgnoreCase) ||
          key.StartsWith("FFPROBE_", StringComparison.OrdinalIgnoreCase) ||
          key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
          key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
          key.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase))
        start.Environment.Remove(key);
    foreach (var pair in environment)
    {
      if (!string.Equals(pair.Key, "BOOKSPLICE_LOCAL_APP_DATA", StringComparison.Ordinal) ||
          !System.IO.Path.IsPathFullyQualified(pair.Value) || pair.Value.Contains('\0'))
        throw new ArgumentException("Invalid child environment.");
      start.Environment[pair.Key] = pair.Value;
    }
    var process = Process.Start(start) ?? throw new IOException("The verified executable did not start.");
    try
    {
      var identity = Identity(process.SafeHandle);
      var image = ImageFingerprint(identity.ExecutablePath);
      if (!string.Equals(identity.ExecutablePath, verifiedExecutable.Path,
          StringComparison.OrdinalIgnoreCase) ||
          image.Identity != verifiedExecutable.Identity ||
          ProcessUser(process.SafeHandle) != WindowsIdentity.GetCurrent().User)
        throw new IOException("The child process does not match the verified executable.");
      verifiedExecutable.Revalidate();
      return new SupervisedProcess(process, identity, verifiedExecutable.Identity,
        image.Sha256, WindowsIdentity.GetCurrent().User!);
    }
    catch (Exception error)
    {
      ResolveFailedStart(process, error, handle => TerminateProcess(handle, 1));
      throw;
    }
  }

  internal static void ResolveFailedStart(Process process, Exception original,
    Func<SafeProcessHandle, bool> terminate)
  {
    bool terminated = process.HasExited;
    if (!terminated)
    {
      try { terminated = terminate(process.SafeHandle); }
      catch (Exception) { terminated = false; }
    }
    if (terminated)
    {
      try
      {
        if (process.WaitForExit(10_000))
        {
          process.Dispose();
          ExceptionDispatchInfo.Capture(original).Throw();
        }
      }
      catch (Exception error) when (!ReferenceEquals(error, original))
      {
        throw new UnverifiedChildException("The failed child exit could not be confirmed.",
          new UnverifiedChild(process), original);
      }
    }
    throw new UnverifiedChildException("The failed child may still be running.",
      new UnverifiedChild(process), original);
  }
}
