using System.Collections.ObjectModel;
using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Metadata;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;
using BookSplice.Gui.Notifications;

namespace BookSplice.Gui.ViewModels;

public sealed class BookQueueItemViewModel(string source, AppSettings settings, MetadataProfileCatalog? profiles = null) : ObservableModel
{
  private string _status = "Queued";
  private string _details = "";
  private bool _busy;
  private bool _waiting;
  private double _percent;
  private bool _progressIndeterminate;
  private bool _cancellationRequested;
  private string? _publishedPath;
  private ConversionResultDisplay? _result;
  private AppSettings _settings = settings;
  public string Source { get; } = source;
  public SourceSelection? Selection { get; init; }
  public string Name => Selection?.Name ?? Path.GetFileName(Source.TrimEnd(Path.DirectorySeparatorChar));
  public AppSettings Settings => _settings;
  public IReadOnlyList<QualityProfile> QualityProfiles { get; } = QualityProfileCatalog.Version1;
  public IReadOnlyList<ChannelPolicyOption> ChannelPolicies { get; } =
  [
    new(ChannelPolicy.PreserveSourceChannels, "Preserve source channels"),
    new(ChannelPolicy.ForceMono, "Force mono"),
    new(ChannelPolicy.ForceStereo, "Force stereo"),
  ];
  public IReadOnlyList<ValidationLevel> ValidationLevels { get; } = Enum.GetValues<ValidationLevel>();
  private readonly MetadataProfileCatalog _profiles = profiles ?? MetadataProfileCatalog.BuiltIn;
  public IReadOnlyList<string> MetadataProfiles { get; } = (profiles ?? MetadataProfileCatalog.BuiltIn).Profiles.Select(profile => profile.Name).ToArray();
  public string OutputDirectory { get => Settings.OutputDirectory; set => UpdateSettings(Settings with { OutputDirectory = value }); }
  public string QualityProfileId { get => Settings.QualityProfileId; set { if (QualityProfileCatalog.FindById(value) is not null) UpdateSettings(Settings with { QualityProfileId = value }); } }
  public ChannelPolicy ChannelPolicy { get => Settings.ChannelPolicy; set => UpdateSettings(Settings with { ChannelPolicy = value }); }
  public string OutputTemplate { get => Settings.OutputTemplate; set => UpdateSettings(Settings with { OutputTemplate = value.Trim() }); }
  public string TemplateHelp { get; } = OutputTemplateHelp.Text;
  public ValidationLevel ValidationLevel { get => Settings.ValidationLevel; set => UpdateSettings(Settings with { ValidationLevel = value }); }
  public string MetadataProfileId { get => Settings.MetadataProfileId; set { if (_profiles.Find(value) is not null) UpdateSettings(Settings with { MetadataProfileId = value }); } }
  public BookAnalysis? Analysis { get; private set; }
  public OrderCandidateId? SelectedOrder { get; set; }
  public ReviewedTrackOrder? ReviewedOrder { get; internal set; }
  public BookAnalysisOptions AnalysisOptions => new(SelectedOrder, ReviewedOrder: ReviewedOrder);
  public ObservableCollection<string> IncludedTracks { get; } = [];
  public ObservableCollection<string> ExcludedTracks { get; } = [];
  public bool IsTrackListEdited { get; private set; }
  public bool CanApplyTrackList => IncludedTracks.Count > 0;
  public CoverCandidate? SelectedCover { get; set; }
  public bool OmitCover { get; set; }
  public ObservableCollection<ChapterViewModel> Chapters { get; } = [];
  public ObservableCollection<MetadataFieldViewModel> Metadata { get; } = [];
  public IReadOnlyList<OrderCandidate> OrderCandidates => Analysis?.OrderResolution?.Candidates.Where(candidate => candidate.IsCredible).ToArray() ?? [];
  public IReadOnlyList<CoverCandidate> Covers => Analysis?.Cover.Candidates ?? [];
  public IReadOnlyList<string> OrderedFiles => Analysis?.OrderedFiles.Select(file => file.RelativePath).ToArray() ?? [];
  public string Summary => Analysis is null ? "" : $"{Analysis.OrderedFiles.Count} files{(Analysis.ExcludedFiles.Count > 0 ? $" ({Analysis.ExcludedFiles.Count} excluded)" : "")} | {TimeSpan.FromMicroseconds(Analysis.Chapters.TotalDurationMicroseconds):g} | {string.Join(", ", Analysis.OrderedFiles.SelectMany(file => file.ProbeResult.AudioStreams).Select(stream => stream.CodecName).Distinct())}";
  public string Status { get => _status; internal set { _status = value; Changed(); Changed(nameof(QueueStatus)); Changed(nameof(CanConvert)); } }
  public string QueueStatus => Result?.Title ?? (IsBusy && IsCancellationRequested ? $"{Status} · Cancellation requested" : Status);
  public ConversionResultDisplay? Result { get => _result; internal set { _result = value; Changed(); Changed(nameof(HasResult)); Changed(nameof(QueueStatus)); } }
  public bool HasResult => Result is not null;
  public string Details { get => _details; internal set { _details = value; Changed(); } }
  public double Percent { get => _percent; internal set { _percent = value; Changed(); } }
  public bool IsProgressIndeterminate { get => _progressIndeterminate; internal set { _progressIndeterminate = value; Changed(); } }
  public bool IsCancellationRequested { get => _cancellationRequested; internal set { _cancellationRequested = value; Changed(); Changed(nameof(QueueStatus)); } }
  public string? PublishedPath { get => _publishedPath; internal set { _publishedPath = value; Changed(); Changed(nameof(CanOpenOutput)); } }
  public bool CanOpenOutput => !IsBusy && PublishedPath is { } path && File.Exists(path);
  public bool IsBusy { get => _busy; internal set { _busy = value; Changed(); Changed(nameof(QueueStatus)); Changed(nameof(CanEdit)); Changed(nameof(CanRemove)); Changed(nameof(CanConvert)); Changed(nameof(CanOpenOutput)); Changed(nameof(CanCancel)); } }
  public bool IsWaiting { get => _waiting; internal set { _waiting = value; Changed(); Changed(nameof(CanCancel)); } }
  public bool CanCancel => IsBusy || IsWaiting;
  public bool CanRemove => !IsBusy;
  public bool CanEdit => !IsBusy && Status != "Complete";
  public bool CanConvert => !IsBusy && Analysis?.Status == BookAnalysisStatus.Ready && Status != "Complete";
  internal CancellationTokenSource? Cancellation { get; set; }
  // The last conversion's outcome, so a batch retry can pick only the books that failed.
  public BookOutcome? Outcome { get; internal set; }
  internal void MarkWaiting() { IsWaiting = true; Status = "Waiting"; }
  internal void CancelWaiting() { IsWaiting = false; Status = "Cancelled"; }
  public void Cancel()
  {
    if (IsWaiting) { CancelWaiting(); return; }
    if (Cancellation is not { } cancellation || Result is not null) return;
    IsCancellationRequested = true;
    cancellation.Cancel();
  }

