using BookSplice.Core.Analysis;
using BookSplice.Core.EditCopy;
using BookSplice.Core.Metadata;
using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.EditCopy;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tests.Execution;

namespace BookSplice.FFmpeg.Tests.EditCopy;

public sealed class EditCopyLayoutAndTagTests
{
  [PinnedMediaFact]
  public async Task AFileWithAPlainVideoStreamCanHaveItsTagsEditedButNotItsChapterTitles()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3), plainVideo: true);
    var before = EditCopyMedia.Hash(source);
    var service = new EditCopyService(new ProcessRunner(), tools, root.GetPath("temp"));

    var chapters = await service.CreateAsync(Request(source, root.GetPath("output", "Chapters.m4b"), chapters: new() { [1] = "Renamed" }), CancellationToken.None);

    Assert.Equal(EditCopyStatus.InvalidInput, chapters.Status);
    Assert.True(chapters.Diagnostics.Single().Code == EditCopyCodes.ChaptersUnsupportedLayout, string.Join("; ", chapters.Checks.Where(check => !check.Passed).Select(check => $"{check.Code}: {check.Message}")));
    Assert.False(Directory.Exists(root.GetPath("output")));
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));

    var tags = await service.CreateAsync(Request(source, root.GetPath("output", "Tags.m4b"), tags: new() { [SemanticField.Genre] = "Horror" }), CancellationToken.None);

    Assert.True(tags.Status == EditCopyStatus.Published, string.Join(Environment.NewLine, tags.Diagnostics.Select(diagnostic => diagnostic.Message).Concat(tags.Checks.Where(check => !check.Passed).Select(check => check.Code))));
    Assert.Contains(tags.Checks, check => check.Code == EditCopyCodes.CheckStreams && check.Passed && check.Required);
    Assert.Contains(tags.Checks, check => check.Code == EditCopyCodes.CheckDuration && check.Passed && check.Required);
    var probe = await new BookSplice.FFmpeg.Probing.FFprobeMediaProbe(new ProcessRunner(), tools).ProbeAsync(tags.PublishedPath!);
    Assert.Contains(probe.OtherStreams!, stream => stream.CodecType == "video" && stream.CodecName == "mjpeg");
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  [PinnedMediaFact]
  public async Task ChapterRemuxIsAllowedOnlyForAudioCoverAndChapterTrack()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var plain = await EditCopyMedia.GenerateAsync(root.Path, tools, EditCopyMedia.Titles(3));
    var withUserData = await EditCopyMedia.GenerateWithUserDataAsync(root.CreateFolder("user-data"), tools);
    var withUuid = root.GetPath("uuid.m4b");
    File.Copy(plain, withUuid);
    await File.AppendAllBytesAsync(withUuid, EditCopyMedia.Box("uuid", new byte[16], [1, 2, 3]));
    var withVideo = await EditCopyMedia.GenerateAsync(root.CreateFolder("video"), tools, EditCopyMedia.Titles(3), plainVideo: true);

    Assert.True(Mp4MetadataBoxes.CanRemuxChapters(plain));
    Assert.True(Mp4MetadataBoxes.CanRemuxChapters(withUserData));
    Assert.False(Mp4MetadataBoxes.CanRemuxChapters(withUuid));
    Assert.False(Mp4MetadataBoxes.CanRemuxChapters(withVideo));
  }

  [Fact]
  public void StreamKindsLeaveOutOnlyTheChapterTextTrack()
  {
    var original = Probe(new MediaStreamInfo(2, "data", "bin_data", null));
    var withVideo = Probe(new MediaStreamInfo(2, "data", "bin_data", null), new MediaStreamInfo(3, "video", "mjpeg", null));
    var withoutChapterTrack = Probe();

    Assert.Equal(["attached_pic/mjpeg", "audio/aac"], EditCopyService.StreamKinds(original));
    Assert.Equal(EditCopyService.StreamKinds(original), EditCopyService.StreamKinds(withoutChapterTrack));
    Assert.NotEqual(EditCopyService.StreamKinds(original), EditCopyService.StreamKinds(withVideo));
  }

  [Fact]
  public void TimingMustMatchExactly()
  {
    Assert.True(EditCopyService.SameTiming(Probe(), Probe()));
    Assert.False(EditCopyService.SameTiming(Probe() with { Duration = 6.001m }, Probe()));
    var shifted = Probe() with { AudioStreams = [Probe().AudioStreams[0] with { StartTime = 0.023m }] };
    Assert.False(EditCopyService.SameTiming(shifted, Probe()));
  }

  [PinnedMediaFact]
  public async Task EditsToNativeTextKeysLandInTheirAtoms()
  {
    using var root = new TemporaryFolder();
    var written = new Dictionary<string, string> { ["show"] = "Old show", ["episode_id"] = "Old episode", ["network"] = "Old network", ["keywords"] = "Old keywords", ["encoding_tool"] = "Old tool", ["disc_subtitle"] = "Old subtitle" };
    var path = await EditCopyMedia.GenerateAsync(root.Path, EditCopyMedia.Tools(), EditCopyMedia.Titles(3), extraTags: written);
    var before = Mp4MetadataBoxes.ReadItems(path);
    foreach (var (atom, value) in new[] { ("tvsh", "Old show"), ("tven", "Old episode"), ("tvnn", "Old network"), ("keyw", "Old keywords"), ("©too", "Old tool") })
      Assert.Equal(value, Text(before, atom));
    Assert.Equal("Old subtitle", Assert.Single(Assert.Single(before, item => item.IsFreeform && item.Name == "DISCSUBTITLE").Data).Text);
    var atoms = new Dictionary<string, string>
    {
      ["show"] = "tvsh",
      ["episode_id"] = "tven",
      ["network"] = "tvnn",
      ["keywords"] = "keyw",
      ["encoding_tool"] = "©too",
      ["sort_name"] = "sonm",
      ["sort_artist"] = "soar",
      ["sort_album_artist"] = "soaa",
      ["sort_album"] = "soal",
      ["sort_composer"] = "soco",
      ["sort_show"] = "sosn",
      ["category"] = "catg",
    };

    Mp4MetadataWriter.Edit(path, atoms.Keys.Append("disc_subtitle").ToDictionary(key => key, key => $"New {key}"));

    var after = Mp4MetadataBoxes.ReadItems(path);
    foreach (var (key, atom) in atoms)
    {
      Assert.Equal($"New {key}", Text(after, atom));
      Assert.True(EditCopyService.ReadsBack(after, key, $"New {key}"), key);
    }
    Assert.Equal("New disc_subtitle", Assert.Single(Assert.Single(after, item => item.IsFreeform && item.Name == "DISCSUBTITLE").Data).Text);
    Assert.True(EditCopyService.ReadsBack(after, "disc_subtitle", "New disc_subtitle"));

    Mp4MetadataWriter.Edit(path, new Dictionary<string, string> { ["encoder"] = "Newer tool" });

    Assert.Equal("Newer tool", Text(Mp4MetadataBoxes.ReadItems(path), "©too"));
  }

  [Fact]
  public void ReadsBackFailsWhenTheNativeAtomKeepsTheOldValue()
  {
    IReadOnlyList<Mp4MetadataItem> stale = [Item("tvsh", null, null, "Old show"), Item("----", Mp4MetadataWriter.FreeformMean, "show", "New show")];
    IReadOnlyList<Mp4MetadataItem> updated = [Item("tvsh", null, null, "New show"), Item("----", Mp4MetadataWriter.FreeformMean, "show", "New show")];

    Assert.False(EditCopyService.ReadsBack(stale, "show", "New show"));
    Assert.True(EditCopyService.ReadsBack(updated, "show", "New show"));
    Assert.False(EditCopyService.ReadsBack(updated, "show", ""));
  }

  [PinnedMediaFact]
  public async Task AnOperationCanceledExceptionThatIsNotOursFailsTheEditAndCleansUp()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var source = await EditCopyMedia.GenerateAsync(root.CreateFolder("books"), tools, EditCopyMedia.Titles(3));
    var before = EditCopyMedia.Hash(source);
    var service = new EditCopyService(new ThrowingRemuxRunner(new ProcessRunner(), tools.FFmpegPath), tools, root.GetPath("temp"));

    var result = await service.CreateAsync(Request(source, root.GetPath("output", "Edited.m4b"), chapters: new() { [0] = "Renamed" }), CancellationToken.None);

    Assert.Equal(EditCopyStatus.Failed, result.Status);
    Assert.Equal(EditCopyCodes.Failed, Assert.Single(result.Diagnostics).Code);
    Assert.Empty(EditCopyMedia.Files(root.GetPath("temp")));
    Assert.False(Directory.Exists(root.GetPath("output")));
    Assert.Equal(before, EditCopyMedia.Hash(source));
  }

  private static EditCopyRequest Request(string source, string destination, Dictionary<SemanticField, string>? tags = null, Dictionary<int, string>? chapters = null)
    => new(source, tags ?? [], chapters ?? [], destination, MetadataProfiles.GenericMp4);

  private static string Text(IReadOnlyList<Mp4MetadataItem> items, string type) => Assert.Single(Assert.Single(items, item => item.Type == type).Data).Text;

  private static Mp4MetadataItem Item(string type, string? mean, string? name, string value)
    => new(type, mean, name, [], [new Mp4MetadataData(1, System.Text.Encoding.UTF8.GetBytes(value))]);

  private static MediaProbeResult Probe(params MediaStreamInfo[] others)
    => new([new AudioTrack(0, "aac", "audio", 1, 44100, 6m, new Rational(1, 44100), new Dictionary<string, string>(), StartTime: 0m)],
      [new MediaAttachedPicture(1, "mjpeg", 64, 64, new Dictionary<string, string>())], [], new TagCollection([]), new Dictionary<string, string>(), [], 6m, OtherStreams: others);

  private sealed class ThrowingRemuxRunner(IProcessRunner inner, string ffmpegPath) : IProcessRunner
  {
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
      => string.Equals(spec.FileName, ffmpegPath, StringComparison.OrdinalIgnoreCase) ? throw new OperationCanceledException() : inner.RunAsync(spec, progress, cancellationToken);
  }
}
