using BookSplice.Core.Metadata;

namespace BookSplice.Core.Tests.Metadata;

public sealed class MetadataProfileCatalogTests
{
  private const string Valid = """{"schemaVersion":1,"name":"Synthetic","version":"1","mappings":[{"field":"BookTitle","keys":["TITLE","ALBUM"]},{"field":"RatingWmp","keys":["RATING WMP"]}]}""";

  [Fact]
  public void Load_reads_a_valid_profile_and_lists_built_ins_first()
  {
    using var directory = new TempProfiles();
    directory.Write("b.json", Valid);
    directory.Write("a.json", Valid.Replace("Synthetic", "alpha"));

    var catalog = MetadataProfileCatalog.Load(directory.Root);

    Assert.Empty(catalog.Errors);
    Assert.Equal(["GenericMp4", "alpha", "Synthetic"], catalog.Profiles.Select(profile => profile.Name));
    var profile = catalog.Find("Synthetic")!;
    Assert.Equal("1", profile.Version);
    Assert.Equal(SemanticField.RatingWmp, profile.Mappings[1].Field);
    Assert.Equal(["RATING WMP"], profile.Mappings[1].Keys);
    Assert.Null(catalog.Find("synthetic"));
    Assert.True(MetadataProfileCatalog.IsBuiltIn("GenericMp4"));
    Assert.False(MetadataProfileCatalog.IsBuiltIn("Synthetic"));
  }

  [Fact]
  public void Load_of_a_missing_directory_returns_only_built_ins_without_errors()
  {
    var catalog = MetadataProfileCatalog.Load(Path.Combine(Path.GetTempPath(), $"BookSplice-{Guid.NewGuid():N}"));

    Assert.Empty(catalog.Errors);
    Assert.Equal(["GenericMp4"], catalog.Profiles.Select(profile => profile.Name));
  }

