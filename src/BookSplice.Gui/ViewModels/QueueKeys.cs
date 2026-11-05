using System.Windows.Input;

namespace BookSplice.Gui.ViewModels;

public enum QueueAction { None, MoveUp, MoveDown, Remove }

public static class QueueKeys
{
  public static QueueAction Resolve(Key key, Key systemKey, ModifierKeys modifiers)
  {
    var pressed = key == Key.System ? systemKey : key;
    return (pressed, modifiers) switch
    {
      (Key.Up, ModifierKeys.Alt) => QueueAction.MoveUp,
      (Key.Down, ModifierKeys.Alt) => QueueAction.MoveDown,
      (Key.Delete, ModifierKeys.None) => QueueAction.Remove,
      _ => QueueAction.None,
    };
  }
}
