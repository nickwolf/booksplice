using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
  private readonly SecurityIdentifier _user;
  private bool _disposed;

  internal SupervisedProcess(Process process, ProcessIdentity identity, FileIdentity imageIdentity,
    SecurityIdentifier user)
  {
    _process = process;
    Identity = identity;
    _imageIdentity = imageIdentity;
    _user = user;
  }

  public ProcessIdentity Identity { get; }
  public bool HasExited => _process.HasExited;

  public static SupervisedProcess CaptureCurrent()
  {
    var process = Process.GetCurrentProcess();
    try
    {
      var path = ProcessSupervisor.ImagePath(process.SafeHandle);
      return new SupervisedProcess(process,
        new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, path),
        ProcessSupervisor.ImageIdentity(path), ProcessSupervisor.ProcessUser(process.SafeHandle));
    }
    catch { process.Dispose(); throw; }
  }

  public static SupervisedProcess CaptureExpected(ProcessIdentity expected)
  {
    ArgumentNullException.ThrowIfNull(expected);
    if (expected.Pid <= 0 || expected.StartTimeUtcTicks <= 0 ||
        !System.IO.Path.IsPathFullyQualified(expected.ExecutablePath))
      throw new ArgumentException("The expected process identity is invalid.");
    var process = Process.GetProcessById(expected.Pid);
    try
    {
      var actual = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks,
        ProcessSupervisor.ImagePath(process.SafeHandle));
      if (actual != expected || process.HasExited)
        throw new IOException("The expected process identity changed.");
      var user = ProcessSupervisor.ProcessUser(process.SafeHandle);
      if (WindowsIdentity.GetCurrent().User is not { } currentUser || !user.Equals(currentUser))
        throw new IOException("The expected process user differs.");
      return new SupervisedProcess(process, actual,
        ProcessSupervisor.ImageIdentity(actual.ExecutablePath), user);
    }
    catch { process.Dispose(); throw; }
  }

  internal void VerifyPeer(int pid)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (pid != Identity.Pid || _process.Id != pid || _process.StartTime.ToUniversalTime().Ticks !=
        Identity.StartTimeUtcTicks || _process.HasExited)
      throw new IOException("The pipe peer is not the retained process.");
    if (!string.Equals(ProcessSupervisor.ImagePath(_process.SafeHandle), Identity.ExecutablePath,
        StringComparison.OrdinalIgnoreCase) ||
        ProcessSupervisor.ImageIdentity(Identity.ExecutablePath) != _imageIdentity ||
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
    if (_process.HasExited) return;
    VerifyPeer(Identity.Pid);
    if (!ProcessSupervisor.TerminateProcess(_process.SafeHandle, 1))
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop the retained process.");
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
public static class ProcessSupervisor
{
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, int flags,
    char[] path, ref int length);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);

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

  internal static FileIdentity ImageIdentity(string path)
  {
    using var directory = SafeDirectoryHandle.OpenPath(System.IO.Path.GetDirectoryName(path)!, false);
    using var file = directory.OpenFile(System.IO.Path.GetFileName(path));
    SafeDirectoryHandle.RejectReparseFile(file);
    return NativeFileIdentity.Read(file);
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
      var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks,
        ImagePath(process.SafeHandle));
      if (!string.Equals(identity.ExecutablePath, verifiedExecutable.Path,
          StringComparison.OrdinalIgnoreCase) ||
          ImageIdentity(identity.ExecutablePath) != verifiedExecutable.Identity ||
          ProcessUser(process.SafeHandle) != WindowsIdentity.GetCurrent().User)
        throw new IOException("The child process does not match the verified executable.");
      verifiedExecutable.Revalidate();
      return new SupervisedProcess(process, identity, verifiedExecutable.Identity,
        WindowsIdentity.GetCurrent().User!);
    }
    catch
    {
      if (!process.HasExited) TerminateProcess(process.SafeHandle, 1);
      process.Dispose();
      throw;
    }
  }
}