  public ConversionOptions CreateOptions()
  {
    var edits = Metadata.Where(field => field.IsEdited).ToDictionary(field => field.Field,
      field => string.IsNullOrWhiteSpace(field.Value) ? MetadataEdit.Clear() : MetadataEdit.Set(field.Value));
    return new ConversionOptions(Settings, QualityProfileCatalog.FindById(Settings.QualityProfileId)
      ?? throw new InvalidOperationException("The selected quality profile is unavailable."), edits,
      selectedCoverHash: SelectedCover?.ContentHash,
      chapterTitles: Chapters.ToDictionary(chapter => chapter.Source, chapter => chapter.Title), omitCover: OmitCover);
  }

  private void UpdateSettings(AppSettings value)
  {
    if (value == _settings) return;
    _settings = value;
    foreach (var property in new[] { nameof(Settings), nameof(OutputDirectory), nameof(QualityProfileId), nameof(ChannelPolicy), nameof(ValidationLevel), nameof(MetadataProfileId), nameof(OutputTemplate) }) Changed(property);
  }

  // Copies the preset's values into this book. Later edits to the preset never reach it.
  public PresetApplyResult ApplyPreset(ConversionPreset preset)
  {
    if (!CanEdit) return new(null, [IsBusy ? "This book is converting. Wait for it to finish before applying a preset." : "This book is already finished, so a preset cannot change it."]);
    var result = PresetApplier.Apply(Settings, preset, _profiles);
    if (result.Settings is { } applied) UpdateSettings(applied);
    return result;
  }

