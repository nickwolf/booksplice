using BookSplice.Core.Analysis;
using BookSplice.Core.Naming;

namespace BookSplice.Core.Tests.Naming;

public sealed class OutputTemplateTests
{
  private static readonly string Destination = Path.Combine(Path.GetTempPath(), "booksplice-template-" + Guid.NewGuid().ToString("N"));
  private static readonly OutputTemplateValues Full = new("The Title", "Jane Doe", "Big Saga", "2", "Some Voice", "1999");

  private static OutputPathPlan Plan(string template, OutputTemplateValues? values = null, string? root = "SourceRoot", CollisionPolicy policy = CollisionPolicy.AvoidCollision, IReadOnlySet<string>? occupied = null)
    => new OutputNamePlanner().Plan(Destination, OutputPathTemplate.Parse(template), values ?? Full, root, policy, occupied ?? new HashSet<string>());

  private static string Relative(OutputPathPlan plan) => Path.GetRelativePath(Destination, plan.Path);

  [Theory]
  [InlineData("Book", "Root")]
  [InlineData("", "Root")]
  [InlineData(" ", null)]
  [InlineData("CON", "Root")]
  [InlineData("...", "Root")]
  public void Default_template_matches_the_title_only_path(string? title, string? root)
  {
    var occupied = new HashSet<string>(["book.m4b", "Root.m4b"]);
    var old = new OutputNamePlanner().CreateRequestedPath(Destination, title, root, CollisionPolicy.AvoidCollision, occupied);
    var plan = new OutputNamePlanner().Plan(Destination, OutputPathTemplate.Default, new OutputTemplateValues(title), root, CollisionPolicy.AvoidCollision, occupied);
    Assert.Equal(old, plan.Path);
    Assert.Equal(Destination, Path.GetDirectoryName(plan.Path));
  }

  [Fact]
  public void Default_template_expectations_are_unchanged()
  {
    Assert.Equal("Book.m4b", Relative(Plan("{title}", new OutputTemplateValues("Book"))));
    Assert.Equal("SourceRoot.m4b", Relative(Plan("{title}", new OutputTemplateValues(" "))));
    Assert.Equal("Audiobook.m4b", Relative(Plan("{title}", new OutputTemplateValues(null), null)));
  }

  [Fact]
  public void Tokens_expand_into_folders_and_file_name_case_insensitively()
  {
    var plan = Plan(@"{Author}\{series}\{seriesPosition} - {TITLE}");
    Assert.Equal(Path.Combine("Jane Doe", "Big Saga", "2 - The Title.m4b"), Relative(plan));
    Assert.Empty(plan.Notes);
  }

  [Fact]
  public void Forward_slashes_also_separate_segments()
    => Assert.Equal(Path.Combine("Jane Doe", "The Title.m4b"), Relative(Plan("{author}/{title}")));

  [Fact]
  public void Folder_with_only_empty_fields_is_dropped_and_explained()
  {
    var plan = Plan(@"{author}\{series}\{title}", Full with { Series = null });
    Assert.Equal(Path.Combine("Jane Doe", "The Title.m4b"), Relative(plan));
    var note = Assert.Single(plan.Notes);
    Assert.Equal("output.folder-skipped", note.Code);
    Assert.Equal(AnalysisDiagnosticSeverity.Information, note.Severity);
    Assert.Contains("series", note.Message);
  }

  [Fact]
  public void Folder_with_some_fields_present_keeps_the_rest_and_tidies_edges()
  {
    var plan = Plan(@"{author}\{series} {seriesPosition}\{title}", Full with { SeriesPosition = " " });
    Assert.Equal(Path.Combine("Jane Doe", "Big Saga", "The Title.m4b"), Relative(plan));
    var file = Plan(@"{seriesPosition} - {title}", Full with { SeriesPosition = null });
    Assert.Equal("The Title.m4b", Relative(file));
    Assert.Contains(file.Notes, note => note.Code == "output.field-missing");
  }

  [Fact]
  public void Literal_folders_are_kept()
    => Assert.Equal(Path.Combine("Audiobooks", "The Title.m4b"), Relative(Plan(@"Audiobooks\{title}", Full with { Series = null })));

