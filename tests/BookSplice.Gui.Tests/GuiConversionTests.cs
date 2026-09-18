using System.Security.Cryptography;
using BookSplice.Core.Metadata;
using BookSplice.Core.Settings;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tools;
using BookSplice.Gui.Bootstrap;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Tests;

public sealed class GuiConversionTests
{
  [PinnedGuiMediaFact]
  public async Task GuiHostConvertsGeneratedBookWithEmbeddedCoverAndEditsWithoutChangingSource()
  {
    var root = Directory.CreateTempSubdirectory("booksplice-gui-").FullName;
    try
    {
      var tools = new MediaToolLocator(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")!).Resolve();
      var runner = new ProcessRunner();
      var source = Path.Combine(root, "source.mp3");
      var cover = Path.Combine(root, "art.jpg");
      var generated = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath,
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x64", "-frames:v", "1", "-y", cover]), null, CancellationToken.None);
      Assert.Equal(0, generated.ExitCode);
      generated = await runner.RunAsync(new ProcessSpec(tools.FFmpegPath,
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-i", cover,
         "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "copy", "-disposition:v:0", "attached_pic", "-y", source]), null, CancellationToken.None);
      Assert.Equal(0, generated.ExitCode);
      File.Delete(cover);
      var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
      var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
      var services = new AppHost(Path.Combine(root, "local")).CreateServices(Path.GetDirectoryName(tools.FFmpegPath)!);
      var model = new MainWindowViewModel(services.Analyzer, services.Conversion, AppSettings.Defaults with { OutputDirectory = output });
      await model.AddAsync(source);
      var item = Assert.Single(model.Items);
      Assert.True(item.CanConvert, item.Details);
      Assert.Single(item.Covers);
      item.Metadata.Single(field => field.Field == SemanticField.BookTitle).Value = "Generated title";
      item.Chapters[0].Title = "Opening";
      await model.ConvertAsync(item);
      Assert.True(item.Status == "Complete", item.Details);
      Assert.True(File.Exists(item.PublishedPath));
      var probed = await new FFprobeMediaProbe(runner, tools).ProbeAsync(item.PublishedPath!);
      Assert.Single(probed.AttachedPictures);
      Assert.Equal("Opening", Assert.Single(probed.Chapters).RawTags["title"]);
      Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }
    finally { Directory.Delete(root, true); }
  }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PinnedGuiMediaFactAttribute : FactAttribute
{
  public PinnedGuiMediaFactAttribute()
  {
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR")))
      Skip = "Pinned FFmpeg tools are unavailable.";
  }
}
