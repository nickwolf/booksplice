using System.IO.Compression;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;
using BookSplice.Updates.Storage;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Tests.Storage;

[CollectionDefinition("WindowsOwnedStorage", DisableParallelization = true)]
public sealed class WindowsOwnedStorageGroup;

[Collection("WindowsOwnedStorage")]
public sealed class SafeZipTests
{
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
    nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

  private static int TryOpenForDataWrite(string path)
  {
    using var handle = CreateFileW(path, 0x2, 7, 0, 3, 0x02000000, 0);
    return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
  }

  private static (string Root, OwnedDirectoryStore Store, OwnedDirectory Destination) CreateDestination()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-zip-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var store = new OwnedDirectoryStore(root, _ => { });
    return (root, store, store.CreateExclusive(root, "staging", Guid.NewGuid(), Guid.NewGuid()));
  }

  private static MemoryStream Archive(params (string Name, string Data)[] entries)
  {
    var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
      foreach (var (name, data) in entries)
      {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(data);
      }
    stream.Position = 0;
    return stream;
  }

  [Theory]
  [InlineData("../escape")]
  [InlineData("..\\escape")]
  [InlineData("/absolute")]
  [InlineData("C:/drive")]
  [InlineData("//server/file")]
  [InlineData("name:stream")]
  [InlineData("CON.txt")]
  [InlineData("bad./file")]
  [InlineData("a</file")]
  public async Task LateUnsafeEntryDoesNotWriteEarlierFiles(string unsafeName)
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = Archive(("first.txt", "first"), (unsafeName, "bad"));
      var extractor = new SafeZipExtractor(fixture.Store);
      await Assert.ThrowsAsync<PackageException>(() => extractor.ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task InventoryDetectsChangedByteAndAddedFile()
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = Archive(("nested/one.txt", "one"));
      using var inventory = await new SafeZipExtractor(fixture.Store).ExtractAsync(archive, fixture.Destination, CancellationToken.None);
      inventory.Revalidate();
      Assert.Single(inventory.Files);
      Assert.Equal("nested/one.txt", inventory.Files[0].RelativePath);
      Assert.ThrowsAny<IOException>(() => File.WriteAllText(Path.Combine(fixture.Destination.Path, "nested", "one.txt"), "two"));
      File.WriteAllText(Path.Combine(fixture.Destination.Path, "nested", "two.txt"), "two");
      Assert.Throws<PackageException>(() => inventory.Revalidate());
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Theory]
  [InlineData("A.txt", "a.TXT")]
  [InlineData("folder", "folder/file.txt")]
  [InlineData("folder/file.txt", "folder")]
  [InlineData("one.txt", "one.txt")]
  public async Task CollisionsDoNotWrite(string first, string second)
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = Archive((first, "a"), (second, "b"));
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task ActualFileLimitIsEnforced()
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = Archive(("one.txt", "12345"));
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store, 1, 4, 4)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Equal(20_000, UpdateLimits.MaxArchiveEntries);
      Assert.Equal(1L << 30, UpdateLimits.MaxExtractedFileBytes);
      Assert.Equal(4L << 30, UpdateLimits.MaxExtractedTotalBytes);
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task ExactLimitsPass()
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = Archive(("one.txt", "1234"));
      using var inventory = await new SafeZipExtractor(fixture.Store, 1, 4, 4)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None);
      inventory.Revalidate();
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task StreamedOverrunIsRefusedEvenWhenCentralDirectoryUnderstatesLength()
  {
    var fixture = CreateDestination();
    try
    {
      using var original = Archive(("one.txt", "12345"));
      var bytes = original.ToArray();
      var central = Enumerable.Range(0, bytes.Length - 4).FirstOrDefault(index =>
        bytes[index] == 0x50 && bytes[index + 1] == 0x4b && bytes[index + 2] == 0x01 && bytes[index + 3] == 0x02);
      Assert.True(central > 0);
      bytes[central + 24] = 4;
      using var archive = new MemoryStream(bytes);
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store, 1, 4, 4)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task ImpliedDirectoriesCountAgainstEntryBudget()
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = Archive(("one/two/three.txt", "x"));
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store, 2, 10, 10)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task UnixLinkEntryIsRejectedBeforeWriting()
  {
    var fixture = CreateDestination();
    try
    {
      var archive = new MemoryStream();
      using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
      {
        using (var writer = new StreamWriter(zip.CreateEntry("first.txt").Open())) writer.Write("first");
        var link = zip.CreateEntry("link");
        link.ExternalAttributes = unchecked((int)0xA0000000);
      }
      archive.Position = 0;
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
      archive.Dispose();
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task WindowsReparseEntryIsRejectedBeforeWriting()
  {
    var fixture = CreateDestination();
    try
    {
      var archive = new MemoryStream();
      using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
      {
        using (var writer = new StreamWriter(zip.CreateEntry("first.txt").Open())) writer.Write("first");
        var reparse = zip.CreateEntry("reparse");
        reparse.ExternalAttributes = 0x04000000;
      }
      archive.Position = 0;
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
      archive.Dispose();
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Theory]
  [InlineData(0x400)]
  [InlineData(0x10)]
  public async Task DosSpecialAttributesAreRejectedBeforeWriting(int attributes)
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = new MemoryStream();
      using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
      {
        using (var writer = new StreamWriter(zip.CreateEntry("first.txt").Open())) writer.Write("first");
        var invalid = zip.CreateEntry("invalid.txt");
        invalid.ExternalAttributes = attributes;
      }
      archive.Position = 0;
      var error = await Record.ExceptionAsync(async () =>
      {
        using var inventory = await new SafeZipExtractor(fixture.Store).ExtractAsync(archive,
          fixture.Destination, CancellationToken.None);
      });
      Assert.IsType<PackageException>(error);
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task UnixRegularFileModeWithGroupWriteIsAccepted()
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = new MemoryStream();
      using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
      {
        var regular = zip.CreateEntry("regular.txt");
        regular.ExternalAttributes = unchecked((int)0x81B40000);
        using var writer = new StreamWriter(regular.Open());
        writer.Write("regular");
      }
      archive.Position = 0;
      using var inventory = await new SafeZipExtractor(fixture.Store).ExtractAsync(archive,
        fixture.Destination, CancellationToken.None);
      inventory.Revalidate();
      Assert.Single(inventory.Files);
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task RegularModeHardLinkExtraIsRejectedBeforeWriting(bool localExtra)
  {
    var fixture = CreateDestination();
    try
    {
      using var original = Archive(("first.txt", "first"), ("linked.txt", "linked"));
      var bytes = AddHardLinkExtra(original.ToArray(), localExtra);
      using var archive = new MemoryStream(bytes);
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  private static byte[] AddHardLinkExtra(byte[] bytes, bool localExtra)
  {
    var central = FindSignature(bytes, 0x50, 0x4b, 0x01, 0x02);
    var end = FindSignature(bytes, 0x50, 0x4b, 0x05, 0x06);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 38), 0x81B40000);
    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 28));
    var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42)));
    var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26));
    var extra = new byte[20];
    BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x000d);
    BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), 16);
    Encoding.ASCII.GetBytes("first.txt").CopyTo(extra, 4);
    var insertAt = localExtra ? local + 30 + localNameLength : central + 46 + nameLength;
    if (localExtra) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 28), 20);
    else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 30), 20);
    var modified = bytes.ToList();
    modified.InsertRange(insertAt, extra);
    bytes = modified.ToArray();
    end += extra.Length;
    if (localExtra)
    {
      var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), centralOffset + 20);
    }
    else
    {
      var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 12));
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), centralSize + 20);
    }
    return bytes;
  }

  [Fact]
  public async Task AlreadyCanceledEmptyArchiveDoesNotOpenDestination()
  {
    var fixture = CreateDestination();
    try
    {
      using var archive = Archive();
      using var cancellation = new CancellationTokenSource();
      cancellation.Cancel();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, cancellation.Token));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task MisleadingSmallEndRecordCountIsRejectedBeforeWriting()
  {
    var fixture = CreateDestination();
    try
    {
      using var original = Archive(("one", "1"), ("two", "2"), ("three", "3"));
      var bytes = original.ToArray();
      var end = FindSignature(bytes, 0x50, 0x4b, 0x05, 0x06);
      bytes[end + 8] = 2;
      bytes[end + 10] = 2;
      using var archive = new MemoryStream(bytes);
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store, 2, 10, 30)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task EndRecordSignatureInsideCommentIsRejected()
  {
    var fixture = CreateDestination();
    try
    {
      using var original = Archive(("one", "1"));
      var bytes = original.ToArray();
      var end = FindSignature(bytes, 0x50, 0x4b, 0x05, 0x06);
      bytes[end + 20] = 4;
      Array.Resize(ref bytes, bytes.Length + 4);
      bytes[^4] = 0x50;
      bytes[^3] = 0x4b;
      bytes[^2] = 0x05;
      bytes[^1] = 0x06;
      using var archive = new MemoryStream(bytes);
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task Zip64SentinelIsRefused()
  {
    var fixture = CreateDestination();
    try
    {
      using var original = Archive(("one", "1"));
      var bytes = original.ToArray();
      var end = FindSignature(bytes, 0x50, 0x4b, 0x05, 0x06);
      bytes[end + 8] = bytes[end + 9] = bytes[end + 10] = bytes[end + 11] = 0xff;
      using var archive = new MemoryStream(bytes);
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  private static int FindSignature(byte[] bytes, byte first, byte second, byte third, byte fourth) =>
    Enumerable.Range(0, bytes.Length - 3).Last(index => bytes[index] == first && bytes[index + 1] == second &&
      bytes[index + 2] == third && bytes[index + 3] == fourth);

  [Fact]
  public async Task NonemptyDestinationIsRefused()
  {
    var fixture = CreateDestination();
    try
    {
      File.WriteAllText(Path.Combine(fixture.Destination.Path, "sentinel"), "keep");
      using var archive = Archive(("one.txt", "one"));
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Destination.Path, "sentinel")));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task ReplacedOwnedDestinationIsRefused()
  {
    var fixture = CreateDestination();
    var old = fixture.Destination.Path + "-old";
    try
    {
      Directory.Move(fixture.Destination.Path, old);
      Directory.CreateDirectory(fixture.Destination.Path);
      using var archive = Archive(("one.txt", "one"));
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination.Path));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task PreexistingNestedJunctionIsRefusedWithoutFollowingIt()
  {
    var fixture = CreateDestination();
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-zip-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var link = Path.Combine(fixture.Destination.Path, "nested");
    try
    {
      CreateJunction(link, outside);
      using var archive = Archive(("nested/one.txt", "one"));
      await Assert.ThrowsAsync<PackageException>(() => new SafeZipExtractor(fixture.Store)
        .ExtractAsync(archive, fixture.Destination, CancellationToken.None));
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    finally
    {
      if (Directory.Exists(link)) Directory.Delete(link);
      Directory.Delete(fixture.Root, true);
      Directory.Delete(outside);
    }
  }

  [Fact]
  public async Task AddedJunctionInvalidatesInventoryWithoutTraversal()
  {
    var fixture = CreateDestination();
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-zip-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var link = Path.Combine(fixture.Destination.Path, "extra");
    try
    {
      using var archive = Archive(("one.txt", "one"));
      using var inventory = await new SafeZipExtractor(fixture.Store).ExtractAsync(archive,
        fixture.Destination, CancellationToken.None);
      CreateJunction(link, outside);
      Assert.Throws<PackageException>(() => inventory.Revalidate());
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    finally
    {
      if (Directory.Exists(link)) Directory.Delete(link);
      Directory.Delete(fixture.Root, true);
      Directory.Delete(outside);
    }
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

  [Fact]
  public async Task FileCannotBeReplacedAtInventoryHandoff()
  {
    var fixture = CreateDestination();
    try
    {
      var checkedHandoff = false;
      using var archive = Archive(("one.txt", "one"));
      var extractor = new SafeZipExtractor(fixture.Store, 1, 10, 10, path =>
      {
        checkedHandoff = true;
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "replacement"));
      });
      using var inventory = await extractor.ExtractAsync(archive, fixture.Destination, CancellationToken.None);
      Assert.True(checkedHandoff);
      inventory.Revalidate();
      Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("one"))), inventory.Files[0].Sha256);
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task NewlyCreatedNestedDirectoryDeniesWriteOpenBeforePayload()
  {
    var fixture = CreateDestination();
    try
    {
      var checkedGuard = false;
      using var archive = Archive(("nested/one.txt", "one"));
      var extractor = new SafeZipExtractor(fixture.Store, 3, 10, 10, null, path =>
      {
        checkedGuard = true;
        Assert.Equal(32, TryOpenForDataWrite(Path.Combine(path, "nested")));
      });
      using var inventory = await extractor.ExtractAsync(archive, fixture.Destination, CancellationToken.None);
      Assert.True(checkedGuard);
      inventory.Revalidate();
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task SameFileTamperDuringBridgeDoesNotBecomeInventoryBaseline()
  {
    var fixture = CreateDestination();
    try
    {
      var tampered = false;
      using var archive = Archive(("one.txt", "one"));
      var extractor = new SafeZipExtractor(fixture.Store, 1, 10, 10, null, null, path =>
      {
        File.WriteAllText(path, "evil");
        tampered = true;
      });
      await Assert.ThrowsAsync<PackageException>(() => extractor.ExtractAsync(archive, fixture.Destination,
        CancellationToken.None));
      Assert.True(tampered);
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Fact]
  public async Task CancellationAfterWritingRetainsOwnedDestinationForCleanup()
  {
    var fixture = CreateDestination();
    try
    {
      using var cancellation = new CancellationTokenSource();
      using var archive = Archive(("one.txt", "one"));
      var extractor = new SafeZipExtractor(fixture.Store, 1, 10, 10, _ => cancellation.Cancel());
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(archive,
        fixture.Destination, cancellation.Token));
      Assert.True(Directory.Exists(fixture.Destination.Path));
      Assert.True(File.Exists(Path.Combine(fixture.Destination.Path, "one.txt")));
      using var held = fixture.Store.OpenValidated(fixture.Destination);
      Assert.Equal(fixture.Destination.Identity, held.Identity);
    }
    finally { Directory.Delete(fixture.Root, true); }
  }

  [Theory]
  [InlineData("valid")]
  [InlineData("missing-launcher")]
  [InlineData("bad-tool")]
  [InlineData("bad-license")]
  [InlineData("bad-source")]
  [InlineData("bad-schema")]
  [InlineData("duplicate-property")]
  [InlineData("wrong-type")]
  [InlineData("bad-version")]
  [InlineData("flavor-line")]
  [InlineData("bad-flavor-line")]
  [InlineData("doubled-carriage-return")]
  public async Task FlatPackageLayoutAndHashesAreValidated(string scenario)
  {
    var fixture = CreateDestination();
    try
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
        sourceArchives = new[] { new { fileName = "source.tar.gz", url = "https://example.test/source", sha256 = Hash("source") } }
      });
      sourceManifest = scenario switch
      {
        "bad-schema" => sourceManifest.Replace("\"schemaVersion\":2", "\"schemaVersion\":3", StringComparison.Ordinal),
        "duplicate-property" => sourceManifest.Replace("\"schemaVersion\":2",
          "\"schemaVersion\":2,\"schemaVersion\":2", StringComparison.Ordinal),
        "wrong-type" => sourceManifest.Replace("\"schemaVersion\":2",
          "\"schemaVersion\":\"two\"", StringComparison.Ordinal),
        _ => sourceManifest
      };
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
      entries = scenario switch
      {
        "missing-launcher" => entries.Where(entry => entry.Name != "BookSplice.Launcher.exe").ToArray(),
        "bad-tool" => entries.Select(entry => entry.Name == "tools/ffmpeg/ffmpeg.exe" ?
          (entry.Name, "wrong") : entry).ToArray(),
        "bad-license" => entries.Select(entry => entry.Name == "licenses/FFmpeg-LICENSE.txt" ?
          (entry.Name, "wrong") : entry).ToArray(),
        "bad-source" => entries.Select(entry => entry.Name == "sources/source.tar.gz" ?
          (entry.Name, "wrong") : entry).ToArray(),
        "bad-version" => entries.Select(entry => entry.Name == "VERSION.txt" ?
          (entry.Name, entry.Data.Replace("BookSplice 0.2.0", "BookSplice 0.2.1", StringComparison.Ordinal)) : entry).ToArray(),
        "flavor-line" or "bad-flavor-line" => entries.Select(entry => entry.Name == "VERSION.txt" ?
          (entry.Name, entry.Data + (scenario == "flavor-line" ? "Build flavor: Test\r\n" : "Build flavor: Other\r\n")) : entry).ToArray(),
        "doubled-carriage-return" => entries.Select(entry => entry.Name == "VERSION.txt" ?
          (entry.Name, entry.Data.Replace("\r\n", "\r\r\n", StringComparison.Ordinal)) : entry).ToArray(),
        _ => entries
      };
      using var archive = Archive(entries);
      using var inventory = await new SafeZipExtractor(fixture.Store).ExtractAsync(archive, fixture.Destination,
        CancellationToken.None);
      var manifest = new VerifiedManifest(1, "BookSplice", "nickwolf/booksplice", "v0.2.0", "0.2.0",
        "win-x64", "BookSplice-0.2.0-win-x64.zip", archive.Length, "", 1, 1, 1,
        new ReleaseIdentity(1, "v0.2.0", new Uri("https://example.test"), "", []));
      if (scenario is "valid" or "flavor-line") inventory.ValidatePackage(manifest);
      else Assert.Throws<PackageException>(() => inventory.ValidatePackage(manifest));
    }
    finally { Directory.Delete(fixture.Root, true); }
  }
}
