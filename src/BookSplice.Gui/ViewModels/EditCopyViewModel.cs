using System.Collections.ObjectModel;
using System.Globalization;
using BookSplice.Core.Analysis;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;

namespace BookSplice.Gui.ViewModels;

public sealed class EditCopyFieldViewModel(EditCopyViewModel owner, SemanticField field, string original, bool isReadOnly = false) : ObservableModel
{
  private string _value = original;
  public SemanticField Field { get; } = field;
  public string Label { get; } = EditCopyPreview.Label(field);
  public string Original { get; } = original;
  // The profile stores this field as a number, which an edit copy can't write.
  public bool IsReadOnly { get; } = isReadOnly;
  public string Value { get => _value; set { if (IsReadOnly || _value == value) return; _value = value ?? ""; Changed(); owner.Refresh(); } }
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
  private readonly IReadOnlyList<EditCopyChapter> _chapters;
  private IReadOnlyList<string> _preview = [];

  // Chapters are the edit service's own read, not the probe's, so titles are whole and match what the edit changes.
  public EditCopyViewModel(string sourcePath, MediaProbeResult probe, IReadOnlyList<EditCopyChapter> chapters, MetadataProfile profile)
  {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(chapters);
    ArgumentNullException.ThrowIfNull(profile);
    SourcePath = sourcePath;
    _probe = probe;
    _chapters = chapters.ToArray();
    Profile = profile;
    var fields = profile.Mappings.Where(mapping => mapping.Keys.Count > 0).Select(mapping => mapping.Field).Distinct().Order()
      .Select(field => new EditCopyFieldViewModel(this, field, EditCopyPreview.OriginalValue(probe, profile, field) ?? "", !EditCopyTagMapping.CanEdit(profile, field))).ToArray();
    Fields = new ReadOnlyCollection<EditCopyFieldViewModel>(fields);
    Chapters = new ReadOnlyCollection<EditCopyChapterViewModel>(_chapters
      .Select((chapter, index) => new EditCopyChapterViewModel(this, index, DurationText.Format(TimeSpan.FromMicroseconds(chapter.StartMicroseconds)), chapter.Title)).ToArray());
    Refresh();
  }

  public string SourcePath { get; }
  public MetadataProfile Profile { get; }
  public IReadOnlyList<EditCopyFieldViewModel> Fields { get; }
  public IReadOnlyList<EditCopyChapterViewModel> Chapters { get; }
  public IReadOnlyList<string> Preview => _preview;
  public bool HasReadOnlyFields => Fields.Any(entry => entry.IsReadOnly);
  public bool HasChanges => Fields.Any(entry => entry.IsChanged) || Chapters.Any(chapter => chapter.IsChanged);
  public bool HasInvalidTitle => Chapters.Any(chapter => chapter.IsInvalid);
  public bool CanCreate => HasChanges && !HasInvalidTitle;
  public string PreviewPlaceholder => HasChanges ? "" : "Nothing has changed yet.";
  public string ProblemText => HasInvalidTitle ? "Every chapter needs a title." : "";

  // Only what differs from the file goes in. A field emptied by the user is sent as "", which removes it.
  // The titles the window started from go too, so the edit is refused if the file's chapters changed meanwhile.
  public EditCopyRequest CreateRequest(string destinationPath) => new(SourcePath,
    Fields.Where(entry => entry.IsChanged).ToDictionary(entry => entry.Field, entry => entry.Value),
    Chapters.Where(chapter => chapter.IsChanged).ToDictionary(chapter => chapter.Index, chapter => chapter.Title),
    destinationPath, Profile, _chapters.Select(chapter => chapter.Title).ToArray());

  internal void Refresh()
  {
    if (Fields is null || Chapters is null) return;
    _preview = EditCopyPreview.Describe(_probe, _chapters, CreateRequest(SourcePath));
    foreach (var property in new[] { nameof(Preview), nameof(HasChanges), nameof(HasInvalidTitle), nameof(CanCreate), nameof(PreviewPlaceholder), nameof(ProblemText) }) Changed(property);
  }
}