  [Theory]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","extra":1,"mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T"],"extra":true}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"Nonsense","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"5","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"booktitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T"]},{"field":"BookTitle","keys":["U"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T"]},{"field":"Author","keys":["t"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T","t"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T"]},{"field":"Author","keys":["ALBUMARTIST"]},{"field":"Narrator","keys":["album_artist"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":[]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[]}""")]
  [InlineData("""{"schemaVersion":1,"name":"bad name","version":"1","mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"","version":"1","mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":2,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":null,"mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":1,"mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T",null]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":[" T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T "]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T\u0001"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":[""]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1\n","mappings":[{"field":"BookTitle","keys":["T"]}]}""")]
  [InlineData("""{"schemaVersion":1,"name":"S","version":"1","mappings":[{"field":"BookTitle","keys":["T"]}]} trailing""")]
  [InlineData("""[]""")]
  [InlineData("""{""")]
  public void Load_rejects_invalid_profiles_without_failing_the_catalog(string json)
  {
    using var directory = new TempProfiles();
    directory.Write("bad.json", json);
    directory.Write("good.json", Valid);

    var catalog = MetadataProfileCatalog.Load(directory.Root);

    var error = Assert.Single(catalog.Errors);
    Assert.Equal("bad.json", error.FileName);
    Assert.NotEmpty(error.Reason);
    Assert.Equal(["GenericMp4", "Synthetic"], catalog.Profiles.Select(profile => profile.Name));
  }

  [Fact]
  public void Load_rejects_duplicate_json_properties()
  {
    using var directory = new TempProfiles();
    directory.Write("dup.json", """{"schemaVersion":1,"name":"a","name":"b","version":"1","mappings":[{"field":"BookTitle","keys":["T"]}]}""");
    directory.Write("dupmapping.json", """{"schemaVersion":1,"name":"c","version":"1","mappings":[{"field":"BookTitle","field":"Author","keys":["T"]}]}""");

    var catalog = MetadataProfileCatalog.Load(directory.Root);

    Assert.Equal(["dup.json", "dupmapping.json"], catalog.Errors.Select(error => error.FileName));
    Assert.Equal(["GenericMp4"], catalog.Profiles.Select(profile => profile.Name));
  }

  [Fact]
  public void Load_rejects_excessive_nesting()
  {
    using var directory = new TempProfiles();
    directory.Write("deep.json", """{"schemaVersion":1,"name":"S","version":"1","mappings":[[[[[[[[[[]]]]]]]]]]}""");

    Assert.Single(MetadataProfileCatalog.Load(directory.Root).Errors);
  }

  [Fact]
  public void Load_accepts_a_key_with_inner_spaces_and_records_the_claimed_id_on_failure()
  {
    using var directory = new TempProfiles();
    directory.Write("bad.json", """{"schemaVersion":1,"name":"Claimed","version":"1","mappings":[]}""");

    var error = Assert.Single(MetadataProfileCatalog.Load(directory.Root).Errors);

    Assert.Equal("Claimed", error.ProfileId);
  }

  [Theory]
  [InlineData("GenericMp4")]
  [InlineData("genericmp4")]
  public void Load_rejects_ids_that_collide_with_a_built_in(string id)
  {
    using var directory = new TempProfiles();
    directory.Write("builtin.json", Valid.Replace("Synthetic", id));

    var catalog = MetadataProfileCatalog.Load(directory.Root);

    Assert.Equal("builtin.json", Assert.Single(catalog.Errors).FileName);
    Assert.Equal(["GenericMp4"], catalog.Profiles.Select(profile => profile.Name));
  }

  [Fact]
  public void Load_rejects_every_file_that_claims_the_same_id()
  {
    using var directory = new TempProfiles();
    directory.Write("one.json", Valid);
    directory.Write("two.json", Valid.Replace("Synthetic", "SYNTHETIC"));
    directory.Write("three.json", Valid.Replace("Synthetic", "Other"));

    var catalog = MetadataProfileCatalog.Load(directory.Root);

    Assert.Equal(["one.json", "two.json"], catalog.Errors.Select(error => error.FileName));
    Assert.Equal(["GenericMp4", "Other"], catalog.Profiles.Select(profile => profile.Name));
  }

  [Fact]
  public void Load_ignores_other_extensions_and_subdirectories()
  {
    using var directory = new TempProfiles();
    directory.Write("note.txt", Valid);
    directory.Write("profile.json.bak", Valid);
    directory.Write("upper.JSON", Valid.Replace("Synthetic", "Upper"));
    Directory.CreateDirectory(Path.Combine(directory.Root, "nested"));
    File.WriteAllText(Path.Combine(directory.Root, "nested", "inner.json"), Valid.Replace("Synthetic", "Inner"));
    Directory.CreateDirectory(Path.Combine(directory.Root, "folder.json"));

    var catalog = MetadataProfileCatalog.Load(directory.Root);

    Assert.Empty(catalog.Errors);
    Assert.Equal(["GenericMp4", "Upper"], catalog.Profiles.Select(profile => profile.Name));
  }

  [Fact]
  public void Load_rejects_oversize_files_and_reports_only_the_file_name()
  {
    using var directory = new TempProfiles();
    directory.Write("big.json", Valid + new string(' ', MetadataProfileCatalog.MaximumFileBytes));

    var error = Assert.Single(MetadataProfileCatalog.Load(directory.Root).Errors);

    Assert.Equal("big.json", error.FileName);
    Assert.DoesNotContain(directory.Root, error.Reason);
  }

  [Fact]
  public void Load_rejects_symbolic_links()
  {
    using var directory = new TempProfiles();
    var target = Path.Combine(directory.Root, "target.txt");
    File.WriteAllText(target, Valid);
    try { File.CreateSymbolicLink(Path.Combine(directory.Root, "link.json"), target); }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { return; }

    var catalog = MetadataProfileCatalog.Load(directory.Root);

    Assert.Equal("link.json", Assert.Single(catalog.Errors).FileName);
    Assert.Equal(["GenericMp4"], catalog.Profiles.Select(profile => profile.Name));
  }

  private sealed class TempProfiles : IDisposable
  {
    public TempProfiles() => Directory.CreateDirectory(Root);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"BookSplice-{Guid.NewGuid():N}");
    public void Write(string name, string content) => File.WriteAllText(Path.Combine(Root, name), content);
    public void Dispose() => Directory.Delete(Root, recursive: true);
  }
}
