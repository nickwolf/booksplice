using System.Runtime.Versioning;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Storage;

[SupportedOSPlatform("windows")]
public sealed record InstallationBinding(string ExecutablePath, FileIdentity DirectoryIdentity,
  FileIdentity ExecutableIdentity, Guid InstallationId, TargetKind Kind,
  string ManagedRootPath, FileIdentity ManagedRootIdentity)
{
  public bool IsValid() => InstallationId != Guid.Empty && DirectoryIdentity.IsValid &&
    ExecutableIdentity.IsValid && ManagedRootIdentity.IsValid &&
    RecoveryRecordValidation.Absolute(ExecutablePath) &&
    RecoveryRecordValidation.Absolute(ManagedRootPath) &&
    Enum.IsDefined(Kind);
}

[SupportedOSPlatform("windows")]
public sealed record InstallationRegistryState(int SchemaVersion,
  IReadOnlyList<InstallationBinding> Bindings)
{
  public static StateRecordDefinition<InstallationRegistryState> Definition(string filename) =>
    new(filename, value => value.SchemaVersion == 1 && value.Bindings is not null &&
      value.Bindings.Count <= 10_000 && value.Bindings.All(binding => binding is not null &&
      binding.IsValid()) &&
      value.Bindings.Select(binding => binding.ExecutablePath)
        .Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.Bindings.Count);
}

[SupportedOSPlatform("windows")]
public sealed class InstallationRegistry(AtomicStateStore store, string path)
{
  public StateRead<InstallationRegistryState> Read() => store.Read<InstallationRegistryState>(path);

  public InstallationBinding? Resolve(string executablePath, FileIdentity directoryIdentity,
    FileIdentity executableIdentity)
  {
    if (!RecoveryRecordValidation.Absolute(executablePath) || !directoryIdentity.IsValid ||
        !executableIdentity.IsValid)
      throw new ArgumentException("The executable capability is invalid.");
    var read = Read();
    if (read.Kind == StateReadKind.Invalid) throw new InvalidDataException("The installation registry is invalid.");
    var binding = read.Value?.Bindings.SingleOrDefault(item =>
      string.Equals(item.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
    if (binding is not null && (binding.DirectoryIdentity != directoryIdentity ||
        binding.ExecutableIdentity != executableIdentity))
      throw new InvalidDataException("The registered executable directory was replaced.");
    return binding;
  }

  public void Bind(InstallationBinding binding, InstallationRecord installation)
  {
    ArgumentNullException.ThrowIfNull(binding);
    ArgumentNullException.ThrowIfNull(installation);
    if (!binding.IsValid() || !installation.IsValid() ||
        binding.InstallationId != installation.InstallationId ||
        binding.ManagedRootIdentity != installation.ManagedRoot.Identity ||
        !string.Equals(binding.ManagedRootPath, installation.ManagedRoot.Path,
          StringComparison.OrdinalIgnoreCase) ||
        (binding.Kind == TargetKind.Original &&
         (binding.DirectoryIdentity != installation.OriginalIdentity ||
          binding.ExecutableIdentity != installation.OriginalExecutableIdentity ||
          !string.Equals(binding.ExecutablePath, installation.OriginalExecutablePath,
            StringComparison.OrdinalIgnoreCase))) ||
        (binding.Kind == TargetKind.Managed &&
         (!string.Equals(Path.GetFileName(binding.ExecutablePath), "BookSplice.Gui.exe",
           StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(binding.ExecutablePath)),
            installation.Versions.Path, StringComparison.OrdinalIgnoreCase))))
      throw new InvalidDataException("The installation binding is invalid.");
    store.Transition<InstallationRegistryState>(path, read =>
    {
      if (read.Kind == StateReadKind.Invalid)
        throw new InvalidDataException("The installation registry is invalid.");
      var entries = read.Value?.Bindings.ToList() ?? [];
      var existing = entries.FindIndex(item => string.Equals(item.ExecutablePath,
        binding.ExecutablePath, StringComparison.OrdinalIgnoreCase));
      if (existing >= 0)
      {
        if (entries[existing] != binding)
          throw new InvalidDataException("The executable is already bound to another identity.");
        return read.Value!;
      }
      entries.Add(binding);
      return new InstallationRegistryState(1, entries);
    });
  }
}

[SupportedOSPlatform("windows")]
public sealed class DurableOwnershipRecorder
{
  private readonly AtomicStateStore _store;
  private readonly string _path;
  private readonly Guid _installationId;
  private readonly FileIdentity _recordRootIdentity;

  public DurableOwnershipRecorder(AtomicStateStore store, string path, OwnedDirectory recordRoot,
    OwnedDirectory managedRoot)
  {
    _store = store ?? throw new ArgumentNullException(nameof(store));
    _path = path ?? throw new ArgumentNullException(nameof(path));
    _installationId = recordRoot.InstallationId;
    _recordRootIdentity = recordRoot.Identity;
    _ = OwnershipState.Definition("ownership.json", recordRoot, managedRoot);
  }

  public void Record(OwnedDirectory created)
  {
    ArgumentNullException.ThrowIfNull(created);
    if (created.InstallationId != _installationId || !created.Identity.IsValid)
      throw new InvalidDataException("The owned directory belongs to another installation.");
    _store.Transition<OwnershipState>(_path, read =>
    {
      if (read.Kind == StateReadKind.Invalid)
        throw new InvalidDataException("The ownership state is invalid.");
      var entries = read.Value?.Entries.ToList() ?? [];
      var entry = OwnershipEntry.FromCreated(created);
      var existing = entries.FindIndex(item => string.Equals(item.Path, entry.Path,
        StringComparison.OrdinalIgnoreCase));
      if (existing >= 0)
      {
        if (entries[existing] != entry)
          throw new InvalidDataException("The owned path was recorded with another identity.");
        return read.Value!;
      }
      entries.Add(entry);
      return new OwnershipState(1, _installationId, _recordRootIdentity, entries);
    });
  }
}
