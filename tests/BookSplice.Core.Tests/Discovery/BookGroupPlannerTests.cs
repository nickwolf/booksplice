using System.Diagnostics;
using BookSplice.Core.Discovery;

namespace BookSplice.Core.Tests.Discovery;

public sealed class BookGroupPlannerTests : IDisposable
{
  private readonly SelectionTree _tree = new();

  [Theory]
  [InlineData("Disc 1")]
  [InlineData("Disc1")]
  [InlineData("disk 2")]
  [InlineData("CD2")]
  [InlineData("cd 2")]
  [InlineData("Part 3")]
  [InlineData("PART_03")]
  [InlineData("1")]
  [InlineData("01")]
  public void Disc_folder_names_are_recognised(string name) => Assert.True(BookGroupPlanner.IsDiscFolderName(name));

  [Theory]
  [InlineData("Discworld")]
  [InlineData("Disc")]
  [InlineData("Part Time Job")]
  [InlineData("1984")]
  [InlineData("Book 1")]
  [InlineData("")]
  public void Other_folder_names_are_not_disc_folders(string name) => Assert.False(BookGroupPlanner.IsDiscFolderName(name));

  [Fact]
  public void Disc_subfolders_merge_into_their_book_and_sibling_books_stay_separate()
  {
    var a1 = _tree.Write("parent/Book A/Disc 1/01.mp3");
    var a2 = _tree.Write("parent/Book A/Disc 2/01.mp3");
    var b1 = _tree.Write("parent/Book B/CD1/01.mp3");
    var b2 = _tree.Write("parent/Book B/CD2/01.mp3");
    var c1 = _tree.Write("parent/Book C/1/01.mp3");
    var c2 = _tree.Write("parent/Book C/2/01.mp3");
    var d = _tree.Write("parent/Book D/Part 1/01.mp3");

    var plan = Plan("parent");

    Assert.Equal(["Book A", "Book B", "Book C", "Book D"], plan.Groups.Select(group => group.Name));
    Assert.Equal([a1, a2], plan.Groups[0].Files);
    Assert.Equal([b1, b2], plan.Groups[1].Files);
    Assert.Equal([c1, c2], plan.Groups[2].Files);
    Assert.Equal([d], plan.Groups[3].Files);
    Assert.Empty(plan.NotInspected);
  }

  [Fact]
  public void A_series_folder_without_audio_is_not_a_book_but_its_books_are()
  {
    var one = _tree.Write("parent/Series/Book One/01.mp3");
    var two = _tree.Write("parent/Series/Book Two/Disc 1/01.mp3");

    var plan = Plan("parent");

    Assert.Equal(["Book One", "Book Two"], plan.Groups.Select(group => group.Name));
    Assert.Equal([one], plan.Groups[0].Files);
    Assert.Equal([two], plan.Groups[1].Files);
  }

  [Fact]
  public void A_book_folder_with_loose_tracks_and_discs_keeps_one_part_per_folder()
  {
    var loose = _tree.Write("parent/Book/00 Intro.mp3");
    var disc = _tree.Write("parent/Book/Disc 1/01.mp3");

    var group = Assert.Single(Plan("parent").Groups);

    Assert.Equal("Book", group.Name);
    Assert.Equal([loose, disc], group.Files);
    Assert.Equal([Path.GetDirectoryName(loose), Path.GetDirectoryName(disc)], group.Parts.Select(part => part.Folder));
    Assert.True(group.CanSplit);
  }

  [Fact]
  public void A_nested_non_disc_folder_becomes_its_own_book_with_its_own_discs()
  {
    var outer = _tree.Write("parent/Outer/Disc 1/01.mp3");
    var bonus = _tree.Write("parent/Outer/Bonus Book/01.mp3");
    var bonusDisc = _tree.Write("parent/Outer/Bonus Book/CD2/01.mp3");

    var plan = Plan("parent");

    Assert.Equal(["Outer", "Bonus Book"], plan.Groups.Select(group => group.Name));
    Assert.Equal([outer], plan.Groups[0].Files);
    Assert.Equal([bonus, bonusDisc], plan.Groups[1].Files);
  }

