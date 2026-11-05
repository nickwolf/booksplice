using System.IO;
using System.Windows.Media.Imaging;
using BookSplice.Core.Covers;

namespace BookSplice.Gui.ViewModels;

// Queue thumbnails come from the cover the book's analysis already chose, so nothing here probes a source file on its own.
public sealed partial class MainWindowViewModel
{
  private const int ThumbnailPixels = 80;

  // An embedded cover is read by running ffmpeg, so a long queue should not start one per book at once.
  private readonly SemaphoreSlim _thumbnailSlots = new(2);

  // Starts a background decode and returns at once. A newer cover choice makes an older decode drop its result, and the same cover is decoded once.
  // Not behind the update gate: it only reads source files, and an update needs an empty queue before it starts.
  private void LoadThumbnail(BookQueueItemViewModel item)
  {
    var cover = coverPayloads is null ? null : item.SelectedCover;
    if (cover is not null && cover.ContentHash == item.ThumbnailHash) return;
    var version = ++item.ThumbnailVersion;
    item.ThumbnailHash = cover?.ContentHash;
    if (cover is null)
    {
      if (item.Thumbnail is not null) item.Thumbnail = null;
      return;
    }
    _ = DecodeThumbnailAsync(item, cover, version);
  }

  private async Task DecodeThumbnailAsync(BookQueueItemViewModel item, CoverCandidate cover, int version)
  {
    await _thumbnailSlots.WaitAsync();
    try
    {
      var bitmap = await Task.Run(async () =>
      {
        await using var stream = await coverPayloads!.OpenReadAsync(new(cover.Origin, cover.SourcePath, cover.EmbeddedPictureIndex, cover.SourceIdentity), CancellationToken.None);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (cover.Height > cover.Width) image.DecodePixelHeight = ThumbnailPixels;
        else image.DecodePixelWidth = ThumbnailPixels;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
      });
      if (version == item.ThumbnailVersion) item.Thumbnail = bitmap;
    }
    // A thumbnail is only decoration, so whatever goes wrong while reading or decoding the cover just leaves the row without one.
    catch (Exception)
    {
      if (version != item.ThumbnailVersion) return;
      item.ThumbnailHash = null;
      item.Thumbnail = null;
    }
    finally { _ = _thumbnailSlots.Release(); }
  }
}
