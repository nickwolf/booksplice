using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.EditCopy;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Probing;
using BookSplice.FFmpeg.Tests.Execution;

namespace BookSplice.FFmpeg.Tests.EditCopy;

public sealed class EditCopyChapterTests
{
  [PinnedMediaFact]
  public async Task ReadChaptersReturnsEachStartAndWholeTitle()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.Path, tools, ["One", "Two", "Three"]);

    var chapters = await new EditCopyService(new ProcessRunner(), tools, root.GetPath("temp")).ReadChaptersAsync(source, CancellationToken.None);

    Assert.Equal([new EditCopyChapter(0, "One"), new EditCopyChapter(2_000_000, "Two"), new EditCopyChapter(4_000_000, "Three")], chapters);
  }

  [PinnedMediaFact]
  public async Task ALongTitleTheProbeCutsShortIsReadWholeAndAnEditToItPublishesWhole()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var longTitle = string.Concat(Enumerable.Repeat("é", 150));
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, ["One", longTitle, "Three"]);
    var before = EditCopyMedia.Hash(source);
    var service = new EditCopyService(new ProcessRunner(), tools, root.GetPath("temp"));

    // The probe the rest of the app uses reads chpl, which keeps 255 of the title's 300 bytes.
    var probe = await new FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(source);
    Assert.NotEqual(longTitle, probe.Chapters[1].RawTags["title"]);
    var chapters = await service.ReadChaptersAsync(source, CancellationToken.None);
    Assert.Equal(longTitle, chapters[1].Title);

    var edited = string.Concat(Enumerable.Repeat("ü", 150));
    var request = new EditCopyRequest(source, new Dictionary<SemanticField, string>(), new Dictionary<int, string> { [1] = edited }, root.GetPath("output", "Edited.m4b"), MetadataProfiles.GenericMp4, chapters.Select(chapter => chapter.Title).ToArray());
    var result = await service.CreateAsync(request, CancellationToken.None);

    Assert.True(result.Status == EditCopyStatus.Published, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code).Concat(result.Checks.Where(check => !check.Passed).Select(check => check.Code))));
    Assert.Equal(["One", edited, "Three"], (await service.ReadChaptersAsync(result.PublishedPath!, CancellationToken.None)).Select(chapter => chapter.Title));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task AnEditMadeAgainstOtherChapterTitlesIsRefused()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, ["One", "Two", "Three"]);
    var before = EditCopyMedia.Hash(source);
    var service = new EditCopyService(new ProcessRunner(), tools, root.GetPath("temp"));
    var request = new EditCopyRequest(source, new Dictionary<SemanticField, string> { [SemanticField.Genre] = "Horror" }, new Dictionary<int, string>(), root.GetPath("output", "Edited.m4b"), MetadataProfiles.GenericMp4, ["One", "Two (cut", "Three"]);

    var result = await service.CreateAsync(request, CancellationToken.None);

    Assert.Equal(EditCopyStatus.InvalidInput, result.Status);
    Assert.Equal(EditCopyCodes.ChaptersChanged, Assert.Single(result.Diagnostics).Code);
    Assert.False(Directory.Exists(root.GetPath("output")));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task TheRewrittenChapterBoxHoldsTheFirst255ChaptersOfALongerBook()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(300), seconds: 30);
    var service = new EditCopyService(new ProcessRunner(), tools, root.GetPath("temp"));

    var result = await service.CreateAsync(new EditCopyRequest(source, new Dictionary<SemanticField, string>(), new Dictionary<int, string> { [0] = "Opening" }, root.GetPath("output", "Edited.m4b"), MetadataProfiles.GenericMp4), CancellationToken.None);

    Assert.Equal(EditCopyStatus.Published, result.Status);
    var chpl = Assert.Single(Mp4MetadataBoxes.ReadUserData(result.PublishedPath!).UdtaChildren, box => box.Type == "chpl").Raw;
    // chpl version 1: size, type, version, flags, 4 reserved bytes, then a one-byte chapter count.
    Assert.Equal(1, chpl[8]);
    Assert.Equal(255, chpl[16]);
    Assert.Equal(300, (await service.ReadChaptersAsync(result.PublishedPath!, CancellationToken.None)).Count);
  }
}
