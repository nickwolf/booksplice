using System.Collections.ObjectModel;
using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Ordering;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.ViewModels;

public sealed class BookQueueItemViewModel(string source, AppSettings settings) : ObservableModel
{
  private string _status = "Queued";
  private string _details = "";
  private bool _busy;
  private double _percent;
  private string? _publishedPath;
  public string Source { get; } = source;
  public string Name => Path.GetFileName(Source.TrimEnd(Path.DirectorySeparatorChar));
  public AppSettings Settings { get; set; } = settings;
  public BookAnalysis? Analysis { get; private set; }
  public OrderCandidateId? SelectedOrder { get; set; }
  public CoverCandidate? SelectedCover { get; set; }
  public bool OmitCover { get; set; }
  public ObservableCollection<ChapterViewModel> Chapters { get; } = [];
  public ObservableCollection<MetadataFieldViewModel> Metadata { get; } = [];
  public IReadOnlyList<OrderCandidate> OrderCandidates => Analysis?.OrderResolution?.Candidates.Where(candidate => candidate.IsCredible).ToArray() ?? [];
  public IReadOnlyList<CoverCandidate> Covers => Analysis?.Cover.Candidates ?? [];
  public IReadOnlyList<string> OrderedFiles => Analysis?.OrderedFiles.Select(file => file.RelativePath).ToArray() ?? [];
  public string Summary => Analysis is null ? "" : $"{Analysis.OrderedFiles.Count} files | {TimeSpan.FromMicroseconds(Analysis.Chapters.TotalDurationMicroseconds):g} | {string.Join(", ", Analysis.OrderedFiles.SelectMany(file => file.ProbeResult.AudioStreams).Select(stream => stream.CodecName).Distinct())}";
  public string Status { get => _status; internal set { _status = value; Changed(); Changed(nameof(CanConvert)); } }
  public string Details { get => _details; internal set { _details = value; Changed(); } }
  public double Percent { get => _percent; internal set { _percent = value; Changed(); } }
  public string? PublishedPath { get => _publishedPath; internal set { _publishedPath = value; Changed(); } }
  public bool IsBusy { get => _busy; internal set { _busy = value; Changed(); Changed(nameof(CanEdit)); Changed(nameof(CanRemove)); Changed(nameof(CanConvert)); } }
  public bool CanRemove => !IsBusy;
  public bool CanEdit => !IsBusy && Status != "Complete";
  public bool CanConvert => !IsBusy && Analysis?.Status == BookAnalysisStatus.Ready && Status != "Complete";
  internal CancellationTokenSource? Cancellation { get; set; }
  public void Cancel() => Cancellation?.Cancel();

  internal void SetAnalysis(BookAnalysis analysis)
  {
    Analysis = analysis;
    var edits = Metadata.Where(field => field.IsEdited).ToDictionary(field => field.Field, field => field.Value);
    Metadata.Clear();
    foreach (var field in Enum.GetValues<Core.Metadata.SemanticField>().Where(field => field is not (Core.Metadata.SemanticField.Track or Core.Metadata.SemanticField.Disc)))
      Metadata.Add(new(field, analysis.BookMetadata.Get(field)));
    foreach (var field in Metadata) if (edits.TryGetValue(field.Field, out var edited)) field.Value = edited;
    var chapterEdits = Chapters.ToDictionary(chapter => chapter.Source, chapter => chapter.Title, StringComparer.Ordinal);
    Chapters.Clear();
    foreach (var chapter in analysis.Chapters.Entries)
      Chapters.Add(new ChapterViewModel(chapter) { Title = chapterEdits.GetValueOrDefault(chapter.SourceRelativePath, chapter.Title) });
    SelectedCover = analysis.Cover.Candidates.FirstOrDefault(cover => cover.ContentHash == SelectedCover?.ContentHash) ?? analysis.Cover.Selected;
    Status = analysis.Status switch
    {
      BookAnalysisStatus.NeedsDecision => "Choose track order",
      BookAnalysisStatus.Ready => "Ready",
      BookAnalysisStatus.InspectOnly => "M4B inspection only",
      _ => "Invalid input"
    };
    Details = string.Join(Environment.NewLine, analysis.Diagnostics.Select(diagnostic => diagnostic.Message));
    foreach (var property in new[] { nameof(Analysis), nameof(OrderCandidates), nameof(Covers), nameof(SelectedCover), nameof(OrderedFiles), nameof(Summary), nameof(CanConvert) }) Changed(property);
  }
}
