using BookSplice.Core.Analysis;
using BookSplice.Core.Settings;
using BookSplice.Core.Planning;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Validation;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class ConversionResultDisplayTests
{
  private static readonly string[] AudioChecks = ["audio.primary-count", "audio.codec", "audio.sample-rate", "audio.layout", "audio.extra"];
  [Theory]
  [InlineData(ConversionTerminalStatus.Succeeded, "Conversion complete")]
  [InlineData(ConversionTerminalStatus.Cancelled, "Conversion cancelled")]
  [InlineData(ConversionTerminalStatus.ValidationFailed, "Conversion failed")]
  [InlineData(ConversionTerminalStatus.PublicationFailed, "Conversion failed")]
  public void TerminalOutcomesRemainDistinct(ConversionTerminalStatus status, string title)
  {
    var display = ConversionResultDisplay.Create(Result(status), TimeSpan.FromSeconds(252), ValidationLevel.Lightweight);
    Assert.Equal(title, display.Title);
    Assert.Equal("Elapsed: 4m 12s", display.ElapsedText);
    Assert.Equal(status == ConversionTerminalStatus.Succeeded, display.CanOpenOutput);
    Assert.Equal(status == ConversionTerminalStatus.Succeeded ? "Saved to: finished.m4b" : "No output was published.", display.OutputText);
  }

  [Fact]
  public void UnknownWarningsRemainVisibleAndOriginalMessagesRemainInDiagnostics()
  {
    const string message = "Unrecognized probe warning";
    var display = ConversionResultDisplay.Create(Result(ConversionTerminalStatus.Succeeded,
      diagnostics: [new("validation.warning", ServiceDiagnosticSeverity.Warning, message)]), TimeSpan.Zero, ValidationLevel.Lightweight);
    Assert.Equal("Conversion complete with warnings", display.Title);
    Assert.Equal(message, Assert.Single(display.Warnings).Message);
    Assert.Null(display.Warnings[0].Explanation);
    Assert.Contains(message, display.DiagnosticsText);
  }

  [Theory]
  [InlineData(ValidationLevel.Lightweight, false, false, "Not run")]
  [InlineData(ValidationLevel.Full, false, false, "Not completed")]
  [InlineData(ValidationLevel.Full, true, true, "Passed")]
  [InlineData(ValidationLevel.Full, true, false, "Failed")]
  public void FullDecodeUsesTheActualCheck(ValidationLevel requested, bool hasDecode, bool passed, string expected)
  {
    var report = new ValidationReport(Path.GetFullPath("temporary.m4b"), passed || !hasDecode,
      hasDecode ? [new("decode.full", passed, "A full FFmpeg decode reported an error.")] : [new("container.parse", true, "")]);
    var display = ConversionResultDisplay.Create(Result(ConversionTerminalStatus.Succeeded, report), TimeSpan.Zero, requested);
    Assert.Equal("Full audio decode: " + expected, display.DecodeText);
    if (hasDecode && passed) Assert.DoesNotContain("reported an error", string.Join(' ', display.Checks));
  }

  [Fact]
  public void FailureLeadsWithTheStageAndRequiredFailure()
  {
    var report = ValidationReport.Failed(Path.GetFullPath("temporary.m4b"),
      [new("audio.primary-count", false, "The output must contain exactly one primary audio stream.")]);
    var display = ConversionResultDisplay.Create(Result(ConversionTerminalStatus.ValidationFailed, report), TimeSpan.Zero, ValidationLevel.Full);
    Assert.Contains("Validation", display.Summary);
    Assert.Contains("exactly one primary audio stream", display.Summary);
    Assert.False(display.CanOpenOutput);
    Assert.Contains(display.Checks, check => check.StartsWith("Failed:", StringComparison.Ordinal));
  }

  [Fact]
  public void CompletedPlanDeterminesWhetherDecodeWasRequested()
  {
    var plan = new ConversionPlan(["source.wav"], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(),
      new Dictionary<SemanticField, IReadOnlyList<string>>()), null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Full, CollisionPolicy.AvoidCollision,
      "finished.m4b", AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", MetadataProfiles.GenericMp4);
    var report = ValidationReport.Passed(Path.GetFullPath("temporary.m4b"), [new("container.parse", true, "")]);
    var result = Result(ConversionTerminalStatus.Cancelled, report) with { Planning = new(BookAnalysisStatus.Ready, plan, []) };
    var display = ConversionResultDisplay.Create(result, TimeSpan.Zero, ValidationLevel.Lightweight);
    Assert.Equal("Full audio decode: Not completed", display.DecodeText);
  }

  [Fact]
  public void DecoderFailureCannotBeExplainedAwayByADataStreamWarning()
  {
    const string message = "Unsupported codec with id 98314 for input stream 1";
    var report = ValidationReport.Failed(Path.GetFullPath("temporary.m4b"), [new("decode.full", false, "Full audio decode failed.")], ["Invalid data decoding audio"]);
    var result = Result(ConversionTerminalStatus.ValidationFailed, report,
      [new("validation.warning", ServiceDiagnosticSeverity.Warning, message), new("validation.failed", ServiceDiagnosticSeverity.Error, "Invalid data decoding audio")]);
    var display = ConversionResultDisplay.Create(result, TimeSpan.Zero, ValidationLevel.Full);
    Assert.Equal("Conversion failed", display.Title);
    Assert.Contains("Full audio decode failed", display.Summary);
    Assert.Equal("Full audio decode: Failed", display.DecodeText);
    Assert.Null(Assert.Single(display.Warnings).Explanation);
    Assert.Contains("Invalid data decoding audio", display.DiagnosticsText);
  }

  [Fact]
  public void CancellationRetainsEvidenceOfPublicationIfItAlreadyHappened()
  {
    var display = ConversionResultDisplay.Create(Result(ConversionTerminalStatus.Cancelled) with
    { Publication = new(PublicationStatus.Published, "already-saved.m4b") }, TimeSpan.Zero, ValidationLevel.Full);
    Assert.True(display.CanOpenOutput);
    Assert.Contains("already-saved.m4b", display.OutputText);
    Assert.Equal("Conversion cancelled", display.Title);
  }

  [Theory]
  [InlineData("data", 1, true, true)]
  [InlineData("audio", 1, true, false)]
  [InlineData("data", 2, true, false)]
  [InlineData("data", 1, false, false)]
  public void UnsupportedCodecExplanationRequiresMatchingDataStreamAndPassedAudio(string type, int streamIndex, bool valid, bool explained)
  {
    const string message = "Unsupported codec with id 98314 for input stream 1";
    var facts = new MediaProbeResult([new(0, "aac", "audio", 1, 44100, 1, null, new Dictionary<string, string>())], [], [],
      new TagCollection([]), new Dictionary<string, string>(), [message], OtherStreams: [new(streamIndex, type, "bin_data", "text")]);
    var checks = AudioChecks
      .Select(code => new ValidationCheck(code, valid, "Missing required audio"));
    var report = new ValidationReport(Path.GetFullPath("temporary.m4b"), valid, checks, facts, [message]);
    var display = ConversionResultDisplay.Create(Result(valid ? ConversionTerminalStatus.Succeeded : ConversionTerminalStatus.ValidationFailed, report,
      [new("validation.warning", ServiceDiagnosticSeverity.Warning, message)]), TimeSpan.Zero, ValidationLevel.Lightweight);
    var warning = Assert.Single(display.Warnings);
    Assert.Equal(explained, warning.Explanation is not null);
    Assert.Equal(message, warning.Message);
    Assert.Contains(message, display.DiagnosticsText);
  }

  private static ConversionServiceResult Result(ConversionTerminalStatus status, ValidationReport? validation = null, IReadOnlyList<ServiceDiagnostic>? diagnostics = null)
    => new(Guid.NewGuid(), status, ConversionStage.Validation, null, null, null, validation, null, diagnostics ?? [], [],
      status == ConversionTerminalStatus.Succeeded ? "finished.m4b" : null);
}
