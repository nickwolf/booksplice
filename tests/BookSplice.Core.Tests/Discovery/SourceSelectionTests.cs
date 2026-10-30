using System.Diagnostics;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;

namespace BookSplice.Core.Tests.Discovery;

public sealed class SourceSelectionTests : IDisposable
{
  private readonly SelectionTree _tree = new();

  [Fact]
  public void Create_uses_the_deepest_common_ancestor_and_unique_ids_for_repeated_names()
  {
    var selection = SourceSelection.Create([_tree.Write("book/Disc2/01.mp3"), _tree.Write("book/Disc1/01.mp3")]);

    Assert.True(selection.SharesRoot);
    Assert.Equal(Path.Combine(_tree.Root, "book"), selection.RootPath);
    Assert.Equal("book", selection.Name);
    Assert.Equal(["Disc2/01.mp3", "Disc1/01.mp3"], selection.Paths.Select(selection.FileId));
  }

  [Fact]
  public void Create_drops_repeated_paths_and_equality_ignores_order_and_case()
  {
    var first = _tree.Write("book/01.mp3");
    var second = _tree.Write("book/02.mp3");

    var selection = SourceSelection.Create([first, second, first.ToUpperInvariant()]);

    Assert.Equal(2, selection.Paths.Count);
    Assert.Equal(selection, SourceSelection.Create([second, first]));
    Assert.NotEqual(selection, SourceSelection.Create([first]));
  }

  [Fact]
  public void FileId_is_the_full_path_without_a_shared_volume_root()
  {
    var selection = SourceSelection.Create([@"C:\one\01.mp3", @"D:\two\01.mp3"]);

    Assert.False(selection.SharesRoot);
    Assert.Equal(@"C:\one\01.mp3", selection.FileId(@"C:\one\01.mp3"));
    Assert.Equal(@"D:\two\01.mp3", selection.FileId(@"D:\two\01.mp3"));
    Assert.Equal(@"C:\one", selection.RootPath);
  }

  [Fact]
  public async Task Discover_reads_only_the_selected_files_and_never_their_siblings()
  {
    var selected = new[] { _tree.Write("book/01.mp3"), _tree.Write("book/03.mp3") };
    _tree.Write("book/02.mp3");
    _tree.Write("book/sub/04.mp3");
    var before = selected.Select(SelectionTree.Hash).ToArray();
    var probe = new RecordingProbe();

    var result = await new SourceDiscoverer(probe, 2).DiscoverAsync(SourceSelection.Create(selected), CancellationToken.None);

    Assert.Equal(selected.Order(StringComparer.Ordinal), probe.Inputs.Order(StringComparer.Ordinal));
    Assert.Equal(selected.Select(x => x + "|").Order(StringComparer.Ordinal), probe.Inputs.Select(x => x + "|").Order(StringComparer.Ordinal));
    Assert.Equal(before, selected.Select(SelectionTree.Hash));
  }

  [Fact]
  public async Task Discover_keeps_same_named_files_from_different_folders_apart()
  {
    var selection = SourceSelection.Create([_tree.Write("book/Disc1/01.mp3"), _tree.Write("book/Disc2/01.mp3")]);

    var result = await new SourceDiscoverer(new RecordingProbe(), 2).DiscoverAsync(selection, CancellationToken.None);

    Assert.Equal(["Disc1/01.mp3", "Disc2/01.mp3"], result.Files.Select(file => file.RelativePath));
    Assert.Empty(result.Errors);
  }

  [Fact]
  public async Task Discover_probes_a_repeated_selection_and_a_hard_link_once()
  {
    var first = _tree.Write("book/01.mp3");
    var link = Path.Combine(_tree.Root, "book", "02.mp3");
    await CreateHardLinkAsync(link, first);
    var probe = new RecordingProbe();

    var result = await new SourceDiscoverer(probe, 2).DiscoverAsync(SourceSelection.Create([first, first, link]), CancellationToken.None);

    var file = Assert.Single(result.Files);
    Assert.Equal("01.mp3", file.RelativePath);
    Assert.Single(probe.Inputs);
  }

  [Fact]
  public async Task Discover_reports_missing_and_unsupported_selections()
  {
    var good = _tree.Write("book/01.mp3");
    var text = _tree.Write("book/notes.txt");

    var result = await new SourceDiscoverer(new RecordingProbe(), 2).DiscoverAsync(SourceSelection.Create([good, text, Path.Combine(_tree.Root, "book", "gone.mp3")]), CancellationToken.None);

    Assert.Single(result.Files);
    Assert.Contains(result.Errors, error => error.Code == "discovery.input-unsupported");
    Assert.Contains(result.Errors, error => error.Code == "discovery.input-unreadable");
  }

  [Fact]
  public async Task Discover_skips_a_selected_file_behind_a_reparse_point()
  {
    var real = _tree.Write("real/01.mp3");
    var link = Path.Combine(_tree.Root, "book", "linked");
    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
    await RunCmdAsync($"mklink /J \"{link}\" \"{Path.GetDirectoryName(real)}\"");
    try
    {
      var kept = _tree.Write("book/02.mp3");
      var result = await new SourceDiscoverer(new RecordingProbe(), 2).DiscoverAsync(SourceSelection.Create([Path.Combine(link, "01.mp3"), kept]), CancellationToken.None);

      Assert.Equal(["02.mp3"], result.Files.Select(file => file.RelativePath));
      Assert.Contains(result.Warnings, warning => warning.Code == "discovery.reparse-point-skipped");
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }
  }