  [Fact]
  public void Files_nested_inside_a_disc_folder_belong_to_that_disc()
  {
    var top = _tree.Write("parent/Book/Disc 1/01.mp3");
    var nested = _tree.Write("parent/Book/Disc 1/Extras/02.mp3");

    var group = Assert.Single(Plan("parent").Groups);

    Assert.Equal([top, nested], group.Files);
    Assert.Single(group.Parts);
  }

  [Fact]
  public void Loose_audio_in_the_parent_becomes_one_book_per_file()
  {
    var one = _tree.Write("parent/one.mp3");
    var two = _tree.Write("parent/two.m4b");
    _tree.Write("parent/notes.txt");
    _tree.Write("parent/Book/01.mp3");

    var plan = Plan("parent");

    Assert.Equal(["one.mp3", "two.m4b", "Book"], plan.Groups.Select(group => group.Name));
    Assert.True(plan.Groups[0].IsLooseFile);
    Assert.Equal([one], plan.Groups[0].Files);
    Assert.Equal([two], plan.Groups[1].Files);
    Assert.False(plan.Groups[2].IsLooseFile);
  }

  [Fact]
  public void A_directly_nested_disc_named_folder_is_its_own_book()
  {
    _tree.Write("parent/Disc 1/01.mp3");
    _tree.Write("parent/Disc 2/01.mp3");

    Assert.Equal(["Disc 1", "Disc 2"], Plan("parent").Groups.Select(group => group.Name));
  }

  [Fact]
  public void Unsupported_and_partial_files_are_ignored_and_no_file_repeats()
  {
    _tree.Write("parent/Book/01.mp3");
    _tree.Write("parent/Book/02.mp3.part");
    _tree.Write("parent/Book/cover.jpg");
    _tree.Write("parent/Book/Disc 1/01.mp3");

    var files = Plan("parent").Groups.SelectMany(group => group.Files).ToArray();

    Assert.Equal(2, files.Length);
    Assert.Equal(files.Length, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
  }

  [Fact]
  public async Task A_reparse_point_under_the_parent_is_skipped_and_reported()
  {
    var real = _tree.Write("outside/Linked Book/01.mp3");
    var kept = _tree.Write("parent/Book/01.mp3");
    var link = Path.Combine(_tree.Root, "parent", "Linked");
    await CreateJunctionAsync(link, Path.GetDirectoryName(Path.GetDirectoryName(real))!);
    try
    {
      var plan = Plan("parent");

      Assert.Equal([kept], Assert.Single(plan.Groups).Files);
      var problem = Assert.Single(plan.NotInspected);
      Assert.Equal("discovery.reparse-point-skipped", problem.Code);
      Assert.Equal(link, problem.FullPath);
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }
  }

  [Fact]
  public async Task A_parent_reached_through_a_reparse_point_proposes_nothing()
  {
    _tree.Write("outside/Book/01.mp3");
    var link = Path.Combine(_tree.Root, "link");
    await CreateJunctionAsync(link, Path.Combine(_tree.Root, "outside"));
    try
    {
      var plan = new BookGroupPlanner().Plan(link);

      Assert.Empty(plan.Groups);
      Assert.Equal("discovery.reparse-point-skipped", Assert.Single(plan.NotInspected).Code);
    }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }
  }

  [Fact]
  public void An_unreadable_folder_is_reported_and_the_rest_is_still_planned()
  {
    var kept = _tree.Write("parent/Good/01.mp3");
    _tree.Write("parent/Bad/01.mp3");
    var bad = Path.Combine(_tree.Root, "parent", "Bad");
    var planner = new BookGroupPlanner(new FaultingEnumerator(bad));

    var plan = planner.Plan(Path.Combine(_tree.Root, "parent"));

    Assert.Equal([kept], Assert.Single(plan.Groups).Files);
    var problem = Assert.Single(plan.NotInspected);
    Assert.Equal("discovery.path-unreadable", problem.Code);
    Assert.Equal(bad, problem.FullPath);
  }

  [Fact]
  public void A_missing_parent_is_reported()
  {
    var plan = new BookGroupPlanner().Plan(Path.Combine(_tree.Root, "nowhere"));

    Assert.Empty(plan.Groups);
    Assert.Equal("discovery.input-unreadable", Assert.Single(plan.NotInspected).Code);
  }

