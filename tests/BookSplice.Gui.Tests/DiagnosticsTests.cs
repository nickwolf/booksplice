using BookSplice.Core.Analysis;
using BookSplice.Core.Chapters;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Settings;
using BookSplice.Gui.Diagnostics;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class DiagnosticsTests
{
  [Fact]
  public void ExportIncludesUsefulStateAndRedactsLocalPaths()
  {
    var item = new BookQueueItemViewModel(@"C:\Private Library\Secret Book", AppSettings.Defaults);
    typeof(BookQueueItemViewModel).GetProperty(nameof(BookQueueItemViewModel.Status))!.SetValue(item, "Failed");
    typeof(BookQueueItemViewModel).GetProperty(nameof(BookQueueItemViewModel.Details))!.SetValue(item,
      @"Could not read C:\Private Library\Secret Book\01.mp3. Output C:\Private Output\Secret.m4b");

    var text = DiagnosticsTextBuilder.Build(item);

    Assert.Contains("Status: Failed", text);
    Assert.Contains("Validation: Lightweight", text);
    Assert.Contains("[path]", text);
    Assert.DoesNotContain("Private Library", text, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Secret Book", text, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Private Output", text, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ExportRedactsPrivateTrackFilenamesFromAnalysisDiagnostics()
  {
    const string source = @"C:\Private Library\Secret Book";
    const string track = @"C:\Private Library\Secret Book\Secret Track Title.mp3";
    var item = new BookQueueItemViewModel(source, AppSettings.Defaults);
    var file = new SourceFile(track, "Secret Track Title.mp3",
      new MediaProbeResult([], [], [], new TagCollection([]), new Dictionary<string, string>(), [], 1));
    var analysis = new BookAnalysis(BookAnalysisStatus.Invalid, source, [file], null,
      new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()),
      new CoverDiscoveryResult([], [], null), ChapterPlan.Valid([], 0),
      [new AnalysisDiagnostic("test", AnalysisDiagnosticSeverity.Error, $"Could not read {track}.")]);
    typeof(BookQueueItemViewModel).GetMethod("SetAnalysis", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(item, [analysis]);

    var text = DiagnosticsTextBuilder.Build(item);

    Assert.Contains("[path]", text);
    Assert.DoesNotContain("Secret Track Title", text, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ExportRedactsPrivateTrackFilenameWithoutAnalysis()
  {
    const string source = @"C:\Private Library\Secret Book";
    var item = new BookQueueItemViewModel(source, AppSettings.Defaults);
    typeof(BookQueueItemViewModel).GetProperty(nameof(BookQueueItemViewModel.Details))!.SetValue(item,
      @"Could not find file 'C:\Private Library\Secret Book\Secret Track Title.mp3'.");

    var text = DiagnosticsTextBuilder.Build(item);

    Assert.Contains("[path]", text);
    Assert.DoesNotContain("Private Library", text, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Secret Track Title", text, StringComparison.OrdinalIgnoreCase);
  }
}
