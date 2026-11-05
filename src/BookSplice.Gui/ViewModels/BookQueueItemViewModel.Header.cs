using BookSplice.Core.Planning;

namespace BookSplice.Gui.ViewModels;

// The planned output shown above the tabs. Only the model writes it, and only for the selected book.
public sealed partial class BookQueueItemViewModel
{
  private BookHeader? _header;

  public BookHeader? Header
  {
    get => _header;
    internal set
    {
      if (Equals(_header, value)) return;
      _header = value;
      foreach (var property in new[] { nameof(Header), nameof(HeaderFileName), nameof(HeaderFullPath), nameof(HeaderAudioText), nameof(HeaderInfoText), nameof(HeaderRefusalText), nameof(HasHeaderPlan), nameof(HasHeaderRefusal), nameof(HasHeaderInfo) }) Changed(property);
    }
  }

  public string HeaderFileName => Header?.FileName ?? "";
  public string HeaderFullPath => Header?.FullPath ?? "";
  public string HeaderAudioText => Header?.AudioText ?? "";
  public string HeaderInfoText => Header?.InfoText ?? "";
  public string HeaderRefusalText => Header?.RefusalText ?? "";
  public bool HasHeaderPlan => Header?.HasPlan == true;
  public bool HasHeaderRefusal => Header?.HasRefusal == true;
  public bool HasHeaderInfo => Header?.HasInfo == true;

  public bool QualityProfileMissing => QualityProfileCatalog.FindById(Settings.QualityProfileId) is null;
  public bool MetadataProfileMissing => _profiles.Find(Settings.MetadataProfileId) is null;
  public bool IsProfileBlocked => BlockReason is not null;
}
