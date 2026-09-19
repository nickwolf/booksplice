using System.Collections.ObjectModel;
using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.ViewModels;

public sealed class BookQueueItemViewModel(string source, AppSettings settings) : ObservableModel
{
  private string _status = "Queued";
  private string _details = "";
  private bool _busy;
  private double _percent;
  private string? _publishedPath;
  private AppSettings _settings = settings;
  public string Source { get; } = source;
  public string Name => Path.GetFileName(Source.TrimEnd(Path.DirectorySeparatorChar));
  public AppSettings Settings => _settings;
  public IReadOnlyList<QualityProfile> QualityProfiles { get; } = QualityProfileCatalog.Version1;
  public IReadOnlyList<ChannelPolicyOption> ChannelPolicies { get; } =
  [
    new(ChannelPolicy.PreserveSourceChannels, "Preserve source channels"),
    new(ChannelPolicy.ForceMono, "Force mono"),
    new(ChannelPolicy.ForceStereo, "Force stereo"),
  ];
  public IReadOnlyList<ValidationLevel> ValidationLevels { get; } = Enum.GetValues<ValidationLevel>();
  public IReadOnlyList<string> MetadataProfiles { get; } = ["GenericMp4", "NickMp3tag"];
  public string OutputDirectory { get => Settings.OutputDirectory; set => UpdateSettings(Settings with { OutputDirectory = value }); }
  public string QualityProfileId { get => Settings.QualityProfileId; set { if (QualityProfileCatalog.FindById(value) is not null) UpdateSettings(Settings with { QualityProfileId = value }); } }
  public ChannelPolicy ChannelPolicy { get => Settings.ChannelPolicy; set => UpdateSettings(Settings with { ChannelPolicy = value }); }
  public ValidationLevel ValidationLevel { get => Settings.ValidationLevel; set => UpdateSettings(Settings with { ValidationLevel = value }); }
  public string MetadataProfileId { get => Settings.MetadataProfileId; set { if (value is "GenericMp4" or "NickMp3tag") UpdateSettings(Settings with { MetadataProfileId = value }); } }
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

  public ConversionOptions CreateOptions(bool singleConversionJob = false)
  {
    var effectiveSettings = singleConversionJob ? Settings with { ConversionJobs = 1 } : Settings;
    var edits = Metadata.Where(field => field.IsEdited).ToDictionary(field => field.Field,
      field => string.IsNullOrWhiteSpace(field.Value) ? MetadataEdit.Clear() : MetadataEdit.Set(field.Value));
    return new ConversionOptions(effectiveSettings, QualityProfileCatalog.FindById(effectiveSettings.QualityProfileId)
      ?? throw new InvalidOperationException("The selected quality profile is unavailable."), edits,
      selectedCoverHash: SelectedCover?.ContentHash,
      chapterTitles: Chapters.ToDictionary(chapter => chapter.Source, chapter => chapter.Title), omitCover: OmitCover);
  }

  private void UpdateSettings(AppSettings value)
  {
    if (value == _settings) return;
    _settings = value;
    foreach (var property in new[] { nameof(Settings), nameof(OutputDirectory), nameof(QualityProfileId), nameof(ChannelPolicy), nameof(ValidationLevel), nameof(MetadataProfileId) }) Changed(property);
  }

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