  public void LoadTrackList(IEnumerable<string> included, IEnumerable<string> excluded)
  {
    IncludedTracks.Clear();
    foreach (var id in included) IncludedTracks.Add(id);
    ExcludedTracks.Clear();
    foreach (var id in excluded) ExcludedTracks.Add(id);
    SetTrackListEdited(false);
  }

  public bool MoveTrack(string id, int offset)
  {
    var index = IncludedTracks.IndexOf(id);
    var target = index + offset;
    if (!CanEdit || index < 0 || target < 0 || target >= IncludedTracks.Count) return false;
    IncludedTracks.Move(index, target);
    SetTrackListEdited(true);
    return true;
  }

  public bool ExcludeTrack(string id)
  {
    if (!CanEdit || !IncludedTracks.Remove(id)) return false;
    ExcludedTracks.Add(id);
    SetTrackListEdited(true);
    return true;
  }

  public bool IncludeTrack(string id)
  {
    if (!CanEdit || !ExcludedTracks.Remove(id)) return false;
    IncludedTracks.Add(id);
    SetTrackListEdited(true);
    return true;
  }

  public bool ApplyTrackAction(TrackListAction action, string id) => action switch
  {
    TrackListAction.MoveUp => MoveTrack(id, -1),
    TrackListAction.MoveDown => MoveTrack(id, 1),
    TrackListAction.Exclude => ExcludeTrack(id),
    TrackListAction.Include => IncludeTrack(id),
    _ => false,
  };

  // Keeps the reviewed choices for files that are still there; a new file is never included without a decision.
  private string ReconcileTrackList(IReadOnlyList<string> discovered)
  {
    var present = discovered.ToHashSet(StringComparer.Ordinal);
    var known = IncludedTracks.Concat(ExcludedTracks).ToHashSet(StringComparer.Ordinal);
    var included = IncludedTracks.Where(present.Contains).ToArray();
    var excluded = ExcludedTracks.Where(present.Contains).ToList();
    var added = discovered.Where(id => !known.Contains(id)).ToArray();
    excluded.AddRange(added);
    var removed = known.Count - (included.Length + excluded.Count - added.Length);
    LoadTrackList(included, excluded);
    return $"The track list was updated to match the source: {added.Length} new file(s) were added to the excluded list and {removed} missing file(s) were removed. Review the lists, then apply.";
  }

  public ReviewedTrackOrder CreateReviewedOrder() => new(IncludedTracks.ToArray(), ExcludedTracks.ToArray());

  private void SetTrackListEdited(bool edited)
  {
    IsTrackListEdited = edited;
    Changed(nameof(IsTrackListEdited));
    Changed(nameof(CanApplyTrackList));
  }

  internal void SetAnalysis(BookAnalysis analysis)
  {
    Analysis = analysis;
    string? trackListNote = null;
    if (analysis.OrderedFiles.Count > 0) LoadTrackList(analysis.OrderedFiles.Select(file => file.RelativePath), analysis.ExcludedFiles.Select(file => file.RelativePath));
    else if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code == "analysis.order-sources-changed")) trackListNote = ReconcileTrackList(analysis.DiscoveredFileIds);
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
    Details = string.Join(Environment.NewLine, analysis.Diagnostics.Select(diagnostic => diagnostic.Message).Concat(trackListNote is null ? [] : [trackListNote]));
    foreach (var property in new[] { nameof(Analysis), nameof(OrderCandidates), nameof(Covers), nameof(SelectedCover), nameof(OrderedFiles), nameof(Summary), nameof(CanConvert) }) Changed(property);
  }
}
