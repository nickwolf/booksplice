using BookSplice.Cli;
using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Execution;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;

namespace BookSplice.Cli.Tests;

public sealed class AdvancedOptionsTests
{
  [Fact]
  public void AdvancedOptionsOverrideSavedValues()
  {
    var parsed = CliParser.Parse(["book", "--order", "metadata", "--metadata-profile", "NickMp3tag", "--validation", "full"]);
    Assert.True(parsed.IsSuccess);
    Assert.Equal(OrderCandidateId.Metadata, parsed.Options!.Order);
    var configuration = CliConfiguration.Resolve(parsed.Options!, new(SettingsLoadCode.Valid, AppSettings.Defaults with { OutputDirectory = Path.GetTempPath() }, []));
    Assert.Equal("NickMp3tag", configuration.Settings!.MetadataProfileId);
    Assert.Equal(ValidationLevel.Full, configuration.Settings.ValidationLevel);
  }

  [Theory]
  [InlineData("--order", "random")]
  [InlineData("--metadata-profile", "unknown")]
  [InlineData("--validation", "none")]
  public void InvalidAdvancedOptionsFailBeforeConversion(string option, string value)
    => Assert.False(CliParser.Parse(["book", option, value]).IsSuccess);

  [Fact]
  public async Task InteractiveDecisionDisplaysOrderAndResubmitsSelectedCandidate()
  {
    var service = new DecisionService();
    var output = new StringWriter();
    var result = await new CliApplication(new Store(), service).RunAsync(["book"], output, TextWriter.Null,
      CancellationToken.None, new StringReader("2\n"));
    Assert.Equal(CliExitCode.Success, result);
    Assert.Contains("02.mp3", output.ToString());
    Assert.Equal(OrderCandidateId.Metadata, service.Request!.AnalysisOptions!.SelectedOrderCandidateId);
    Assert.Equal(2, service.Calls);
    Assert.Equal([Path.Combine(Path.GetTempPath(), "02.mp3"), Path.Combine(Path.GetTempPath(), "01.mp3")], service.Request.ExpectedSourcePaths);
  }

  [Fact]
  public async Task JsonNeverReadsInteractiveInput()
  {
    var service = new DecisionService();
    var result = await new CliApplication(new Store(), service).RunAsync(["book", "--json"], TextWriter.Null,
      TextWriter.Null, CancellationToken.None, new StringReader("2\n"));
    Assert.Equal(CliExitCode.OrderingDecisionRequired, result);
    Assert.Equal(1, service.Calls);
  }

  [Fact]
  public async Task CancellationStopsAConsoleStyleBlockingPromptWithoutWaitingForInput()
  {
    var service = new DecisionService();
    using var input = new BlockingReader();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var result = await new CliApplication(new Store(), service).RunAsync(
      ["book"], TextWriter.Null, TextWriter.Null, cancellation.Token, input);
    Assert.Equal(CliExitCode.Cancelled, result);
    Assert.Equal(1, service.Calls);
  }

  private sealed class BlockingReader : TextReader
  {
    private readonly ManualResetEventSlim _release = new();
    public override string? ReadLine() { _release.Wait(); return null; }
    protected override void Dispose(bool disposing) { if (disposing) _release.Set(); base.Dispose(disposing); }
  }
  private sealed class Store : ISettingsStore
  {
    public string SettingsPath => "";
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(SettingsLoadCode.Valid, AppSettings.Defaults with { OutputDirectory = Path.GetTempPath() }, []));
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class DecisionService : IConversionService
  {
    public int Calls { get; private set; }
    public ConversionRequest? Request { get; private set; }
    public Task<ConversionServiceResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)
    {
      Calls++;
      Request = request;
      var candidates = new[] {
        new OrderCandidate(OrderCandidateId.NaturalPath, ["01.mp3", "02.mp3"], true, OrderConfidence.High, []),
        new OrderCandidate(OrderCandidateId.Metadata, ["02.mp3", "01.mp3"], true, OrderConfidence.High, []) };
      var analysis = new BookAnalysis(BookAnalysisStatus.NeedsDecision, Path.GetTempPath(), [],
        new OrderResolution(OrderStatus.NeedsDecision, null, candidates, []),
        new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
        new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0), []);
      return Task.FromResult(new ConversionServiceResult(Guid.NewGuid(), Calls == 1 ? ConversionTerminalStatus.DecisionRequired : ConversionTerminalStatus.Succeeded,
        ConversionStage.Analysis, analysis, null, null, null, null, [], [], null));
    }
  }
}