  [Fact]
  public async Task Analyze_does_not_read_covers_from_a_directory_whose_file_was_refused()
  {
    var real = _tree.Write("real/01.mp3");
    _tree.Write("real/cover.jpg", SelectionTree.TinyJpeg(30, 30));
    var link = Path.Combine(_tree.Root, "book", "linked");
    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
    await RunCmdAsync($"mklink /J \"{link}\" \"{Path.GetDirectoryName(real)}\"");
    try
    {
      var kept = new[] { _tree.Write("book/Disc1/02.mp3"), _tree.Write("book/Disc2/03.mp3") };

      var analysis = await SelectionTree.Analyzer().AnalyzeAsync(SourceSelection.Create(kept.Prepend(Path.Combine(link, "01.mp3"))), false);

      Assert.Equal(2, analysis.OrderedFiles.Count);
      Assert.Empty(analysis.Cover.Candidates);
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }
  }

  [Fact]
  public async Task Discover_stops_when_cancelled_before_probing()
  {
    var selection = SourceSelection.Create([_tree.Write("book/01.mp3")]);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SourceDiscoverer(new RecordingProbe(), 2).DiscoverAsync(selection, cancellation.Token));
  }

  [Fact]
  public async Task Discover_records_an_identity_that_changes_with_content_and_timestamp()
  {
    var path = _tree.Write("book/01.mp3");
    var discoverer = new SourceDiscoverer(new RecordingProbe(), 2);
    var selection = SourceSelection.Create([path]);
    var original = (await discoverer.DiscoverAsync(selection, CancellationToken.None)).Files[0].Identity!;

    Assert.True(original.Matches((await discoverer.DiscoverAsync(selection, CancellationToken.None)).Files[0].Identity!));
    File.SetLastWriteTimeUtc(path, original.LastWriteUtc.AddMinutes(5));
    Assert.False(original.Matches((await discoverer.DiscoverAsync(selection, CancellationToken.None)).Files[0].Identity!));
    File.AppendAllText(path, "more");
    Assert.NotEqual(original.Length, (await discoverer.DiscoverAsync(selection, CancellationToken.None)).Files[0].Identity!.Length);
  }

  [Fact]
  public async Task Analyze_covers_only_the_selected_files_and_finds_the_parent_cover_for_disc_folders()
  {
    var selected = new[] { _tree.Write("book/Disc1/01.mp3"), _tree.Write("book/Disc2/01.mp3") };
    _tree.Write("book/Disc3/01.mp3");
    _tree.Write("book/cover.jpg", SelectionTree.TinyJpeg(200, 300));
    _tree.Write("book/Disc3/other.jpg", SelectionTree.TinyJpeg(900, 900));
    _tree.Write("book/Disc1/art.jpg", SelectionTree.TinyJpeg(50, 60));

    var analysis = await SelectionTree.Analyzer().AnalyzeAsync(SourceSelection.Create(selected), false);

    Assert.Equal(BookAnalysisStatus.Ready, analysis.Status);
    Assert.Equal(["Disc1/01.mp3", "Disc2/01.mp3"], analysis.OrderedFiles.Select(file => file.RelativePath));
    Assert.Equal("book", analysis.SourceRootName);
    Assert.Equal(Path.Combine(_tree.Root, "book", "cover.jpg"), analysis.Cover.Selected!.SourcePath);
    Assert.True(analysis.Cover.Selected.IsCanonicalRootFile);
    Assert.Equal(2, analysis.Cover.Candidates.Count);
    Assert.DoesNotContain(analysis.Cover.Candidates, cover => cover.SourcePath.Contains("Disc3"));
  }

  [Fact]
  public async Task Analyze_one_folder_selection_keeps_the_recursive_root_search_of_a_single_file()
  {
    var selected = new[] { _tree.Write("book/01.mp3"), _tree.Write("book/02.mp3") };
    _tree.Write("book/art/cover.jpg", SelectionTree.TinyJpeg(10, 10));

    var analysis = await SelectionTree.Analyzer().AnalyzeAsync(SourceSelection.Create(selected), false);

    Assert.Equal(Path.Combine(_tree.Root, "book"), analysis.SourceRootPath);
    Assert.Contains(analysis.Cover.Candidates, cover => cover.SourcePath.EndsWith("cover.jpg", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Analyze_a_folder_path_still_reads_the_whole_folder()
  {
    _tree.Write("book/01.mp3");
    _tree.Write("book/Disc2/02.mp3");

    var analysis = await SelectionTree.Analyzer().AnalyzeAsync(Path.Combine(_tree.Root, "book"), false);

    Assert.Equal(["01.mp3", "Disc2/02.mp3"], analysis.OrderedFiles.Select(file => file.RelativePath));
  }

  public void Dispose() => _tree.Dispose();

  private static Task CreateHardLinkAsync(string linkPath, string targetPath) => RunCmdAsync($"mklink /H \"{linkPath}\" \"{targetPath}\"");

  private static async Task RunCmdAsync(string command)
  {
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}") { CreateNoWindow = true, RedirectStandardError = true, UseShellExecute = false })!;
    await process.WaitForExitAsync();
    Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
  }
}
