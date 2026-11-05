using System.Windows.Media;

namespace BookSplice.Gui.ViewModels;

// What the queue list shows for this book beyond its name and status: row action labels and the cover thumbnail.
public sealed partial class BookQueueItemViewModel
{
  private bool _canMoveUp;
  private bool _canMoveDown;
  private ImageSource? _thumbnail;
  internal int ThumbnailVersion;
  internal string? ThumbnailHash;

  public string MoveUpText => $"Move {Name} up";
  public string MoveDownText => $"Move {Name} down";
  public string RemoveText => $"Remove {Name}";
  public string MoveUpTip => MoveUpText + " (Alt+Up)";
  public string MoveDownTip => MoveDownText + " (Alt+Down)";
  public string RemoveTip => RemoveText + " (Delete)";

  // The first book has nothing to move ahead of, and the last has nothing to move behind.
  public bool CanMoveUp => _canMoveUp;
  public bool CanMoveDown => _canMoveDown;

  internal void SetPosition(int index, int count)
  {
    var up = index > 0;
    var down = index >= 0 && index < count - 1;
    if (up != _canMoveUp) { _canMoveUp = up; Changed(nameof(CanMoveUp)); }
    if (down != _canMoveDown) { _canMoveDown = down; Changed(nameof(CanMoveDown)); }
  }

  // Small and frozen, decoded from the cover the Cover tab already knows about. Null while there is none.
  public ImageSource? Thumbnail { get => _thumbnail; internal set { _thumbnail = value; Changed(); Changed(nameof(HasThumbnail)); } }
  public bool HasThumbnail => _thumbnail is not null;
}
