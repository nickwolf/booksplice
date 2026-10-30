using System.Windows.Input;

namespace BookSplice.Gui.ViewModels;

public enum TrackListAction { None, MoveUp, MoveDown, Exclude, Include }

public static class TrackListKeys
{
  public static TrackListAction Resolve(Key key, Key systemKey, ModifierKeys modifiers, bool excludedList)
  {
    var pressed = key == Key.System ? systemKey : key;
    if (excludedList) return pressed == Key.Enter && modifiers == ModifierKeys.None ? TrackListAction.Include : TrackListAction.None;
    return (pressed, modifiers) switch
    {
      (Key.Up, ModifierKeys.Alt) => TrackListAction.MoveUp,
      (Key.Down, ModifierKeys.Alt) => TrackListAction.MoveDown,
      (Key.Delete, ModifierKeys.None) => TrackListAction.Exclude,
      _ => TrackListAction.None,
    };
  }
}
