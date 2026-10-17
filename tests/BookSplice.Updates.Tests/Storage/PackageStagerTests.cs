using System.IO.Compression;
using System.Net;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Transport;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class PackageStagerTests
{
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
    nint security, uint disposition, uint flags, nint template);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input,
    uint inputLength, nint output, uint outputLength, out uint returned, nint overlapped);

  [Fact]
  public async Task SignedPackageStagesAndPromotesWithExactIdentity()
  {
    using var fixture = new Fixture();
    using var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    Assert.Equal(fixture.Operation, staged.OperationId);
    Assert.Equal(Path.Combine(fixture.Layout.Staging.Path, fixture.Operation.ToString("D")), staged.Stage.Path);
    Assert.Equal(Path.Combine(staged.Stage.Path, "payload"), staged.Payload.Path);
    Assert.Equal(19, staged.Inventory.Files.Count);
    staged.Inventory.Revalidate();
    Assert.ThrowsAny<IOException>(() => File.Move(Path.Combine(staged.Stage.Path,
      fixture.Manifest.ArchiveName), Path.Combine(staged.Stage.Path, "swapped.zip")));

    using var promoted = await fixture.Stager.PromoteAsync(staged, fixture.Layout, CancellationToken.None);
    Assert.Equal(staged.Payload.Identity, promoted.Owned.Identity);
    Assert.Equal(Path.Combine(fixture.Layout.Versions.Path, "0.2.0-" + fixture.Operation.ToString("D")),
      promoted.Owned.Path);
    Assert.Single(fixture.Journal.Intents);
    Assert.Single(fixture.Journal.Promoted);
    Assert.Equal(promoted.Owned.Identity, fixture.Journal.Intents[0].Snapshot.RootIdentity);
    promoted.Inventory.Revalidate();
  }

  [Fact]
  public async Task DisposedStageAllowsPlainDirectoryRename()
  {
    using var fixture = new Fixture();
    var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    var path = staged.Payload.Path;
    staged.Dispose();
    Directory.Move(path, path + "-moved");
    Directory.Move(path + "-moved", path);
  }

  [Fact]
  public void BareNativeDirectoryRenameWorks()
  {
    using var fixture = new Fixture();
    var owned = fixture.CreateEmptyPayload();
    using var source = fixture.OpenForRename(owned);
    using var target = fixture.OpenVersions();
    source.RenameChildTo(target, "probe");
    Assert.True(Directory.Exists(Path.Combine(fixture.Layout.Versions.Path, "probe")));
  }

  [Fact]
  public async Task WrongDownloadHashDoesNotCreatePayloadFilesOrPromotionIntent()
  {
    using var fixture = new Fixture(serveWrongArchive: true);
    await Assert.ThrowsAsync<PackageDownloadException>(() => fixture.Stager.StageAsync(
      fixture.Release, fixture.Manifest, fixture.Layout, CancellationToken.None));
    var stage = Assert.Single(Directory.EnumerateDirectories(fixture.Layout.Staging.Path));
    Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(stage, "payload")));
    Assert.Empty(fixture.Journal.Intents);
  }

  [Fact]
  public async Task ExistingStageAndDestinationAreNeverAdopted()
  {
    using var fixture = new Fixture();
    var stagePath = Path.Combine(fixture.Layout.Staging.Path, fixture.Operation.ToString("D"));
    Directory.CreateDirectory(stagePath);
    File.WriteAllText(Path.Combine(stagePath, "sentinel"), "keep");
    await Assert.ThrowsAsync<OwnedDirectoryException>(() => fixture.Stager.StageAsync(
      fixture.Release, fixture.Manifest, fixture.Layout, CancellationToken.None));
    Assert.Equal("keep", File.ReadAllText(Path.Combine(stagePath, "sentinel")));
    Assert.Empty(fixture.Journal.Intents);
  }

  [Fact]
  public async Task ExistingPromotionDestinationIsUntouched()
  {
    using var fixture = new Fixture();
    using var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    var destination = Path.Combine(fixture.Layout.Versions.Path, "0.2.0-" + fixture.Operation.ToString("D"));
    Directory.CreateDirectory(destination);
    File.WriteAllText(Path.Combine(destination, "sentinel"), "keep");
    await Assert.ThrowsAnyAsync<Exception>(() => fixture.Stager.PromoteAsync(staged,
      fixture.Layout, CancellationToken.None));
    Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "sentinel")));
    Assert.Empty(fixture.Journal.Promoted);
  }

  [Fact]
  public async Task FailedOwnershipRecordLeavesRecoverableIntent()
  {
    using var fixture = new Fixture();
    using var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    fixture.Journal.FailPromoted = true;
    await Assert.ThrowsAsync<IOException>(() => fixture.Stager.PromoteAsync(staged,
      fixture.Layout, CancellationToken.None));
    Assert.Single(fixture.Journal.Intents);
    Assert.Empty(fixture.Journal.Promoted);
    Assert.True(Directory.Exists(fixture.Journal.Intents[0].DestinationPath));
  }

  [Fact]
  public async Task CancellationAndDiskFailureRetainOnlyRecordedOwnedPaths()
  {
    using var fixture = new Fixture();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Stager.StageAsync(
      fixture.Release, fixture.Manifest, fixture.Layout, cancelled.Token));
    Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Layout.Staging.Path));
    var shortDisk = fixture.CreateStager(_ => 0);
    await Assert.ThrowsAsync<PackageException>(() => shortDisk.StageAsync(fixture.Release,
      fixture.Manifest, fixture.Layout, CancellationToken.None));
    Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Layout.Staging.Path));
  }

  [Fact]
  public async Task DiskFailureAfterDownloadRetainsExactStageForRecovery()
  {
    using var fixture = new Fixture();
    var checks = 0;
    var stager = fixture.CreateStager(_ => ++checks == 1 ? long.MaxValue : 0);
    await Assert.ThrowsAsync<PackageException>(() => stager.StageAsync(fixture.Release,
      fixture.Manifest, fixture.Layout, CancellationToken.None));
    var stage = Assert.Single(Directory.EnumerateDirectories(fixture.Layout.Staging.Path));
    Assert.True(File.Exists(Path.Combine(stage, fixture.Manifest.ArchiveName)));
    Assert.True(Directory.Exists(Path.Combine(stage, "payload")));
    Assert.Empty(fixture.Journal.Intents);
  }

  [Fact]
  public async Task CancellationAfterDownloadRetainsExactStageForRecovery()
  {
    using var fixture = new Fixture();
    using var cancellation = new CancellationTokenSource();
    var stager = fixture.CreateStager(_ => long.MaxValue,
      beforeExtraction: _ => cancellation.Cancel());
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stager.StageAsync(
      fixture.Release, fixture.Manifest, fixture.Layout, cancellation.Token));
    var stage = Assert.Single(Directory.EnumerateDirectories(fixture.Layout.Staging.Path));
    Assert.True(File.Exists(Path.Combine(stage, fixture.Manifest.ArchiveName)));
    Assert.True(Directory.Exists(Path.Combine(stage, "payload")));
    Assert.Empty(fixture.Journal.Intents);
  }

  [Fact]
  public async Task ArchiveHandleCannotBeSwappedAtExtractionBoundary()
  {
    using var fixture = new Fixture();
    var checkedBoundary = false;
    var stager = fixture.CreateStager(_ => long.MaxValue, archive =>
    {
      checkedBoundary = true;
      Assert.ThrowsAny<IOException>(() => File.Move(archive, archive + ".swapped"));
      Assert.ThrowsAny<IOException>(() => File.WriteAllText(archive, "tampered"));
    });
    using var staged = await stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    Assert.True(checkedBoundary);
    staged.Inventory.Revalidate();
  }

  [Fact]
  public async Task DataRootReplacementIsRefusedBeforeCreatingStage()
  {
    using var fixture = new Fixture();
    Directory.Move(fixture.Layout.DataRoot, fixture.Layout.DataRoot + "-old");
    Directory.CreateDirectory(fixture.Layout.DataRoot);
    await Assert.ThrowsAsync<OwnedDirectoryException>(() => fixture.Stager.StageAsync(
      fixture.Release, fixture.Manifest, fixture.Layout, CancellationToken.None));
    Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Layout.Staging.Path));
  }

  [Fact]
  public async Task SourceMutationAfterGuardReleaseIsRefusedBeforeIntent()
  {
    using var fixture = new Fixture();
    using var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    var stager = fixture.CreateStager(_ => long.MaxValue, afterRelease: path =>
      File.WriteAllText(Path.Combine(path, "BookSplice.Gui.exe"), "tampered"));
    await Assert.ThrowsAsync<PackageException>(() => stager.PromoteAsync(staged,
      fixture.Layout, CancellationToken.None));
    Assert.Empty(fixture.Journal.Intents);
    Assert.True(Directory.Exists(staged.Payload.Path));
  }

  [Fact]
  public async Task ReplacedStageParentIsRefusedEvenWhenPayloadIdentityMatches()
  {
    using var fixture = new Fixture();
    using var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    var stager = fixture.CreateStager(_ => long.MaxValue, afterRelease: payload =>
    {
      var stage = Path.GetDirectoryName(payload)!;
      Directory.Move(stage, stage + "-old");
      Directory.CreateDirectory(stage);
      Directory.Move(Path.Combine(stage + "-old", "payload"), payload);
    });
    await Assert.ThrowsAsync<OwnedDirectoryException>(() => stager.PromoteAsync(staged,
      fixture.Layout, CancellationToken.None));
    Assert.Empty(fixture.Journal.Intents);
  }

  [Fact]
  public async Task DestinationJunctionInjectedAtIntentCannotRedirectRename()
  {
    using var fixture = new Fixture();
    using var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-promotion-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var destination = Path.Combine(fixture.Layout.Versions.Path,
      "0.2.0-" + fixture.Operation.ToString("D"));
    try
    {
      fixture.Journal.OnIntent = _ =>
      {
        Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.Layout.Versions.Path,
          fixture.Layout.Versions.Path + "-moved"));
        CreateJunction(destination, outside);
      };
      await Assert.ThrowsAnyAsync<Exception>(() => fixture.Stager.PromoteAsync(staged,
        fixture.Layout, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
      Assert.Empty(fixture.Journal.Promoted);
      Assert.Single(fixture.Journal.Intents);
      Assert.True(Directory.Exists(staged.Payload.Path));
    }
    finally
    {
      if (Directory.Exists(destination)) Directory.Delete(destination);
      Directory.Delete(outside);
    }
  }

  [Fact]
  public async Task InPlaceDestinationReparseAtIntentCannotRedirectRename()
  {
    using var fixture = new Fixture();
    using var staged = await fixture.Stager.StageAsync(fixture.Release, fixture.Manifest,
      fixture.Layout, CancellationToken.None);
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-reparse-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var set = false;
    try
    {
      fixture.Journal.OnIntent = _ => set = TrySetMountPoint(fixture.Layout.Versions.Path, outside);
      PromotedPackage? promoted = null;
      var error = await Record.ExceptionAsync(async () => promoted =
        await fixture.Stager.PromoteAsync(staged, fixture.Layout, CancellationToken.None));
      Assert.True(set, "The test must mutate the existing destination directory in place.");
      Assert.IsType<NotSupportedException>(error);
      Assert.Null(promoted);
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
      Assert.Empty(fixture.Journal.Promoted);
    }
    finally
    {
      if (set) ClearMountPoint(fixture.Layout.Versions.Path);
      Directory.Delete(outside);
    }
  }

  [Fact]
  public void NativeRelativeRenameWithRetainedReparsedParentNeverWritesOutside()
  {
    using var fixture = new Fixture();
    var owned = fixture.CreateEmptyPayload();
    using var source = fixture.OpenForRename(owned);
    using var parent = fixture.OpenVersions();
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-native-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var set = false;
    Exception? renameError = null;
    try
    {
      set = TrySetMountPoint(fixture.Layout.Versions.Path, outside);
      Assert.True(set, "The test must mutate the retained destination in place.");
      renameError = Record.Exception(() => source.RenameChildTo(parent, "probe"));
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    finally
    {
      if (set) ClearMountPoint(fixture.Layout.Versions.Path);
      Directory.Delete(outside);
    }
    Assert.Equal(1921, Assert.IsType<System.ComponentModel.Win32Exception>(renameError).NativeErrorCode);
    Assert.False(Directory.Exists(Path.Combine(fixture.Layout.Versions.Path, "probe")));
    Assert.True(Directory.Exists(owned.Path));
  }

  private static bool TrySetMountPoint(string directory, string target)
  {
    var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
    var print = Encoding.Unicode.GetBytes(target);
    var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
    substitute.CopyTo(buffer, 16);
    print.CopyTo(buffer, 18 + substitute.Length);
    using var handle = OpenReparseForWrite(directory);
    return DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, 0, 0, out _, 0);
  }

  private static void ClearMountPoint(string directory)
  {
    var buffer = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
    using var handle = OpenReparseForWrite(directory);
    Assert.True(DeviceIoControl(handle, 0x000900AC, buffer, (uint)buffer.Length,
      0, 0, out _, 0), "Could not clear test mount point.");
  }

  private static SafeFileHandle OpenReparseForWrite(string path)
  {
    const uint backupSemantics = 0x02000000;
    const uint openReparsePoint = 0x00200000;
    var handle = CreateFileW(path, 0x100, 7, 0, 3, backupSemantics | openReparsePoint, 0);
    Assert.False(handle.IsInvalid, "Could not open test destination for FSCTL_SET_REPARSE_POINT.");
    return handle;
  }

  private static void CreateJunction(string link, string target)
  {
    using var process = Process.Start(new ProcessStartInfo("cmd.exe")
    {
      ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
      UseShellExecute = false,
      CreateNoWindow = true
    })!;
    process.WaitForExit();
    Assert.Equal(0, process.ExitCode);
  }

  private sealed class Journal : IPromotionJournal
  {
    public List<PromotionIntent> Intents { get; } = [];
    public List<OwnedDirectory> Promoted { get; } = [];
    public bool FailPromoted { get; set; }
    public Action<PromotionIntent>? OnIntent { get; set; }
    public void RecordPromotionIntent(PromotionIntent intent)
    {
      OnIntent?.Invoke(intent);
      Intents.Add(intent);
    }
    public void RecordPromotedDirectory(PromotionIntent intent, OwnedDirectory promoted)
    {
      if (FailPromoted) throw new IOException("journal write failed");
      Promoted.Add(promoted);
    }
  }

  private sealed class Fixture : IDisposable
  {
    private readonly RSA _key = RSA.Create(3072);
    private readonly ValidatedHttpTransport _transport;
    private readonly byte[] _archive;
    private readonly byte[] _served;
    private readonly OwnedDirectoryStore _store;
    private readonly string _root;

    public Fixture(bool serveWrongArchive = false)
    {
      _root = Path.Combine(Path.GetTempPath(), "booksplice-stage-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(_root);
      var original = Path.Combine(_root, "BookSplice");
      var data = Path.Combine(_root, "data");
      Directory.CreateDirectory(original);
      Directory.CreateDirectory(data);
      var installation = Guid.NewGuid();
      Operation = Guid.NewGuid();
      _store = new OwnedDirectoryStore(_root, _ => { });
      var managed = _store.CreateNamedExclusive(_root, ".booksplice-managed-" + installation.ToString("D"),
        "managed-root", installation, Guid.NewGuid());
      var versions = _store.CreateNamedExclusive(managed, "versions", "versions", installation, Guid.NewGuid());
      var staging = _store.CreateNamedExclusive(managed, "staging", "staging", installation, Guid.NewGuid());
      var trash = _store.CreateNamedExclusive(managed, "trash", "trash", installation, Guid.NewGuid());
      Layout = new InstallationLayout(original, data, managed, versions, staging, trash, _store);
      _archive = Archive();
      _served = serveWrongArchive ? _archive.Select((value, index) => index == 0 ? (byte)(value ^ 1) : value).ToArray() : _archive;
      const string name = "BookSplice-0.2.0-win-x64.zip";
      const string tag = "v0.2.0";
      var assets = new ReleaseAsset[]
      {
        Asset(1, name, _archive.Length), Asset(2, name + ".sha256", 85),
        Asset(3, "BookSplice-0.2.0-win-x64.update.json", 1),
        Asset(4, "BookSplice-0.2.0-win-x64.update.sig.json", 1)
      };
      Release = new ReleaseCandidate(1, tag, SemanticVersion.ParseTag(tag),
        new Uri("https://github.com/nickwolf/booksplice/releases/tag/v0.2.0"), "notes", false,
        false, assets);
      var hash = Convert.ToHexStringLower(SHA256.HashData(_archive));
      var json = JsonSerializer.Serialize(new
      {
        schemaVersion = 1,
        product = "BookSplice",
        repository = "nickwolf/booksplice",
        tag,
        version = "0.2.0",
        target = "win-x64",
        archiveName = name,
        archiveLength = _archive.Length,
        archiveSha256 = hash,
        packageProtocol = 1,
        settingsSchemaMin = 1,
        settingsSchemaMax = 1
      });
      var bytes = Encoding.UTF8.GetBytes(json);
      var domain = Encoding.ASCII.GetBytes("BookSplice update manifest v1\n");
      var signed = new byte[domain.Length + bytes.Length];
      domain.CopyTo(signed, 0);
      bytes.CopyTo(signed, domain.Length);
      var spki = _key.ExportSubjectPublicKeyInfo();
      var keyId = Convert.ToHexStringLower(SHA256.HashData(spki));
      var signature = _key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
      var envelope = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
      {
        schemaVersion = 1,
        keyId,
        algorithm = "RSA-PSS-SHA256",
        signature = Convert.ToBase64String(signature)
      }));
      Manifest = new ManifestVerifier(new Dictionary<string, byte[]> { [keyId] = spki })
        .Verify(bytes, envelope, Release, 1);
      var checksum = Encoding.ASCII.GetBytes($"{hash}  {name}\n");
      _transport = new ValidatedHttpTransport(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
          ? checksum : _served)
      }), TimeProvider.System);
      Stager = CreateStager(_ => long.MaxValue);
    }

    public Guid Operation { get; }
    public InstallationLayout Layout { get; }
    public ReleaseCandidate Release { get; }
    public VerifiedManifest Manifest { get; }
    public Journal Journal { get; } = new();
    public PackageStager Stager { get; }

    public OwnedDirectory CreateEmptyPayload() => _store.CreateNamedExclusive(Layout.Staging,
      "empty", "payload", Layout.InstallationId, Guid.NewGuid());
    public BookSplice.Updates.Windows.SafeDirectoryHandle OpenForRename(OwnedDirectory owned) =>
      _store.OpenValidatedForRename(owned, Layout.Staging);
    public BookSplice.Updates.Windows.SafeDirectoryHandle OpenVersions() =>
      _store.OpenValidatedForDestination(Layout.Versions);

    public PackageStager CreateStager(Func<string, long> available,
      Action<string>? beforeExtraction = null, Action<string>? afterRelease = null) =>
      new(new PackageDownloader(new FixedClient(Release), _transport), _store, Journal,
        new SafeZipExtractor(_store), available, 1024, () => Operation,
        beforeExtraction, afterRelease);

    private static ReleaseAsset Asset(long id, string name, long length) =>
      new(id, name, new Uri("https://github.com/nickwolf/booksplice/releases/download/v0.2.0/" + name), length);

    private static byte[] Archive()
    {
      static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
      var licenseHash = Hash("license");
      var sourceManifest = JsonSerializer.Serialize(new
      {
        schemaVersion = 2,
        provider = "BookSplice source build",
        release = "fixture",
        builderBase = "debian:fixture",
        ffmpegSha256 = Hash("ffmpeg"),
        ffprobeSha256 = Hash("ffprobe"),
        licenseSha256 = licenseHash,
        zlibLicenseSha256 = Hash("zlib"),
        sourceArchives = new[]
        {
          new { fileName = "source.tar.gz", url = "https://example.test/source", sha256 = Hash("source") }
        }
      });
      var entries = new (string Name, string Data)[]
      {
        ("BookSplice.Gui.exe", "gui"), ("booksplice.exe", "cli"), ("BookSplice.Launcher.exe", "launcher"),
        ("LICENSE", "license"), ("README.md", "readme"), ("THIRD-PARTY-NOTICES.md", "notices"),
        ("VERSION.txt", "BookSplice 0.2.0\r\nRuntime: Windows x64, self-contained .NET 10\r\nMedia tools: source-built FFmpeg fixture, LGPL 2.1 or later; zlib 1.3.2\r\nMedia license SHA-256: " + licenseHash + "\r\n"),
        ("licenses/FFmpeg-LICENSE.txt", "license"), ("licenses/FFmpeg-components.md", "components"),
        ("licenses/LGPL-2.1.txt", "lgpl"), ("licenses/zlib-LICENSE.txt", "zlib"),
        ("tools/ffmpeg/ffmpeg.exe", "ffmpeg"), ("tools/ffmpeg/ffprobe.exe", "ffprobe"),
        ("sources/manifest.json", sourceManifest), ("sources/BUILDING.md", "building"),
        ("sources/Dockerfile", "docker"), ("sources/build-minimal.sh", "build"),
        ("sources/Build-MediaTools.ps1", "powershell"), ("sources/source.tar.gz", "source")
      };
      using var stream = new MemoryStream();
      using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        foreach (var (name, data) in entries)
        {
          using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
          writer.Write(data);
        }
      return stream.ToArray();
    }

    public void Dispose()
    {
      _transport.Dispose();
      _key.Dispose();
      Directory.Delete(_root, true);
    }
  }

  private sealed class FixedClient(ReleaseCandidate candidate) : IReleaseClient
  {
    public Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<ReleaseCandidate>>([candidate]);
  }

  private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
      CancellationToken cancellationToken) => Task.FromResult(response(request));
  }
}
