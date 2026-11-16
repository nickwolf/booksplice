using System.Runtime.InteropServices;
using System.Text.Json;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.EditCopy;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tests.Execution;
using BookSplice.FFmpeg.Tools;

namespace BookSplice.FFmpeg.Tests.EditCopy;

public sealed class EditCopyServiceIntegrationTests
{
  [PinnedMediaFact]
  public async Task ATagsOnlyEditPublishesACopyWithTheSameAudioAndChapters()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3));
    var before = EditCopyMedia.Hash(source);
    var destination = root.GetPath("output", "Edited.m4b");
    var service = Service(tools, root);

    var result = await service.CreateAsync(Request(source, destination, tags: new() { [SemanticField.BookTitle] = "New title", [SemanticField.Author] = "New author", [SemanticField.Comment] = "" }), CancellationToken.None);

    Assert.True(result.Status == EditCopyStatus.Published, Describe(result));
    Assert.Equal(Path.GetFullPath(destination), result.PublishedPath);
    Assert.All(result.Checks.Where(check => check.Required), check => Assert.True(check.Passed, check.Code));
    Assert.Equal(await service.ReadPacketDigestAsync(source, CancellationToken.None), await service.ReadPacketDigestAsync(destination, CancellationToken.None));
    Assert.Equal(await service.ReadChaptersAsync(source, CancellationToken.None), await service.ReadChaptersAsync(destination, CancellationToken.None));
    var items = Mp4MetadataBoxes.ReadItems(destination);
    Assert.Equal("New title", Text(items, "\u00A9nam"));
    Assert.Equal("New title", Text(items, "\u00A9alb"));
    Assert.Equal("New author", Text(items, "\u00A9ART"));
    Assert.Equal("New author", Text(items, "aART"));
    Assert.Equal("Test Narrator", Text(items, "\u00A9wrt"));
    Assert.DoesNotContain(items, item => item.Type == "\u00A9cmt" || item.IsITunesFreeform("comment"));
    Assert.Equal("Keep this one", Assert.Single(Assert.Single(items, item => item.IsITunesFreeform("BOOKSPLICE_NOTE")).Data).Text);
    Assert.Equal(Covers(source), Covers(destination));
    Assert.Equal(before, EditCopyMedia.Hash(source));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
  }

  [PinnedMediaFact]
  public async Task AChapterTitleEditRemuxesWithTheNewTitlesAndKeepsEverythingElse()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(4));
    var before = EditCopyMedia.Hash(source);
    var destination = root.GetPath("output", "Edited.m4b");
    var service = Service(tools, root);

    var result = await service.CreateAsync(Request(source, destination, tags: new() { [SemanticField.Genre] = "Horror" }, chapters: new() { [0] = "Prologue", [3] = "Épilogue" }), CancellationToken.None);

    Assert.True(result.Status == EditCopyStatus.Published, Describe(result));
    var original = await service.ReadRawChaptersAsync(source, CancellationToken.None);
    var copy = await service.ReadRawChaptersAsync(destination, CancellationToken.None);
    Assert.Equal(["Prologue", "Chapter 2", "Chapter 3", "Épilogue"], copy.Select(chapter => chapter.Title));
    Assert.Equal(Times(original), Times(copy));
    Assert.Equal(await service.ReadPacketDigestAsync(source, CancellationToken.None), await service.ReadPacketDigestAsync(destination, CancellationToken.None));
    Assert.NotEmpty(Covers(source));
    Assert.Equal(Covers(source), Covers(destination));
    var items = Mp4MetadataBoxes.ReadItems(destination);
    Assert.Equal("Horror", Text(items, "\u00A9gen"));
    Assert.Equal("Test Book", Text(items, "\u00A9nam"));
    Assert.Equal("Keep this one", Assert.Single(Assert.Single(items, item => item.IsITunesFreeform("BOOKSPLICE_NOTE")).Data).Text);
    Assert.Equal("Freeform comment", Assert.Single(Assert.Single(items, item => item.IsITunesFreeform("comment")).Data).Text);
    Assert.Equal("eng", Assert.Single(Assert.Single(items, item => item.IsITunesFreeform("language")).Data).Text);
    Assert.Equal(Mp4MetadataBoxes.ReadMajorBrand(source), Mp4MetadataBoxes.ReadMajorBrand(destination));
    Assert.Equal(await StreamsAsync(tools, source), await StreamsAsync(tools, destination));
    // The pad added for TagLib's sake is taken off again.
    Assert.Equal("moov", EditCopyMedia.LastTopLevelType(destination));
    Assert.Equal(before, EditCopyMedia.Hash(source));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
  }

  [PinnedMediaFact]
  public async Task OtherUserDataAndAMoovLevelMetaBoxSurviveBothRoutesByteForByte()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateWithUserDataAsync(root.CreateFolder("books"), tools);
    var before = EditCopyMedia.Hash(source);
    var original = Mp4MetadataBoxes.ReadUserData(source);
    Assert.Contains(original.UdtaChildren, box => box.Type == "Xtra");
    Assert.NotNull(original.MoovMeta);
    var service = Service(tools, root);

    var tagsOnly = await service.CreateAsync(Request(source, root.GetPath("output", "Tags.m4b"), tags: new() { [SemanticField.BookTitle] = "New title" }), CancellationToken.None);
    var remuxed = await service.CreateAsync(Request(source, root.GetPath("output", "Chapters.m4b"), chapters: new() { [1] = "Renamed" }), CancellationToken.None);

    foreach (var result in new[] { tagsOnly, remuxed })
    {
      Assert.True(result.Status == EditCopyStatus.Published, Describe(result));
      Assert.Contains(result.Checks, check => check.Code == EditCopyCodes.CheckMetadataBoxes && check.Passed && check.Required);
      var copy = Mp4MetadataBoxes.ReadUserData(result.PublishedPath!);
      Assert.Equal(Convert.ToHexString(Assert.Single(original.UdtaChildren, box => box.Type == "Xtra").Raw), Convert.ToHexString(Assert.Single(copy.UdtaChildren, box => box.Type == "Xtra").Raw));
      Assert.Equal(Convert.ToHexString(original.MoovMeta!), Convert.ToHexString(copy.MoovMeta!));
    }
    Assert.Equal("Renamed", (await service.ReadChaptersAsync(remuxed.PublishedPath!, CancellationToken.None))[1].Title);
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task ADroppedUserDataBoxFailsValidationAndPublishesNothing()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateWithUserDataAsync(root.CreateFolder("books"), tools);
    var before = EditCopyMedia.Hash(source);
    var service = Service(tools, root);
    service.BeforeValidation = (copy, _) =>
    {
      var data = Mp4MetadataBoxes.ReadUserData(copy);
      Mp4MetadataBoxes.RestoreUserData(copy, data with { UdtaChildren = data.UdtaChildren.Where(box => box.Type != "Xtra").ToArray() });
      return Task.CompletedTask;
    };

    var result = await service.CreateAsync(Request(source, root.GetPath("output", "Edited.m4b"), chapters: new() { [1] = "Renamed" }), CancellationToken.None);

    Assert.Equal(EditCopyStatus.ValidationFailed, result.Status);
    Assert.Contains(result.Checks, check => check.Code == EditCopyCodes.CheckMetadataBoxes && !check.Passed);
    Assert.Empty(EditCopyMedia.Files(root.GetPath("output")));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task FiveHundredChaptersAndALongNonAsciiTitleSurviveARemux()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var longTitle = string.Concat(Enumerable.Repeat("Ünïcödé ", 38)).Trim()[..300];
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(500, index => index == 10 ? longTitle : $"Chapter {index + 1}"), seconds: 50);
    var before = EditCopyMedia.Hash(source);
    var destination = root.GetPath("output", "Edited.m4b");
    var service = Service(tools, root);
    var edited = "Ō" + longTitle[1..];

    var result = await service.CreateAsync(Request(source, destination, chapters: new() { [499] = edited }), CancellationToken.None);

    Assert.True(result.Status == EditCopyStatus.Published, Describe(result));
    var original = await service.ReadRawChaptersAsync(source, CancellationToken.None);
    var copy = await service.ReadRawChaptersAsync(destination, CancellationToken.None);
    Assert.Equal(500, copy.Count);
    Assert.Equal(300, longTitle.Length);
    Assert.Equal(longTitle, copy[10].Title);
    Assert.Equal(edited, copy[499].Title);
    Assert.Equal(original.Take(499).Select(chapter => chapter.Title), copy.Take(499).Select(chapter => chapter.Title));
    Assert.Equal(Times(original), Times(copy));
    Assert.Equal(await service.ReadPacketDigestAsync(source, CancellationToken.None), await service.ReadPacketDigestAsync(destination, CancellationToken.None));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task AnExistingDestinationGetsANumberedCopyAndStaysByteIdentical()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3));
    var before = EditCopyMedia.Hash(source);
    var existing = await EditCopyMedia.GenerateAsync(root.CreateFolder("output"), tools, EditCopyMedia.Titles(2), name: "Edited.m4b");
    var existingHash = EditCopyMedia.Hash(existing);

    var result = await Service(tools, root).CreateAsync(Request(source, existing, tags: new() { [SemanticField.BookTitle] = "New title" }), CancellationToken.None);

    Assert.True(result.Status == EditCopyStatus.Published, Describe(result));
    Assert.Equal(root.GetPath("output", "Edited (2).m4b"), result.PublishedPath);
    Assert.Equal(existingHash, EditCopyMedia.Hash(existing));
    Assert.Equal("New title", Text(Mp4MetadataBoxes.ReadItems(result.PublishedPath!), "\u00A9nam"));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task AHardLinkToTheSourceIsRejectedAndNothingIsWritten()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3));
    var before = EditCopyMedia.Hash(source);
    var link = Path.Combine(root.CreateFolder("output"), "Linked.m4b");
    Assert.True(CreateHardLink(link, source, IntPtr.Zero), $"CreateHardLink failed with {Marshal.GetLastPInvokeError()}.");

    var result = await Service(tools, root).CreateAsync(Request(source, link, tags: new() { [SemanticField.BookTitle] = "New title" }), CancellationToken.None);

    Assert.Equal(EditCopyStatus.InvalidInput, result.Status);
    Assert.Equal(EditCopyCodes.DestinationIsSource, Assert.Single(result.Diagnostics).Code);
    Assert.Equal([link], EditCopyMedia.Files(root.GetPath("output")));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task CancellingDuringTheRemuxLeavesNoTemporaryFilesAndPublishesNothing()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3), seconds: 120);
    var before = EditCopyMedia.Hash(source);
    var destination = root.GetPath("output", "Edited.m4b");
    using var cancellation = new CancellationTokenSource();
    var runner = new CancelOnRemuxRunner(new ProcessRunner(), tools.FFmpegPath, cancellation);
    var service = new EditCopyService(runner, tools, root.GetPath("temp"));

    var result = await service.CreateAsync(Request(source, destination, chapters: new() { [1] = "Renamed" }), cancellation.Token);

    Assert.True(runner.RemuxStarted);
    Assert.Equal(EditCopyStatus.Cancelled, result.Status);
    Assert.Null(result.PublishedPath);
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("output")));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task AFailedValidationPublishesNothingAndLeavesAnExistingOutputUntouched()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3));
    var before = EditCopyMedia.Hash(source);
    var existing = await EditCopyMedia.GenerateAsync(root.CreateFolder("output"), tools, EditCopyMedia.Titles(2), name: "Edited.m4b");
    var existingHash = EditCopyMedia.Hash(existing);
    var service = Service(tools, root);
    service.BeforeValidation = (copy, _) =>
    {
      Mp4MetadataWriter.Edit(copy, new Dictionary<string, string> { ["BOOKSPLICE_NOTE"] = "Changed behind the edit's back" });
      return Task.CompletedTask;
    };

    var result = await service.CreateAsync(Request(source, existing, tags: new() { [SemanticField.BookTitle] = "New title" }), CancellationToken.None);

    Assert.Equal(EditCopyStatus.ValidationFailed, result.Status);
    Assert.Contains(result.Checks, check => check.Code == EditCopyCodes.CheckUntouchedTags && !check.Passed);
    Assert.Null(result.PublishedPath);
    Assert.Equal([existing], EditCopyMedia.Files(root.GetPath("output")));
    Assert.Equal(existingHash, EditCopyMedia.Hash(existing));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task ASourceThatFailsARequiredCheckIsNotEdited()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3));
    var bytes = await File.ReadAllBytesAsync(source);
    await File.WriteAllBytesAsync(source, bytes.AsMemory(0, bytes.Length * 7 / 10));
    var before = EditCopyMedia.Hash(source);

    var result = await Service(tools, root).CreateAsync(Request(source, root.GetPath("output", "Edited.m4b"), tags: new() { [SemanticField.BookTitle] = "New title" }), CancellationToken.None);

    Assert.Equal(EditCopyStatus.InvalidInput, result.Status);
    Assert.Equal(EditCopyCodes.SourceCheckFailed, Assert.Single(result.Diagnostics).Code);
    Assert.False(Directory.Exists(root.GetPath("output")));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  private static EditCopyService Service(MediaToolSet tools, TemporaryFolder root) => new(new ProcessRunner(), tools, root.GetPath("temp"));

  private static EditCopyRequest Request(string source, string destination, Dictionary<SemanticField, string>? tags = null, Dictionary<int, string>? chapters = null)
    => new(source, tags ?? [], chapters ?? [], destination, MetadataProfiles.GenericMp4);

  // Exact instants. The muxer may store them in a finer time base than the original's (1/4410000 for 1/44100).
  private static (decimal Start, decimal End)[] Times(IReadOnlyList<EditCopyService.RawChapter> chapters)
    => chapters.Select(chapter => ((decimal)chapter.Start * chapter.TimeBase.Numerator / chapter.TimeBase.Denominator, (decimal)chapter.End * chapter.TimeBase.Numerator / chapter.TimeBase.Denominator)).ToArray();

  private static string Text(IReadOnlyList<Mp4MetadataItem> items, string type) => Assert.Single(Assert.Single(items, item => item.Type == type).Data).Text;

  private static string[] Covers(string path)
    => Mp4MetadataBoxes.ReadItems(path).Where(item => item.Type == "covr").SelectMany(item => item.Data).Select(data => Convert.ToHexString(data.Payload)).ToArray();

  private static async Task<string[]> StreamsAsync(MediaToolSet tools, string path)
  {
    var result = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFprobePath, ["-v", "error", "-show_entries", "stream=codec_type,codec_name:stream_disposition=attached_pic", "-of", "json", path]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    using var document = JsonDocument.Parse(result.StandardOutput);
    return document.RootElement.GetProperty("streams").EnumerateArray()
      .Select(stream => $"{stream.GetProperty("codec_type").GetString()}/{stream.GetProperty("codec_name").GetString()}/{stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32()}").ToArray();
  }

  private static string Describe(EditCopyResult result)
    => string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}").Concat(result.Checks.Where(check => !check.Passed).Select(check => $"{check.Code}: {check.Message}")));

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

  // Cancels the edit as soon as the ffmpeg remux process has started, so the cancel lands while ffmpeg is writing the copy.
  private sealed class CancelOnRemuxRunner(IProcessRunner inner, string ffmpegPath, CancellationTokenSource cancellation) : IProcessRunner
  {
    public bool RemuxStarted { get; private set; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
    {
      if (!string.Equals(spec.FileName, ffmpegPath, StringComparison.OrdinalIgnoreCase)) return await inner.RunAsync(spec, progress, cancellationToken);
      ProcessRunner.ProcessStarted.Value = _ => { RemuxStarted = true; cancellation.Cancel(); };
      try { return await inner.RunAsync(spec, progress, cancellationToken); }
      finally { ProcessRunner.ProcessStarted.Value = null; }
    }
  }
}
