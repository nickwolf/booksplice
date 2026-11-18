using System.Runtime.Versioning;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;
using Microsoft.Win32;

namespace BookSplice.Updates.Handover;

// The launcher commands setup runs silently: exit codes and one stderr line only, never a dialog.
// An installed uninstaller never updates but runs the newest launcher, so --installed, --register,
// --unregister and these exit codes must keep working in every later version.
[SupportedOSPlatform("windows")]
public static class InstallerCommands
{
  public const int Done = 0, Failed = 1, ShortcutCollision = 2, Busy = 3, Unknown = 4;
  // Mirrors the key the GUI registers for toasts (WindowsToastNotifier).
  public const string ToastKeyPath = @"Software\Classes\AppUserModelId\BookSplice.App";

  public static string DataRoot()
  {
    var root = Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA");
    return Path.Combine(Path.GetFullPath(string.IsNullOrWhiteSpace(root) ?
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : root), "BookSplice");
  }

  // The first line of VERSION.txt is "BookSplice <version>".
  public static string ReadVersion(string applicationDirectory)
  {
    var first = File.ReadLines(Path.Combine(applicationDirectory, "VERSION.txt")).FirstOrDefault();
    return first is not null && first.StartsWith("BookSplice ", StringComparison.Ordinal) &&
      first["BookSplice ".Length..].Trim() is { Length: > 0 } version ? version :
      throw new InvalidDataException("VERSION.txt has no version.");
  }

  // Creates the installation for the extracted folder and ensures the shortcut. Setup runs it from
  // {app}\app. A run that dies after creating the managed root but before binding leaves an unbound
  // sibling; nothing refers to it and the next run creates a fresh one, so it is left for the uninstaller.
  public static int Register(string applicationDirectory, string dataRoot, bool replaceShortcut,
    TextWriter error, string? programsDirectory = null)
  {
    try
    {
      var appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
      using var gui = GuiInstanceLease.TryAcquire(dataRoot);
      using var update = gui is null ? null : new ActivityLeaseStore(dataRoot).TryEnterUpdate();
      if (update is null) return Refuse(error);
      var version = ReadVersion(appDir);
      var executable = Path.Combine(appDir, "BookSplice.Gui.exe");
      if (!File.Exists(executable)) throw new FileNotFoundException("BookSplice.Gui.exe is missing.");
      var installation = OpenBound(executable, appDir, dataRoot, programsDirectory) ??
        InstallationOpener.Create(appDir, dataRoot, version, programsDirectory);
      var shortcuts = new ManagedShortcutStore(installation.Record, installation.State,
        Path.Combine(installation.RecordsPath, "shortcut.json"), programsDirectory);
      var result = shortcuts.EnsureRecordedShortcut(installation.Layout);
      if (result == ShortcutResult.Collision && replaceShortcut &&
          DeleteRegularFile(Path.Combine(ProgramsPath(programsDirectory), "BookSplice.lnk")))
        result = shortcuts.EnsureRecordedShortcut(installation.Layout);
      if (result == ShortcutResult.Collision)
      {
        error.WriteLine("Installed, but a different BookSplice shortcut already exists and was left alone.");
        return ShortcutCollision;
      }
      if (result == ShortcutResult.Failed) throw new IOException("The shortcut could not be created.");
      return Done;
    }
    catch (Exception failure)
    {
      error.WriteLine("Register failed: " + failure.Message);
      return Failed;
    }
  }

