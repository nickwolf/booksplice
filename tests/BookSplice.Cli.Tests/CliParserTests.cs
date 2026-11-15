using BookSplice.Cli;

namespace BookSplice.Cli.Tests;

public sealed class CliParserTests
{
  [Fact]
  public void ParseAcceptsEverySupportedOption()
  {
    var result = CliParser.Parse([
      "C:\\Books\\Source",
      "--output", "C:\\Output",
      "--quality", "balanced",
      "--bitrate", "112",
      "--jobs", "7",
      "--no-chapters",
      "--overwrite",
      "--dry-run",
      "--json",
    ]);

    var options = Assert.IsType<CliOptions>(result.Options);
    Assert.True(result.IsSuccess);
    Assert.Empty(result.Diagnostics);
    Assert.Equal("C:\\Books\\Source", options.Source);
    Assert.Equal("C:\\Output", options.OutputDirectory);
    Assert.Equal("balanced", options.QualityProfileId);
    Assert.Equal(112, options.BitrateKbps);
    Assert.Equal(7, options.Jobs);
    Assert.False(options.CreateChapters);
    Assert.True(options.Overwrite);
    Assert.True(options.DryRun);
    Assert.True(options.Json);
  }

  [Fact]
  public void ParseAcceptsAndValidatesTheOutputTemplate()
  {
    var ok = CliParser.Parse(["book", "--output-template", @"{author}\{series}\{title}"]);
    Assert.True(ok.IsSuccess);
    Assert.Equal(@"{author}\{series}\{title}", ok.Options!.OutputTemplate);
    Assert.Null(CliParser.Parse(["book"]).Options!.OutputTemplate);
    foreach (var bad in new[] { @"..\{title}", "{nope}", @"C:\{title}" })
      Assert.Contains(CliParser.Parse(["book", "--output-template", bad]).Diagnostics, d => d.Code == "usage.output-template");
    Assert.Contains(CliParser.Parse(["book", "--output-template"]).Diagnostics, d => d.Code == "usage.missing-value");
  }

  [Fact]
  public void ParseLeavesOptionalOverridesUnset()
  {
    var result = CliParser.Parse(["book"]);

    var options = Assert.IsType<CliOptions>(result.Options);
    Assert.True(result.IsSuccess);
    Assert.Null(options.OutputDirectory);
    Assert.Null(options.QualityProfileId);
    Assert.Null(options.BitrateKbps);
    Assert.Null(options.Jobs);
    Assert.Null(options.CreateChapters);
    Assert.False(options.Overwrite);
    Assert.False(options.DryRun);
    Assert.False(options.Json);
  }

  [Theory]
  [InlineData()]
  [InlineData("one", "two")]
  public void ParseRequiresExactlyOneSource(params string[] arguments)
  {
    var result = CliParser.Parse(arguments);

    Assert.False(result.IsSuccess);
    Assert.Null(result.Options);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.source-count");
  }

  [Theory]
  [InlineData("--unknown")]
  [InlineData("-x")]
  public void ParseRejectsUnknownOptions(string option)
  {
    var result = CliParser.Parse(["book", option]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.unknown-option");
  }

  [Theory]
  [InlineData("--output")]
  [InlineData("--quality")]
  [InlineData("--bitrate")]
  [InlineData("--jobs")]
  public void ParseRejectsOptionsWithMissingValues(string option)
  {
    var result = CliParser.Parse(["book", option]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.missing-value");
  }

  [Theory]
  [InlineData("--output", "C:\\One", "C:\\Two")]
  [InlineData("--quality", "balanced", "efficient")]
  [InlineData("--bitrate", "64", "96")]
  [InlineData("--jobs", "2", "3")]
  public void ParseRejectsDuplicateScalarOptions(string option, string first, string second)
  {
    var result = CliParser.Parse(["book", option, first, option, second]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.duplicate-option");
  }

  [Fact]
  public void ParseRejectsBothChapterSwitches()
  {
    var result = CliParser.Parse(["book", "--chapters", "--no-chapters"]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.chapter-conflict");
  }

  [Theory]
  [InlineData("31")]
  [InlineData("321")]
  [InlineData("not-a-number")]
  public void ParseRejectsBitrateOutsideInclusiveRange(string value)
  {
    var result = CliParser.Parse(["book", "--bitrate", value]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.bitrate-range");
  }

  [Theory]
  [InlineData("0")]
  [InlineData("33")]
  [InlineData("not-a-number")]
  public void ParseRejectsJobsOutsideInclusiveRange(string value)
  {
    var result = CliParser.Parse(["book", "--jobs", value]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.jobs-range");
  }

  [Theory]
  [InlineData("Balanced")]
  [InlineData("unknown")]
  public void ParseRequiresAnExactFrozenQualityProfileId(string value)
  {
    var result = CliParser.Parse(["book", "--quality", value]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.quality-profile");
  }

  [Theory]
  [InlineData("book\nname")]
  [InlineData("C:\\Output\rname")]
  [InlineData("balanced\t")]
  public void ParseRejectsControlCharacters(string unsafeValue)
  {
    var arguments = unsafeValue.StartsWith("book", StringComparison.Ordinal)
      ? new[] { unsafeValue }
      : unsafeValue.StartsWith("C:", StringComparison.Ordinal)
        ? ["book", "--output", unsafeValue]
        : ["book", "--quality", unsafeValue];

    var result = CliParser.Parse(arguments);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.unsafe-character");
  }


  [Fact]
  public void ParseAcceptsCheckWithFullValidation()
  {
    var result = CliParser.Parse(["Book.m4b", "--check", "--validation", "full"]);

    var options = Assert.IsType<CliOptions>(result.Options);
    Assert.True(result.IsSuccess);
    Assert.True(options.Check);
    Assert.Equal(BookSplice.Core.Settings.ValidationLevel.Full, options.Validation);
  }

  [Fact]
  public void ParseAcceptsCheckWithJsonAndAnM4bInAnyCase()
  {
    var result = CliParser.Parse(["Book.M4B", "--check", "--json"]);

    var options = Assert.IsType<CliOptions>(result.Options);
    Assert.True(result.IsSuccess);
    Assert.True(options.Check);
    Assert.True(options.Json);
  }

  [Theory]
  [InlineData("Book.mp3")]
  [InlineData("Book")]
  public void ParseRejectsCheckSourcesThatAreNotM4b(string source)
  {
    var result = CliParser.Parse([source, "--check"]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.check-source");
  }

  [Theory]
  [InlineData("--overwrite")]
  [InlineData("--dry-run")]
  [InlineData("--no-chapters")]
  [InlineData("--output", "C:\\Output")]
  [InlineData("--quality", "balanced")]
  public void ParseRejectsCheckWithOptionsOtherThanValidationAndJson(params string[] options)
  {
    var result = CliParser.Parse(["Book.m4b", .. options, "--check"]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.check-options");
  }

  [Fact]
  public void ParseRejectsCheckWithoutASource()
  {
    var result = CliParser.Parse(["--check"]);

    Assert.False(result.IsSuccess);
    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "usage.source-count");
  }
}
