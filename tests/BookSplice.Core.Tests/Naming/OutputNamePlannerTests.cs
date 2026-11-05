using BookSplice.Core.Naming;

namespace BookSplice.Core.Tests.Naming;

public sealed class OutputNamePlannerTests
{
  [Fact]
  public void CreateRequestedPath_sanitizes_windows_names_and_stays_in_destination()
  {
    var path = new OutputNamePlanner().CreateRequestedPath("C:\\staging", "  Bad<name>: \"x\" / ", null);
    Assert.Equal(".m4b", Path.GetExtension(path));
    Assert.DoesNotContain("<", path);
    Assert.DoesNotContain("\\x", path);
    Assert.StartsWith(Path.GetFullPath("C:\\staging"), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CreateRequestedPath_uses_title_root_and_stable_fallback()
  {
    var planner = new OutputNamePlanner();
    Assert.EndsWith("Book.m4b", planner.CreateRequestedPath("out", "Book", "Root"));
    Assert.EndsWith("Root.m4b", planner.CreateRequestedPath("out", " ", "Root"));
    Assert.EndsWith("Audiobook.m4b", planner.CreateRequestedPath("out", null, null));
  }

  [Theory]
  [InlineData("CON")]
  [InlineData("prn.txt")]
  [InlineData("AUX")]
  [InlineData("NUL")]
  [InlineData("COM1")]
  [InlineData("COM9.m4b")]
  [InlineData("LPT1")]
  [InlineData("LPT9.foo")]
  public void CreateRequestedPath_avoids_reserved_device_names(string title)
    => Assert.NotEqual("CON.m4b", Path.GetFileName(new OutputNamePlanner().CreateRequestedPath("out", title, null)));

  [Fact]
  public void CreateRequestedPath_applies_deterministic_case_insensitive_collision_suffixes()
  {
    var path = new OutputNamePlanner().CreateRequestedPath("out", "Title", null, CollisionPolicy.AvoidCollision, new HashSet<string>(["title.m4b", "Title (2).m4b"]));
    Assert.EndsWith("Title (3).m4b", path);
  }

  [Fact]
  public void CreateRequestedPath_overwrite_policy_keeps_unsuffixed_name_without_mutating_filesystem()
  {
    var path = new OutputNamePlanner().CreateRequestedPath("out", "Title", null, CollisionPolicy.Overwrite, new HashSet<string>(["Title.m4b"]));
    Assert.EndsWith("Title.m4b", path);
  }

  [Fact]
  public void Plan_adds_queued_paths_to_the_folder_listing_and_numbers_past_both()
  {
    using var folder = new TempFolder();
    File.WriteAllText(Path.Combine(folder.Path, "Title.m4b"), "");
    File.WriteAllText(Path.Combine(folder.Path, "title (2).M4B"), "");
    var queued = new[] { Path.Combine(folder.Path, "Title (3).m4b") };
    var plan = Plan(folder.Path, queued);
    Assert.Equal(Path.Combine(folder.Path, "Title (4).m4b"), plan.Path);
    Assert.Contains(plan.Notes, note => note.Code == "output.collision-renamed");
  }

  [Fact]
  public void Plan_with_only_a_queued_path_suffixes_and_compares_case_insensitively()
  {
    using var folder = new TempFolder();
    Assert.Equal(Path.Combine(folder.Path, "Title.m4b"), Plan(folder.Path, []).Path);
    Assert.Equal(Path.Combine(folder.Path, "Title (2).m4b"), Plan(folder.Path, [Path.Combine(folder.Path.ToUpperInvariant(), "TITLE.M4B")]).Path);
  }

  [Fact]
  public void Plan_ignores_queued_paths_in_other_folders()
  {
    using var folder = new TempFolder();
    using var other = new TempFolder();
    Assert.Equal(Path.Combine(folder.Path, "Title.m4b"), Plan(folder.Path, [Path.Combine(other.Path, "Title.m4b"), Path.Combine(folder.Path, "Sub", "Title.m4b")]).Path);
  }

  [Fact]
  public void Plan_overwrite_policy_keeps_the_name_and_notes_a_queued_path()
  {
    using var folder = new TempFolder();
    var plan = new OutputNamePlanner().Plan(folder.Path, OutputPathTemplate.Default, new OutputTemplateValues("Title"), null, CollisionPolicy.Overwrite, queuedOutputPaths: [Path.Combine(folder.Path, "Title.m4b")]);
    Assert.Equal(Path.Combine(folder.Path, "Title.m4b"), plan.Path);
    Assert.Contains(plan.Notes, note => note.Code == "output.collision-overwrite");
  }

  private static OutputPathPlan Plan(string folder, string[] queued)
    => new OutputNamePlanner().Plan(folder, OutputPathTemplate.Default, new OutputTemplateValues("Title"), null, CollisionPolicy.AvoidCollision, queuedOutputPaths: queued);

  private sealed class TempFolder : IDisposable
  {
    public string Path { get; } = Directory.CreateTempSubdirectory("booksplice-names-").FullName;
    public void Dispose() => Directory.Delete(Path, true);
  }

  [Fact]
  public void CreateRequestedPath_bounds_component_including_extension()
  {
    var path = new OutputNamePlanner().CreateRequestedPath("out", new string('x', 400), null);
    Assert.InRange(Path.GetFileName(path).Length, 1, OutputNamePlanner.MaxComponentLength);
    Assert.EndsWith(".m4b", path);
  }
}
