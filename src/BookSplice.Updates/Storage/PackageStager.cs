using System.Runtime.Versioning;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;
using BookSplice.Updates.Transport;
using BookSplice.Updates.Windows;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Storage;

public sealed record PromotionIntent(Guid InstallationId, Guid OperationId, OwnedDirectory Source,
  OwnedDirectory DestinationParent, string DestinationLeaf, string DestinationPath,
  PackageSnapshot Snapshot);

public interface IPromotionJournal
{
  void RecordPromotionIntent(PromotionIntent intent);
  void RecordPromotedDirectory(PromotionIntent intent, OwnedDirectory promoted);
}

[SupportedOSPlatform("windows")]
public sealed class StagedPackage : IDisposable
{
  private PackageInventory? _inventory;

  internal StagedPackage(ReleaseCandidate release, VerifiedManifest manifest, Guid operationId,
    OwnedDirectory stage, OwnedDirectory payload, PackageInventory inventory)
  {
    Release = release with { Assets = Array.AsReadOnly(release.Assets.ToArray()) };
    Manifest = manifest;
    OperationId = operationId;
    Stage = stage;
    Payload = payload;
    _inventory = inventory;
  }

  public ReleaseCandidate Release { get; }
  public VerifiedManifest Manifest { get; }
  public Guid OperationId { get; }
  public OwnedDirectory Stage { get; }
  public OwnedDirectory Payload { get; }
  public string GuiPath => Path.Combine(Payload.Path, "BookSplice.Gui.exe");
  public PackageInventory Inventory => _inventory ?? throw new ObjectDisposedException(nameof(StagedPackage));

  internal PackageSnapshot ReleaseForPromotion()
  {
    var inventory = _inventory ?? throw new ObjectDisposedException(nameof(StagedPackage));
    var snapshot = inventory.Snapshot();
    _inventory = null;
    inventory.Dispose();
    return snapshot;
  }

  public void Dispose()
  {
    _inventory?.Dispose();
    _inventory = null;
  }
}

[SupportedOSPlatform("windows")]
public sealed class PromotedPackage : IDisposable
{
  internal PromotedPackage(OwnedDirectory owned, PackageInventory inventory, VerifiedManifest manifest,
    Guid operationId)
  {
    Owned = owned;
    Inventory = inventory;
    Manifest = manifest;
    OperationId = operationId;
  }