  [Fact]
  public void Cancelling_during_enumeration_stops_the_plan()
  {
    for (var i = 0; i < 5; i++) _tree.Write($"parent/Book {i}/01.mp3");
    using var cancellation = new CancellationTokenSource();
    var planner = new BookGroupPlanner(new FaultingEnumerator(Path.Combine(_tree.Root, "parent", "Book 2"), cancellation));

    Assert.Throws<OperationCanceledException>(() => planner.Plan(Path.Combine(_tree.Root, "parent"), null, cancellation.Token));
  }

  [Fact]
  public async Task A_token_cancelled_before_planning_throws_without_scanning()
  {
    _tree.Write("parent/Book/01.mp3");
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BookGroupPlanner().PlanAsync(Path.Combine(_tree.Root, "parent"), null, cancellation.Token));
  }

  [Fact]
  public void Progress_counts_folders_and_audio_files()
  {
    _tree.Write("parent/Book A/01.mp3");
    _tree.Write("parent/Book B/Disc 1/01.mp3");
    var reports = new List<BookGroupScanProgress>();

    new BookGroupPlanner().Plan(Path.Combine(_tree.Root, "parent"), new SyncProgress(reports.Add));

    Assert.Equal(new BookGroupScanProgress(4, 2), reports[^1]);
  }

  [Fact]
  public void Planning_never_probes_media_or_opens_files()
  {
    var path = _tree.Write("parent/Book/01.mp3");
    using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

    Assert.Single(Plan("parent").Groups);
  }

  [Fact]
  public void Combine_roots_the_book_at_the_common_folder_and_split_restores_the_parts()
  {
    _tree.Write("parent/Book/Disc 1/01.mp3");
    _tree.Write("parent/Book/Disc 2/01.mp3");
    var split = Assert.Single(Plan("parent").Groups).Split();

    Assert.Equal(["Disc 1", "Disc 2"], split.Select(group => group.Name));

    var combined = BookGroup.Combine(split);

    Assert.Equal("Book", combined.Name);
    Assert.Equal(split.SelectMany(group => group.Files), combined.Files);
    Assert.Equal(2, combined.Parts.Count);
  }

  [Fact]
  public void A_group_selection_is_named_after_its_book_folder_even_with_one_disc()
  {
    var file = _tree.Write("parent/Book/Disc 1/01.mp3");

    var selection = Assert.Single(Plan("parent").Groups).ToSelection();

    Assert.Equal("Book", selection.Name);
    Assert.Equal([file], selection.Paths);
    Assert.Equal("Disc 1/01.mp3", selection.FileId(file));
  }

  [Fact]
  public void A_selection_root_must_contain_every_file()
  {
    var file = _tree.Write("parent/Book/01.mp3");

    Assert.Throws<ArgumentException>(() => SourceSelection.Create([file], Path.Combine(_tree.Root, "parent", "Other")));
    Assert.Equal("parent", SourceSelection.Create([file], Path.Combine(_tree.Root, "parent")).Name);
  }

  private BookGroupPlan Plan(string relative) => new BookGroupPlanner().Plan(Path.Combine(_tree.Root, relative));

  public void Dispose() => _tree.Dispose();

  private sealed class SyncProgress(Action<BookGroupScanProgress> report) : IProgress<BookGroupScanProgress>
  {
    public void Report(BookGroupScanProgress value) => report(value);
  }

  private sealed class FaultingEnumerator(string directory, CancellationTokenSource? cancelOnReach = null) : IBookFolderEnumerator
  {
    private readonly FileSystemBookFolderEnumerator _inner = new();

    public IEnumerable<string> EnumerateEntries(string path)
    {
      if (string.Equals(path, directory, StringComparison.OrdinalIgnoreCase))
      {
        if (cancelOnReach is null) throw new UnauthorizedAccessException("Access to the path is denied.");
        cancelOnReach.Cancel();
      }
      return _inner.EnumerateEntries(path);
    }

    public FileAttributes GetAttributes(string path) => _inner.GetAttributes(path);
  }

  private static async Task CreateJunctionAsync(string linkPath, string targetPath)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"") { CreateNoWindow = true, RedirectStandardError = true, UseShellExecute = false })!;
    await process.WaitForExitAsync();
    Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
  }
}
