using BookSplice.FFmpeg.Commands;
using BookSplice.FFmpeg.EditCopy;
using BookSplice.FFmpeg.Execution;
using BookSplice.FFmpeg.Tests.Execution;

namespace BookSplice.FFmpeg.Tests.EditCopy;

public sealed class Mp4MetadataEditTests
{
  [PinnedMediaFact]
  public async Task AnEmptyValueRemovesTheNativeAtomTheFreeformBoxAndTheMediaType()
  {
    using var root = new TemporaryFolder();
    var path = await EditCopyMedia.GenerateAsync(root.Path, EditCopyMedia.Tools(), EditCopyMedia.Titles(3));
    var before = Mp4MetadataBoxes.ReadItems(path);
    Assert.Contains(before, item => item.Type == "\u00A9cmt");
    Assert.Contains(before, item => item.IsITunesFreeform("comment"));
    Assert.Contains(before, item => item.IsITunesFreeform("BOOKSPLICE_NOTE"));
    Assert.Contains(before, item => item.Type == "stik");

    Mp4MetadataWriter.Edit(path, new Dictionary<string, string> { ["comment"] = "", ["BOOKSPLICE_NOTE"] = "", ["media_type"] = "", ["title"] = "New title" });

    var after = Mp4MetadataBoxes.ReadItems(path);
    Assert.DoesNotContain(after, item => item.Type == "\u00A9cmt");
    Assert.DoesNotContain(after, item => item.IsITunesFreeform("comment"));
    Assert.DoesNotContain(after, item => item.IsITunesFreeform("BOOKSPLICE_NOTE"));
    Assert.DoesNotContain(after, item => item.Type == "stik");
    Assert.Equal("New title", Assert.Single(Assert.Single(after, item => item.Type == "\u00A9nam").Data).Text);
    Assert.Equal("eng", Assert.Single(Assert.Single(after, item => item.IsITunesFreeform("language")).Data).Text);
  }

  [PinnedMediaFact]
  public async Task AnEditUpdatesTheNativeAtomAndTheFreeformBoxWithTheSameName()
  {
    using var root = new TemporaryFolder();
    var path = await EditCopyMedia.GenerateAsync(root.Path, EditCopyMedia.Tools(), EditCopyMedia.Titles(3));

    Mp4MetadataWriter.Edit(path, new Dictionary<string, string> { ["comment"] = "Both places", ["media_type"] = "6", ["series"] = "New freeform" });

    var after = Mp4MetadataBoxes.ReadItems(path);
    Assert.Equal("Both places", Assert.Single(Assert.Single(after, item => item.Type == "\u00A9cmt").Data).Text);
    Assert.Equal("Both places", Assert.Single(Assert.Single(after, item => item.IsITunesFreeform("comment")).Data).Text);
    Assert.Equal([6], Assert.Single(Assert.Single(after, item => item.Type == "stik").Data).Payload);
    Assert.Equal("New freeform", Assert.Single(Assert.Single(after, item => item.IsITunesFreeform("series")).Data).Text);
  }

  [PinnedMediaFact]
  public async Task UntouchedItemsKeepTheirExactBytes()
  {
    using var root = new TemporaryFolder();
    var path = await EditCopyMedia.GenerateAsync(root.Path, EditCopyMedia.Tools(), EditCopyMedia.Titles(3));
    var before = Mp4MetadataBoxes.ReadItems(path);

    Mp4MetadataWriter.Edit(path, new Dictionary<string, string> { ["title"] = new string('x', 8000) });

    var after = Mp4MetadataBoxes.ReadItems(path);
    Assert.Equal(before.Where(item => item.Type != "\u00A9nam").Select(item => Convert.ToHexString(item.Raw)), after.Where(item => item.Type != "\u00A9nam").Select(item => Convert.ToHexString(item.Raw)));
  }

  [PinnedMediaFact]
  public async Task TagLibRewritesAnUntouchedItemsLocaleButKeepsItsContent()
  {
    using var root = new TemporaryFolder();
    var path = await EditCopyMedia.GenerateAsync(root.Path, EditCopyMedia.Tools(), EditCopyMedia.Titles(3));
    var genre = Assert.Single(Mp4MetadataBoxes.ReadItems(path), item => item.Type == "\u00A9gen").Raw;
    var bytes = await File.ReadAllBytesAsync(path);
    var offset = bytes.AsSpan().IndexOf(genre);
    Assert.True(offset > 0);
    Assert.Equal("data"u8.ToArray(), bytes[(offset + 12)..(offset + 16)]);
    bytes[offset + 23] = 0x01;
    await File.WriteAllBytesAsync(path, bytes);
    var patched = Assert.Single(Mp4MetadataBoxes.ReadItems(path), item => item.Type == "\u00A9gen");
    Assert.Equal(1, patched.Raw[23]);

    Mp4MetadataWriter.Edit(path, new Dictionary<string, string> { ["title"] = "New title" });

    // TagLib renders untouched items again on save, so the edit check compares content (EditCopyService.Content), not bytes.
    var after = Assert.Single(Mp4MetadataBoxes.ReadItems(path), item => item.Type == "\u00A9gen");
    Assert.Equal(0, after.Raw[23]);
    Assert.Equal(EditCopyService.Content(patched), EditCopyService.Content(after));
  }

