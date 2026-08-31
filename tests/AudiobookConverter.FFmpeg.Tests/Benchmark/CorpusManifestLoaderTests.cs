using AudiobookConverter.Benchmarks;
#pragma warning disable CA1707

namespace AudiobookConverter.FFmpeg.Tests.Benchmark;

public sealed class CorpusManifestLoaderTests
{
  [Fact]
  public void Load_rejects_duplicate_case_ids_without_disclosing_source_path()
  {
    var path = Write("""{"schemaVersion":"1","cases":[{"caseId":"case-one","sourcePath":"private-one.mp3","traits":["speech"],"expectedOrder":["one.mp3"],"groups":["smoke"],"copyPermission":true},{"caseId":"case-one","sourcePath":"private-two.mp3","traits":["speech"],"expectedOrder":["two.mp3"],"groups":["smoke"],"copyPermission":true}]}""");
    try
    {
      var exception = Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Load(path));
      Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally { File.Delete(path); }
  }

  [Fact]
  public void Load_rejects_unknown_fields_and_malformed_expected_order()
  {
    var path = Write("""{"schemaVersion":"1","cases":[{"caseId":"case-one","sourcePath":"synthetic","traits":["speech"],"expectedOrder":["one.mp3","one.mp3"],"groups":["smoke"],"copyPermission":true,"unknown":1}]}""");
    try { Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Load(path)); }
    finally { File.Delete(path); }
  }

  [Theory]
  [InlineData("{\"schemaVersion\":\"1\",\"cases\":[]}")]
  [InlineData("{\"schemaVersion\":\"\",\"cases\":[{\"caseId\":\"case-one\",\"sourcePath\":\"private.mp3\",\"traits\":[\"speech\"],\"expectedOrder\":[\"one.mp3\"],\"groups\":[\"smoke\"],\"copyPermission\":true}]}")]
  [InlineData("{\"schemaVersion\":\"1\",\"cases\":[{\"caseId\":\"case-one\",\"sourcePath\":\"private.mp3\",\"traits\":[],\"expectedOrder\":[\"one.mp3\"],\"groups\":[\"smoke\"],\"copyPermission\":true}]}")]
  public void Load_rejects_missing_or_empty_required_values_without_disclosing_source_path(string text)
  {
    var path = Write(text);
    try
    {
      var exception = Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Load(path));
      Assert.DoesNotContain("private.mp3", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally { File.Delete(path); }
  }

  [Fact]
  public void Load_rejects_expected_order_that_contains_a_path()
  {
    var path = Write("""{"schemaVersion":"1","cases":[{"caseId":"case-one","sourcePath":"private.mp3","traits":["speech"],"expectedOrder":["nested/one.mp3"],"groups":["smoke"],"copyPermission":true}]}""");
    try { Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Load(path)); }
    finally { File.Delete(path); }
  }

  private static string Write(string text)
  {
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    File.WriteAllText(path, text);
    return path;
  }
}
