using BookSplice.Core.Chapters;

namespace BookSplice.Gui.ViewModels;

public sealed class ChapterViewModel(ChapterEntry chapter) : ObservableModel
{
  private string _title = chapter.Title;
  public string Source { get; } = chapter.SourceRelativePath;
  public string Start { get; } = TimeSpan.FromMicroseconds(chapter.StartMicroseconds).ToString("g", System.Globalization.CultureInfo.CurrentCulture);
  public string Title { get => _title; set { _title = value; Changed(); } }
}