  [PinnedMediaFact]
  public async Task AChapterBoxAtTheEndOfTheFileKeepsItsExactBytesThroughAnEdit()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var original = await EditCopyMedia.GenerateAsync(root.Path, tools, EditCopyMedia.Titles(4));
    var copy = root.GetPath("copy.m4b");
    var remux = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "error", "-i", original, "-map", "0:a:0", "-c", "copy", "-f", "mp4", copy]), null, CancellationToken.None);
    Assert.Equal(0, remux.ExitCode);
    var (offset, before) = ChapterBox(await File.ReadAllBytesAsync(copy));
    Assert.Equal(new FileInfo(copy).Length, offset + before.Length);

    Mp4MetadataWriter.Edit(copy, new Dictionary<string, string> { ["genre"] = "Horror", ["title"] = new string('x', 4000) });

    var (_, after) = ChapterBox(await File.ReadAllBytesAsync(copy));
    Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(after));
    Assert.Equal("Horror", Assert.Single(Assert.Single(Mp4MetadataBoxes.ReadItems(copy), item => item.Type == "©gen").Data).Text);
  }

  [PinnedMediaFact]
  public async Task AnEditLeavesOtherUserDataAndTheMoovLevelMetaBoxAlone()
  {
    using var root = new TemporaryFolder();
    var path = await EditCopyMedia.GenerateWithUserDataAsync(root.Path, EditCopyMedia.Tools());
    var before = Mp4MetadataBoxes.ReadUserData(path);

    Mp4MetadataWriter.Edit(path, new Dictionary<string, string> { ["title"] = "New title" });

    var after = Mp4MetadataBoxes.ReadUserData(path);
    Assert.Equal(before.UdtaChildren.Select(box => box.Type), after.UdtaChildren.Select(box => box.Type));
    Assert.Equal(before.Preserved.Select(Convert.ToHexString), after.Preserved.Select(Convert.ToHexString));
  }

  [PinnedMediaFact]
  public async Task RestoreUserDataPutsTheOriginalsBoxesInPlaceOfTheMuxersAndKeepsItsChapterBox()
  {
    using var root = new TemporaryFolder();
    var tools = EditCopyMedia.Tools();
    var original = await EditCopyMedia.GenerateWithUserDataAsync(root.Path, tools);
    var copy = root.GetPath("copy.m4b");
    var remux = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFmpegPath, ["-hide_banner", "-v", "error", "-i", original, "-map", "0:a:0", "-c", "copy", "-f", "mp4", copy]), null, CancellationToken.None);
    Assert.Equal(0, remux.ExitCode);
    EditCopyMedia.InjectUserData(copy, EditCopyMedia.Box("Xtra", [1, 2, 3]), EditCopyMedia.MdtaMetaBox("com.apple.quicktime.title", "Muxer"));
    var originalData = Mp4MetadataBoxes.ReadUserData(original);
    var meta = Mp4MetadataBoxes.ReadMetaBox(original)!;
    var chapters = Assert.Single(Mp4MetadataBoxes.ReadUserData(copy).UdtaChildren, box => box.Type == "chpl").Raw;
    Assert.NotEqual(Convert.ToHexString(meta), Convert.ToHexString(Mp4MetadataBoxes.ReadMetaBox(copy) ?? []));
    Assert.False(EditCopyService.SameUserData(Mp4MetadataBoxes.ReadUserData(copy), originalData));

    Mp4MetadataBoxes.RestoreUserData(copy, originalData);

    var restored = Mp4MetadataBoxes.ReadUserData(copy);
    Assert.Equal(Convert.ToHexString(meta), Convert.ToHexString(Mp4MetadataBoxes.ReadMetaBox(copy)!));
    Assert.Equal(originalData.Preserved.Select(Convert.ToHexString), restored.Preserved.Select(Convert.ToHexString));
    Assert.Equal(Convert.ToHexString(originalData.MoovMeta!), Convert.ToHexString(restored.MoovMeta!));
    Assert.Equal(Convert.ToHexString(chapters), Convert.ToHexString(Assert.Single(restored.UdtaChildren, box => box.Type == "chpl").Raw));
    Assert.Equal(originalData.UdtaChildren.Select(box => box.Type), restored.UdtaChildren.Select(box => box.Type));
    var probe = await new ProcessRunner().RunAsync(new ProcessSpec(tools.FFprobePath, ["-v", "error", "-show_entries", "format_tags=title", "-of", "csv=p=0", copy]), null, CancellationToken.None);
    Assert.Equal(0, probe.ExitCode);
    Assert.Equal("Test Book", probe.StandardOutput.Trim());
  }

  private static (long Offset, byte[] Box) ChapterBox(byte[] file)
  {
    var type = file.AsSpan().LastIndexOf("chpl"u8);
    Assert.True(type >= 4);
    var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(type - 4, 4));
    return (type - 4, file[(type - 4)..(type - 4 + size)]);
  }
}
