using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using BookSplice.Updates.Storage;

namespace BookSplice.Updates.Windows;

public enum ShortcutResult { Created, Unchanged, Collision, Failed }

public sealed record ShortcutOwnershipRecord(int SchemaVersion, Guid InstallationId,
  string Path, FileIdentity FileIdentity, string Sha256, string Target, string Arguments)
{
  public static StateRecordDefinition<ShortcutOwnershipRecord> Definition(string name,
    Guid installationId, string expectedPath, string target) => new(name,
      value => value.SchemaVersion == 1 && value.InstallationId == installationId &&
        string.Equals(value.Path, expectedPath, StringComparison.OrdinalIgnoreCase) &&
        value.FileIdentity.IsValid && value.Sha256 is { Length: 64 } &&
        value.Sha256.All(Uri.IsHexDigit) &&
        string.Equals(value.Target, target, StringComparison.OrdinalIgnoreCase) &&
        value.Arguments.Length == 0);
}

[SupportedOSPlatform("windows")]
public sealed class ManagedShortcutStore
{
  private readonly InstallationRecord _installation;
  private readonly AtomicStateStore _state;
  private readonly string _recordPath;
  private readonly string _programs;

  public ManagedShortcutStore(InstallationRecord installation, AtomicStateStore state,
    string recordPath, string? programsDirectory = null)
  {
    _installation = installation ?? throw new ArgumentNullException(nameof(installation));
    _state = state ?? throw new ArgumentNullException(nameof(state));
    _recordPath = recordPath ?? throw new ArgumentNullException(nameof(recordPath));
    _programs = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programsDirectory ??
      UpdateBuild.ProgramsDirectory()));
  }

  public ShortcutResult EnsureRecordedShortcut(InstallationLayout layout)
  {
    if (layout.InstallationId != _installation.InstallationId ||
        layout.ManagedRoot != _installation.ManagedRoot ||
        _installation.StableLauncherIdentity is not { } launcherIdentity ||
        _installation.StableLauncherSha256 is not { } launcherHash)
      return ShortcutResult.Failed;
    var path = Path.Combine(_programs, "BookSplice.lnk");
    try
    {
      using (var launcherRoot = SafeDirectoryHandle.OpenPath(layout.ManagedRoot.Path, false))
      {
        if (launcherRoot.Identity != layout.ManagedRoot.Identity)
          return ShortcutResult.Failed;
        using var launcher = launcherRoot.OpenFile("BookSplice.Launcher.exe");
        SafeDirectoryHandle.RejectReparseFile(launcher);
        if (NativeFileIdentity.Read(launcher) != launcherIdentity ||
            !string.Equals(PackageInventory.HashHandle(launcher), launcherHash,
              StringComparison.OrdinalIgnoreCase))
          return ShortcutResult.Failed;
      }
      using var programs = SafeDirectoryHandle.OpenPath(_programs, true);
      var read = _state.Read<ShortcutOwnershipRecord>(_recordPath);
      if (read.Kind == StateReadKind.Invalid) return ShortcutResult.Failed;
      if (read.Kind == StateReadKind.Valid)
      {
        var recorded = read.Value!;
        if (recorded.InstallationId != layout.InstallationId ||
            !string.Equals(recorded.Path, path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(recorded.Target, layout.StableLauncherPath,
              StringComparison.OrdinalIgnoreCase) || recorded.Arguments.Length != 0)
          return ShortcutResult.Collision;
        using var existing = programs.OpenFile("BookSplice.lnk");
        SafeDirectoryHandle.RejectReparseFile(existing);
        return NativeFileIdentity.Read(existing) == recorded.FileIdentity &&
          string.Equals(PackageInventory.HashHandle(existing), recorded.Sha256,
            StringComparison.OrdinalIgnoreCase) ?
          ShortcutResult.Unchanged : ShortcutResult.Collision;
      }
      if (File.Exists(path) || Directory.Exists(path)) return ShortcutResult.Collision;
      var bytes = CreateLink(layout.StableLauncherPath);
      using var created = programs.CreateNewFile("BookSplice.lnk");
      RandomAccess.Write(created, bytes, 0);
      RandomAccess.FlushToDisk(created);
      var identity = NativeFileIdentity.Read(created);
      var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
      if (!string.Equals(PackageInventory.HashHandle(created), digest,
            StringComparison.OrdinalIgnoreCase))
        return ShortcutResult.Failed;
      var record = new ShortcutOwnershipRecord(1, layout.InstallationId, path,
        identity, digest, layout.StableLauncherPath, "");
      _state.Transition<ShortcutOwnershipRecord>(_recordPath, current =>
        current.Kind == StateReadKind.Missing ? record :
        throw new InvalidDataException("The shortcut ownership record appeared during creation."));
      return ShortcutResult.Created;
    }
    catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 80 or 183 })
    { return ShortcutResult.Collision; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
      Win32Exception or COMException or NotSupportedException or ArgumentException)
    { return ShortcutResult.Failed; }
  }

  [DllImport("ole32.dll")]
  private static extern int CreateStreamOnHGlobal(nint memory, bool deleteOnRelease,
    out IStream stream);

  private static byte[] CreateLink(string target)
  {
    var linkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!;
    var link = Activator.CreateInstance(linkType)!;
    IStream? stream = null;
    try
    {
      ((IShellLinkW)link).SetPath(target);
      ((IShellLinkW)link).SetArguments("");
      ((IShellLinkW)link).SetDescription("BookSplice");
      Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, true, out stream));
      ((IPersistStream)link).Save(stream, true);
      stream.Stat(out var stat, 1);
      var length = checked((int)stat.cbSize);
      if (length is < 76 or > 1024 * 1024)
        throw new InvalidDataException("The shortcut size is invalid.");
      var bytes = new byte[length];
      stream.Seek(0, 0, 0);
      var readCount = Marshal.AllocHGlobal(sizeof(int));
      try
      {
        stream.Read(bytes, bytes.Length, readCount);
        if (Marshal.ReadInt32(readCount) != bytes.Length)
          throw new InvalidDataException("The shortcut stream was truncated.");
      }
      finally { Marshal.FreeHGlobal(readCount); }
      return bytes;
    }
    finally
    {
      if (stream is not null) Marshal.ReleaseComObject(stream);
      Marshal.ReleaseComObject(link);
    }
  }

  internal static (string Target, string Arguments) InspectLink(string path)
  {
    var linkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!;
    var link = Activator.CreateInstance(linkType)!;
    var target = Marshal.AllocHGlobal(32768 * sizeof(char));
    var arguments = Marshal.AllocHGlobal(32768 * sizeof(char));
    try
    {
      ((IPersistFile)link).Load(path, 0);
      ((IShellLinkW)link).GetPath(target, 32768, 0, 0);
      ((IShellLinkW)link).GetArguments(arguments, 32768);
      return (Marshal.PtrToStringUni(target) ?? "", Marshal.PtrToStringUni(arguments) ?? "");
    }
    finally
    {
      Marshal.FreeHGlobal(arguments);
      Marshal.FreeHGlobal(target);
      Marshal.ReleaseComObject(link);
    }
  }

  [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellLinkW
  {
    void GetPath(nint file, int maxPath, nint data, uint flags);
    void GetIDList(out nint idList);
    void SetIDList(nint idList);
    void GetDescription(nint name, int maxName);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory(nint directory, int maxPath);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
    void GetArguments(nint arguments, int maxPath);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int show);
    void SetShowCmd(int show);
    void GetIconLocation(nint icon, int maxPath, out int index);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    void Resolve(nint hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
  }

  [ComImport, Guid("00000109-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IPersistStream
  {
    void GetClassID(out Guid classId);
    [PreserveSig] int IsDirty();
    void Load(IStream stream);
    void Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
    void GetSizeMax(out long size);
  }
}
