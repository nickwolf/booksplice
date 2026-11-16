using System.Collections.ObjectModel;
using System.Globalization;
using BookSplice.Core.Analysis;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;

namespace BookSplice.Gui.ViewModels;

public sealed class EditCopyFieldViewModel(EditCopyViewModel owner, SemanticField field, string original) : ObservableModel
{
  private string _value = original;
  public SemanticField Field { get; } = field;
  public string Label { get; } = EditCopyPreview.Label(field);
  public string Original { get; } = original;
  public string Value { get => _value; set { if (_value == value) return; _value = value ?? ""; Changed(); owner.Refresh(); } }
  public bool IsChanged => !string.Equals(_value, Original, StringComparison.Ordinal);
}

public sealed class EditCopyChapterViewModel(EditCopyViewModel owner, int index, string start, string original) : ObservableModel
{
  private string _title = original;
  public int Index { get; } = index;
  public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);
  public string Start { get; } = start;
  public string Original { get; } = original;
  public string Title { get => _title; set { if (_title == value) return; _title = value ?? ""; Changed(); Changed(nameof(IsInvalid)); owner.Refresh(); } }
  public bool IsChanged => !string.Equals(_title, Original, StringComparison.Ordinal);
  // The engine refuses a blank title, so the window does too.
  public bool IsInvalid => string.IsNullOrWhiteSpace(_title) || _title.Contains('\0');
}

// The Edit a copy window's state: the file's current tags and chapter titles, what the user changed, and the change list that follows.
public sealed class EditCopyViewModel : ObservableModel
{
  private readonly MediaProbeResult _probe;
  private IReadOnlyList<string> _preview = [];

  public EditCopyViewModel(string sourcePath, MediaProbeResult probe, MetadataProfile profile)
  {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(profile);
    SourcePath = sourcePath;
    _probe = probe;
    Profile = profile;
    var fields = profile.Mappings.Where(mapping => mapping.Keys.Count > 0).Select(mapping => mapping.Field).Distinct().Order()
      .Select(field => new EditCopyFieldViewModel(this, field, EditCopyPreview.OriginalValue(probe, profile, field) ?? "")).ToArray();
    Fields = new ReadOnlyCollection<EditCopyFieldViewModel>(fields);
    Chapters = new ReadOnlyCollection<EditCopyChapterViewModel>(probe.Chapters
      .Select((chapter, index) => new EditCopyChapterViewModel(this, index, DurationText.Format(TimeSpan.FromSeconds((double)chapter.StartTime)), chapter.RawTags.GetValueOrDefault("title") ?? "")).ToArray());
    Refresh();
  }

  public string SourcePath { get; }
  public MetadataProfile Profile { get; }
  public IReadOnlyList<EditCopyFieldViewModel> Fields { get; }
  public IReadOnlyList<EditCopyChapterViewModel> Chapters { get; }
  public IReadOnlyList<string> Preview => _preview;
  public bool HasChanges => Fields.Any(entry => entry.IsChanged) || Chapters.Any(chapter => chapter.IsChanged);
  public bool HasInvalidTitle => Chapters.Any(chapter => chapter.IsInvalid);
  public bool CanCreate => HasChanges && !HasInvalidTitle;
  public string PreviewPlaceholder => HasChanges ? "" : "Nothing has changed yet.";
  public string ProblemText => HasInvalidTitle ? "Every chapter needs a title." : "";

  // Only what differs from the file goes in. A field emptied by the user is sent as "", which removes it.
  public EditCopyRequest CreateRequest(string destinationPath) => new(SourcePath,
    Fields.Where(entry => entry.IsChanged).ToDictionary(entry => entry.Field, entry => entry.Value),
    Chapters.Where(chapter => chapter.IsChanged).ToDictionary(chapter => chapter.Index, chapter => chapter.Title),
    destinationPath, Profile);

  internal void Refresh()
  {
    if (Fields is null || Chapters is null) return;
    _preview = EditCopyPreview.Describe(_probe, CreateRequest(SourcePath));
    foreach (var property in new[] { nameof(Preview), nameof(HasChanges), nameof(HasInvalidTitle), nameof(CanCreate), nameof(PreviewPlaceholder), nameof(ProblemText) }) Changed(property);
  }
}
