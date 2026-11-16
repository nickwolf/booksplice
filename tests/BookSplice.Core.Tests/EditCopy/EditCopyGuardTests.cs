using System.Diagnostics;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;

namespace BookSplice.Core.Tests.EditCopy;

public sealed class EditCopyGuardTests
{
  private const string Source = @"C:\Books\Original.m4b";
  private const string Destination = @"C:\Output\Edited.m4b";

  [Fact]
  public void AValidRequestPasses()
  {
    var files = new FakeFileSystem().WithFile(Source, "1:1");

    Assert.Empty(EditCopyGuard.Check(Request(), files));
  }

  [Fact]
  public void AMissingSourceIsRejected()
  {
    var diagnostics = EditCopyGuard.Check(Request(), new FakeFileSystem());

    Assert.Equal(EditCopyCodes.SourceMissing, Assert.Single(diagnostics).Code);
  }

  [Fact]
  public void ASourceThatIsNotAnM4bIsRejected()
  {
    const string source = @"C:\Books\Original.m4a";
    var files = new FakeFileSystem().WithFile(source, "1:1");

    var diagnostics = EditCopyGuard.Check(Request(source: source), files);

    Assert.Equal(EditCopyCodes.SourceNotM4b, Assert.Single(diagnostics).Code);
  }

  [Fact]
  public void ARequestWithoutEditsIsRejected()
  {
    var files = new FakeFileSystem().WithFile(Source, "1:1");

    var diagnostics = EditCopyGuard.Check(Request(tags: [], chapters: []), files);

    Assert.Equal(EditCopyCodes.NoEdits, Assert.Single(diagnostics).Code);
  }

  [Theory]
  [InlineData(-1, "Title", EditCopyCodes.ChapterIndexInvalid)]
  [InlineData(0, "", EditCopyCodes.ChapterTitleInvalid)]
  [InlineData(0, "   ", EditCopyCodes.ChapterTitleInvalid)]
  [InlineData(0, "Bad\0title", EditCopyCodes.ChapterTitleInvalid)]
  public void AnInvalidChapterEditIsRejected(int index, string title, string code)
  {
    var files = new FakeFileSystem().WithFile(Source, "1:1");

    var diagnostics = EditCopyGuard.Check(Request(chapters: new Dictionary<int, string> { [index] = title }), files);

    Assert.Equal(code, Assert.Single(diagnostics).Code);
  }

  [Fact]
  public void ADestinationThatIsAFolderIsRejected()
  {
    var files = new FakeFileSystem().WithFile(Source, "1:1").WithDirectory(Destination);

    var diagnostics = EditCopyGuard.Check(Request(), files);

    Assert.Equal(EditCopyCodes.DestinationInvalid, Assert.Single(diagnostics).Code);
  }

  [Theory]
  [InlineData(@"C:\Books\Original.m4b")]
  [InlineData(@"c:\BOOKS\original.M4B")]
  [InlineData(@"C:\Books\.\Other\..\Original.m4b")]
  public void TheSourcePathIsRejectedAsTheDestinationEvenWithoutReadingIds(string destination)
  {
    var files = new FakeFileSystem().WithFile(Source, null);

    var diagnostics = EditCopyGuard.Check(Request(destination: destination), files);

    Assert.Equal(EditCopyCodes.DestinationIsSource, Assert.Single(diagnostics).Code);
  }

  [Fact]
  public void AnExistingDestinationWithTheSourcesPhysicalIdIsRejected()
  {
    var files = new FakeFileSystem().WithFile(Source, "AB:12").WithFile(Destination, "ab:12");

    var diagnostics = EditCopyGuard.Check(Request(), files);

    Assert.Equal(EditCopyCodes.DestinationIsSource, Assert.Single(diagnostics).Code);
  }

  [Fact]
  public void AnExistingDestinationWithAnotherPhysicalIdIsAccepted()
  {
    var files = new FakeFileSystem().WithFile(Source, "AB:12").WithFile(Destination, "AB:13");

    Assert.Empty(EditCopyGuard.Check(Request(), files));
  }

  [Fact]
  public void AnExistingDestinationWhoseIdCannotBeReadIsRejected()
  {
    var files = new FakeFileSystem().WithFile(Source, "AB:12").WithFile(Destination, null);

    var diagnostics = EditCopyGuard.Check(Request(), files);

    Assert.Equal(EditCopyCodes.DestinationIsSource, Assert.Single(diagnostics).Code);
  }

  [Fact]
  public void ADestinationInsideAJunctionIsAccepted()
  {
    using var root = new TemporaryDirectory();
    var source = root.CreateFile(Path.Combine("books", "Original.m4b"));
    var target = Directory.CreateDirectory(root.GetPath("output")).FullName;
    var junction = root.GetPath("linked-output");
    CreateJunction(junction, target);

    var diagnostics = EditCopyGuard.Check(Request(source: source, destination: Path.Combine(junction, "Edited.m4b")), new FileSystemEditCopyFileSystem());

    Assert.Empty(diagnostics);
  }

  [Fact]
  public void TheSourceReachedThroughAJunctionIsRejectedByItsPhysicalId()
  {
    using var root = new TemporaryDirectory();
    var source = root.CreateFile(Path.Combine("books", "Original.m4b"));
    var junction = root.GetPath("linked-books");
    CreateJunction(junction, Path.GetDirectoryName(source)!);

    var diagnostics = EditCopyGuard.Check(Request(source: source, destination: Path.Combine(junction, "Original.m4b")), new FileSystemEditCopyFileSystem());

    Assert.Equal(EditCopyCodes.DestinationIsSource, Assert.Single(diagnostics).Code);
  }

  private static EditCopyRequest Request(string source = Source, string destination = Destination, Dictionary<SemanticField, string>? tags = null, Dictionary<int, string>? chapters = null)
    => new(source, tags ?? new() { [SemanticField.BookTitle] = "New title" }, chapters ?? [], destination, MetadataProfiles.GenericMp4);

  private static void CreateJunction(string link, string target)
  {
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
    process.WaitForExit();
    Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
  }

  private sealed class FakeFileSystem : IEditCopyFileSystem
  {
    private readonly Dictionary<string, string?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public FakeFileSystem WithFile(string path, string? id) { _files[Path.GetFullPath(path)] = id; return this; }
    public FakeFileSystem WithDirectory(string path) { _directories.Add(Path.GetFullPath(path)); return this; }
    public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));
    public bool DirectoryExists(string path) => _directories.Contains(Path.GetFullPath(path));
    public string? GetPhysicalId(string path) => _files.GetValueOrDefault(Path.GetFullPath(path));
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("booksplice-edit-guard-").FullName;
    public string Path { get; }
    public string GetPath(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public string CreateFile(string name)
    {
      var path = GetPath(name);
      Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
      File.WriteAllText(path, "media");
      return path;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}