  public OwnedDirectory Owned { get; }
  public PackageInventory Inventory { get; }
  public VerifiedManifest Manifest { get; }
  public Guid OperationId { get; }
  public string GuiPath => Path.Combine(Owned.Path, "BookSplice.Gui.exe");
  public void Dispose() => Inventory.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class PackageStager
{
  private readonly PackageDownloader _downloader;
  private readonly OwnedDirectoryStore _store;
  private readonly SafeZipExtractor _extractor;
  private readonly IPromotionJournal _journal;
  private readonly Func<string, long> _availableBytes;
  private readonly long _extractionReserve;
  private readonly Func<Guid> _operationId;
  private readonly Action<string>? _beforeExtraction;
  private readonly Action<string>? _afterRelease;

  public PackageStager(PackageDownloader downloader, OwnedDirectoryStore store,
    IPromotionJournal journal)
    : this(downloader, store, journal, new SafeZipExtractor(store),
      path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace,
      UpdateLimits.MaxExtractedTotalBytes, Guid.NewGuid)
  { }

  internal PackageStager(PackageDownloader downloader, OwnedDirectoryStore store,
    IPromotionJournal journal, SafeZipExtractor extractor, Func<string, long> availableBytes,
    long extractionReserve, Func<Guid> operationId, Action<string>? beforeExtraction = null,
    Action<string>? afterRelease = null)
  {
    _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    _store = store ?? throw new ArgumentNullException(nameof(store));
    _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    _availableBytes = availableBytes ?? throw new ArgumentNullException(nameof(availableBytes));
    _extractionReserve = extractionReserve >= 0 ? extractionReserve :
      throw new ArgumentOutOfRangeException(nameof(extractionReserve));
    _operationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
    _beforeExtraction = beforeExtraction;
    _afterRelease = afterRelease;
  }

  public async Task<StagedPackage> StageAsync(ReleaseCandidate release, VerifiedManifest manifest,
    InstallationLayout layout, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(release);
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(layout);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(UpdateLimits.StagingTimeout);
    var token = deadline.Token;
    token.ThrowIfCancellationRequested();
    layout.Validate(_store);
    if (manifest.ReleaseIdentity.Id != release.Id || manifest.Tag != release.Tag ||
        manifest.ArchiveLength is <= 0 or > UpdateLimits.ZipMaxBytes)
      throw new PackageException("The release and verified manifest disagree.");
    CheckSpace(layout.Staging.Path, checked(manifest.ArchiveLength + _extractionReserve));
    var operation = _operationId();
    if (operation == Guid.Empty) throw new PackageException("A staging operation ID is required.");
    var stage = _store.CreateNamedExclusive(layout.Staging, operation.ToString("D"),
      "stage", layout.InstallationId, operation);
    var payload = _store.CreateNamedExclusive(stage, "payload", "payload", layout.InstallationId, operation);
    SafeFileHandle archiveHandle;
    using (var stageHandle = _store.OpenValidated(stage))
      archiveHandle = stageHandle.CreateNewFile(manifest.ArchiveName);
    using var ownedArchiveHandle = archiveHandle;
    await using var archive = new FileStream(archiveHandle, FileAccess.ReadWrite, 81920, true);
    PackageInventory? inventory = null;
    try
    {
      await _downloader.DownloadAsync(release, manifest, archive, null, token);
      await archive.FlushAsync(token);
      CheckSpace(layout.Staging.Path, _extractionReserve);
      archive.Position = 0;
      _beforeExtraction?.Invoke(Path.Combine(stage.Path, manifest.ArchiveName));
      inventory = await _extractor.ExtractAsync(archive, payload, token);
      inventory.ValidatePackage(manifest);
      token.ThrowIfCancellationRequested();
      var result = new StagedPackage(release, manifest, operation, stage, payload, inventory);
      inventory = null;
      return result;
    }
    finally { inventory?.Dispose(); }
  }

  public Task<PromotedPackage> PromoteAsync(StagedPackage staged, InstallationLayout layout,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(staged);
    ArgumentNullException.ThrowIfNull(layout);
    cancellationToken.ThrowIfCancellationRequested();
    layout.Validate(_store);
    if (staged.Stage.InstallationId != layout.InstallationId ||
        staged.Payload.InstallationId != layout.InstallationId ||
        staged.Stage.OperationId != staged.OperationId ||
        staged.Payload.OperationId != staged.OperationId ||
        !Same(staged.Stage.Path, Path.Combine(layout.Staging.Path, staged.OperationId.ToString("D"))) ||
        !Same(staged.Payload.Path, Path.Combine(staged.Stage.Path, "payload")))
      throw new PackageException("The staged package does not belong to this installation.");
    using (var stageGuard = _store.OpenValidatedReadOnly(staged.Stage)) { }
    var snapshot = staged.ReleaseForPromotion();
    _afterRelease?.Invoke(staged.Payload.Path);
    var leaf = staged.Manifest.Version + "-" + staged.OperationId.ToString("D");
    if (!SafeZipExtractor.IsSafeComponent(leaf))
      throw new PackageException("The promoted version name is invalid.");
    var destination = Path.Combine(layout.Versions.Path, leaf);
    var intent = new PromotionIntent(layout.InstallationId, staged.OperationId, staged.Payload,
      layout.Versions, leaf, destination, snapshot);
    using (var source = PackageInventory.ReopenSnapshot(staged.Payload, snapshot, _store, true,
      staged.Stage))
    using (var destinationParent = _store.OpenValidatedForDestination(layout.Versions))
    {
      cancellationToken.ThrowIfCancellationRequested();
      _journal.RecordPromotionIntent(intent);
      SafeDirectoryHandle.RejectReparseFile(destinationParent.Handle);
      source.ReleaseContentsForRename();
      source.RootHandle.RenameChildTo(destinationParent, leaf);
    }
    var promoted = new OwnedDirectory(destination, staged.Payload.Identity, layout.InstallationId,
      staged.OperationId, "version");
    PackageInventory? confirmed = null;
    try
    {
      confirmed = PackageInventory.ReopenSnapshot(promoted, snapshot, _store, false);
      confirmed.ValidatePackage(staged.Manifest);
      _journal.RecordPromotedDirectory(intent, promoted);
      var result = new PromotedPackage(promoted, confirmed, staged.Manifest, staged.OperationId);
      confirmed = null;
      return Task.FromResult(result);
    }
    finally { confirmed?.Dispose(); }
  }

  private void CheckSpace(string path, long needed)
  {
    if (_availableBytes(path) < needed)
      throw new PackageException("Insufficient space for bounded update staging.");
  }

  private static bool Same(string left, string right) =>
    string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