  // Read-only query for setup: Done when the data-root registry binds this folder's BookSplice.Gui.exe,
  // Failed when it does not (or there is no registry yet), Unknown when the registry can't be read, so
  // setup never overwrites a folder it could not rule out.
  public static int IsInstalled(string applicationDirectory, string dataRoot)
  {
    try
    {
      var executable = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)),
        "BookSplice.Gui.exe");
      var registry = InstallationRegistry.Open(dataRoot, create: false);
      if (registry is null) return Failed;
      var read = registry.Read();
      if (read.Kind == StateReadKind.Invalid) return Unknown;
      return read.Value?.Bindings.Any(item => string.Equals(item.ExecutablePath, executable,
        StringComparison.OrdinalIgnoreCase)) == true ? Done : Failed;
    }
    catch (Exception)
    {
      return Unknown;
    }
  }

  // Run from the managed root's launcher. Leaves every folder for the uninstaller.
  public static int Unregister(string managedRoot, string dataRoot, TextWriter error,
    string? programsDirectory = null, string toastKeyPath = ToastKeyPath, ToastRegistration? toasts = null)
  {
    try
    {
      using var gui = GuiInstanceLease.TryAcquire(dataRoot);
      using var update = gui is null ? null : new ActivityLeaseStore(dataRoot).TryEnterUpdate();
      if (update is null) return Refuse(error);
      var installation = InstallationOpener.Open(managedRoot, programsDirectory: programsDirectory,
        hashLauncher: false);
      // A handover between the old window exiting and the launcher taking the admission holds neither lock,
      // so an unfinished journal also counts as busy (the same rule UpdateCleanup uses). Recovery clears it.
      var transaction = installation.Journal.Read();
      if (transaction.Kind == StateReadKind.Invalid || transaction.Value is { } open && !open.IsFinished())
        return Refuse(error);
      RemoveOwnedShortcut(installation, ProgramsPath(programsDirectory));
      // Unbind from the root this installation recorded; the leases above use the environment's root, like the GUI.
      InstallationRegistry.Open(installation.Layout.DataRoot, create: false)?.Unbind(installation.Layout.InstallationId);
      Registry.CurrentUser.DeleteSubKeyTree(toastKeyPath, false);
      // A portable copy and a setup install share the toast identity, so leave another launcher's activator.
      toasts?.RemoveActivatorIfOwnedBy(installation.Layout.StableLauncherPath);
      return Done;
    }
    catch (Exception failure)
    {
      error.WriteLine("Unregister failed: " + failure.Message);
      return Failed;
    }
  }

  private static int Refuse(TextWriter error)
  {
    error.WriteLine("BookSplice is running or an update is in progress. Close it and try again.");
    return Busy;
  }

  private static string ProgramsPath(string? programsDirectory) =>
    Path.TrimEndingDirectorySeparator(Path.GetFullPath(programsDirectory ?? UpdateBuild.ProgramsDirectory()));

  // Reuses the installation already bound to this folder. A binding whose managed root is gone or
  // unreadable is dropped so Create can start over; other failures (a sharing violation) propagate.
  private static OpenedInstallation? OpenBound(string executable, string appDir, string dataRoot,
    string? programsDirectory)
  {
    var registry = InstallationRegistry.Open(dataRoot, create: false);
    if (registry is null) return null;
    var read = registry.Read();
    if (read.Kind == StateReadKind.Invalid) throw new InvalidDataException("The installation registry is invalid.");
    var binding = read.Value?.Bindings.SingleOrDefault(item =>
      string.Equals(item.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase));
    if (binding is null) return null;
    try
    {
      using var directory = SafeDirectoryHandle.OpenPath(appDir, false);
      if (directory.Identity == binding.DirectoryIdentity)
      {
        var opened = InstallationOpener.Open(binding.ManagedRootPath, true, programsDirectory);
        if (opened.Layout.InstallationId == binding.InstallationId) return opened;
      }
    }
    catch (Exception failure) when (failure is InvalidDataException || IsMissing(failure))
    { }
    registry.Unbind(binding.InstallationId);
    return null;
  }

  private static bool DeleteRegularFile(string path)
  {
    var info = new FileInfo(path);
    if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
      return false;
    info.Delete();
    return true;
  }

  private static void RemoveOwnedShortcut(OpenedInstallation installation, string programs)
  {
    var record = installation.State.Read<ShortcutOwnershipRecord>(
      Path.Combine(installation.RecordsPath, "shortcut.json"));
    if (record.Value is not { } owned) return;
    try
    {
      using (var directory = SafeDirectoryHandle.OpenPath(programs, true))
      using (var file = directory.OpenFile("BookSplice.lnk"))
      {
        SafeDirectoryHandle.RejectReparseFile(file);
        if (NativeFileIdentity.Read(file) != owned.FileIdentity ||
            !string.Equals(PackageInventory.HashHandle(file), owned.Sha256, StringComparison.OrdinalIgnoreCase))
          return;
      }
      File.Delete(owned.Path);
    }
    catch (Exception failure) when (IsMissing(failure))
    { }
  }

  // The directory walk reports a missing component as a Win32Exception, not a FileNotFoundException.
  private static bool IsMissing(Exception failure) => failure is FileNotFoundException or
    DirectoryNotFoundException or System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 };
}
