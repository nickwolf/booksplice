using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BookSplice.Gui.ViewModels;

public abstract class ObservableModel : INotifyPropertyChanged
{
  public event PropertyChangedEventHandler? PropertyChanged;
  protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