  [Fact]
  public void Empty_file_name_falls_back_to_title_then_root_then_audiobook()
  {
    Assert.Equal("The Title.m4b", Relative(Plan("{series}", Full with { Series = null })));
    Assert.Equal(Path.Combine("Jane Doe", "SourceRoot.m4b"), Relative(Plan(@"{author}\{series}", new OutputTemplateValues(null, "Jane Doe"))));
    Assert.Equal("Audiobook.m4b", Relative(Plan("{series}", new OutputTemplateValues(null), null)));
    var trailing = Plan(@"{author}\", Full);
    Assert.Equal(Path.Combine("Jane Doe", "The Title.m4b"), Relative(trailing));
    Assert.Contains(Plan("{series}", Full with { Series = null }).Notes, note => note.Code == "output.filename-fallback");
  }

  [Fact]
  public void Values_cannot_add_segments_or_escape_the_destination()
  {
    var plan = Plan(@"{author}\{title}", new OutputTemplateValues("..\\..\\evil", "..", "x"));
    Assert.Equal(".._.._evil.m4b", Relative(plan));
    Assert.Equal(Destination, Path.GetDirectoryName(Path.GetFullPath(plan.Path)));
    Assert.Equal("AC_DC", Path.GetFileName(Path.GetDirectoryName(Plan(@"{author}\{title}", Full with { Author = "AC/DC" }).Path)));
  }

  [Fact]
  public void Values_of_only_dots_drop_the_folder()
  {
    var plan = Plan(@"{author}\{title}", Full with { Author = " ... " });
    Assert.Equal("The Title.m4b", Relative(plan));
    Assert.Contains(plan.Notes, note => note.Code == "output.folder-skipped");
  }

  [Fact]
  public void Windows_invalid_characters_reserved_names_and_length_are_handled_per_segment()
  {
    var plan = Plan(@"{author}\{series}\{title}", new OutputTemplateValues("a?b*c", "CON", new string('s', 400)));
    var parts = Relative(plan).Split(Path.DirectorySeparatorChar);
    Assert.Equal("_CON", parts[0]);
    Assert.Equal(OutputNamePlanner.MaxComponentLength, parts[1].Length);
    Assert.Equal("a_b_c.m4b", parts[2]);
    Assert.All(parts, part => Assert.InRange(part.Length, 1, OutputNamePlanner.MaxComponentLength));
    Assert.Equal("_Aux", Relative(Plan(@"{author}\{title}", Full with { Author = "Aux" })).Split(Path.DirectorySeparatorChar)[0]);
  }

  [Theory]
  [InlineData(@"\{title}")]
  [InlineData("/{title}")]
  [InlineData(@"C:\books\{title}")]
  [InlineData("C:{title}")]
  [InlineData(@"..\{title}")]
  [InlineData(@"{author}\..\{title}")]
  [InlineData(@"{author}\.\{title}")]
  [InlineData("{unknown}")]
  [InlineData("{title")]
  [InlineData("title}")]
  [InlineData("{}")]
  [InlineData("{{title}}")]
  [InlineData(" ")]
  public void Unsafe_or_malformed_templates_are_rejected(string template)
  {
    Assert.NotNull(OutputPathTemplate.Validate(template));
    Assert.False(OutputPathTemplate.TryParse(template, out var parsed, out var error));
    Assert.Null(parsed);
    Assert.False(string.IsNullOrWhiteSpace(error));
  }

  [Fact]
  public void Unknown_field_error_lists_the_supported_fields()
    => Assert.All(OutputPathTemplate.TokenNames, name => Assert.Contains("{" + name + "}", OutputPathTemplate.Validate("{nope}")));

  [Theory]
  [InlineData(@"C:\")]
  [InlineData(@"\\server\share\")]
  [InlineData(@"\\server\share")]
  public void Root_destinations_work_with_default_and_folder_templates(string destination)
  {
    var planner = new OutputNamePlanner();
    var occupied = new HashSet<string>();
    var root = Path.GetFullPath(destination);
    Assert.Equal(Path.Combine(root, "Book.m4b"), planner.Plan(destination, OutputPathTemplate.Default, new OutputTemplateValues("Book"), null, CollisionPolicy.AvoidCollision, occupied).Path);
    Assert.Equal(Path.Combine(root, "Jane Doe", "Book.m4b"), planner.Plan(destination, OutputPathTemplate.Parse(@"{author}\{title}"), Full with { Title = "Book" }, null, CollisionPolicy.AvoidCollision, occupied).Path);
  }

  [Fact]
  public void Collisions_are_checked_in_the_resolved_folder_and_reported()
  {
    var root = Directory.CreateTempSubdirectory("booksplice-collide-").FullName;
    try
    {
      var folder = Directory.CreateDirectory(Path.Combine(root, "Jane Doe", "Big Saga")).FullName;
      File.WriteAllText(Path.Combine(folder, "The Title.m4b"), "x");
      File.WriteAllText(Path.Combine(root, "The Title (2).m4b"), "x");
      var planner = new OutputNamePlanner();
      var plan = planner.Plan(root, OutputPathTemplate.Parse(@"{author}\{series}\{title}"), Full, null);
      Assert.Equal(Path.Combine(folder, "The Title (2).m4b"), plan.Path);
      Assert.Contains(plan.Notes, note => note.Code == "output.collision-renamed" && note.Message.Contains("The Title (2).m4b"));
      var overwrite = planner.Plan(root, OutputPathTemplate.Parse(@"{author}\{series}\{title}"), Full, null, CollisionPolicy.Overwrite);
      Assert.Equal(Path.Combine(folder, "The Title.m4b"), overwrite.Path);
      Assert.Contains(overwrite.Notes, note => note.Code == "output.collision-overwrite");
      Assert.False(Directory.Exists(Path.Combine(root, "Other")));
      planner.Plan(root, OutputPathTemplate.Parse(@"Other\{title}"), Full, null);
      Assert.False(Directory.Exists(Path.Combine(root, "Other")));
    }
    finally { Directory.Delete(root, true); }
  }
}
