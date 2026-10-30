using System.Text;
using BookSplice.Core.Analysis;
using BookSplice.Core.Covers;
using BookSplice.Core.Discovery;
using BookSplice.Core.Naming;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Queue;
using BookSplice.Core.Settings;

namespace BookSplice.Core.Tests.Queue;

public sealed class QueueSnapshotStoreTests : IDisposable
{
  private readonly string _directory = Path.Combine(Path.GetTempPath(), "queue-store-" + Guid.NewGuid().ToString("N"));

  public void Dispose()
  {
    if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
  }

  private QueueSnapshotStore Store() => new(_directory);

  private static SavedItemSettings Settings(string quality = "high-quality") => new(
    Path.GetFullPath("out"), quality, ChannelPolicy.ForceMono, false, CollisionPolicy.AvoidCollision, "GenericMp4", ValidationLevel.Full, "{Author}/{Title}");

  private static QueueItemRecord FullItem() => new(
    Path.GetFullPath("books/one"), Settings(),
    new SavedSelection([Path.GetFullPath("books/one/a.mp3"), Path.GetFullPath("books/one/sub/b.mp3")], Path.GetFullPath("books/one"), true),
    [new SourceIdentity(Path.GetFullPath("books/one/a.mp3"), 12345, new DateTime(2026, 10, 1, 12, 30, 15, 123, DateTimeKind.Utc).AddTicks(4567), "ABCD:0001"),
     new SourceIdentity(Path.GetFullPath("books/one/sub/b.mp3"), 99, DateTime.UnixEpoch, null)],
    OrderCandidateId.Metadata,
    new ReviewedTrackOrder(["sub/b.mp3", "a.mp3"], ["c.mp3"]),
    new SavedCover(CoverOrigin.EmbeddedPicture, Path.GetFullPath("books/one/a.mp3"), "hash-1", 2),
    true,
    new Dictionary<string, string> { ["a.mp3"] = "Opening" },
    new Dictionary<string, string> { ["Title"] = "Edited title", ["Comment"] = "" },
    QueueItemState.Succeeded, Path.GetFullPath("out/Book.m4b"), "note");

  private static QueueSnapshot Snapshot(params QueueItemRecord[] items) => new(1, new DateTime(2026, 10, 30, 1, 2, 3, DateTimeKind.Utc), items);

  private string Write(string json)
  {
    Directory.CreateDirectory(_directory);
    var path = Path.Combine(_directory, QueueSnapshotStore.FileName);
    File.WriteAllText(path, json, new UTF8Encoding(false));
    return path;
  }

  [Fact]
  public void EveryPersistedFieldRoundTrips()
  {
    var store = Store();
    var second = new QueueItemRecord(Path.GetFullPath("books/two.mp3"), Settings("standard"));
    Assert.True(store.Save(Snapshot(FullItem(), second)));

    var loaded = Store().Load();

    Assert.Equal(QueueLoadCode.Loaded, loaded.Code);
    var items = loaded.Snapshot!.Items;
    Assert.Equal(2, items.Count);
    var item = items[0];
    var expected = FullItem();
    Assert.Equal(expected.Source, item.Source);
    Assert.Equal(expected.Settings, item.Settings);
    Assert.Equal(expected.Selection!.Paths, item.Selection!.Paths);
    Assert.Equal(expected.Selection.RootPath, item.Selection.RootPath);
    Assert.Equal(expected.ExpectedSources, item.ExpectedSources);
    Assert.Equal(expected.ExpectedSources![0].LastWriteUtc.Ticks, item.ExpectedSources![0].LastWriteUtc.Ticks);
    Assert.Equal(OrderCandidateId.Metadata, item.SelectedOrder);
    Assert.Equal(["sub/b.mp3", "a.mp3"], item.ReviewedOrder!.Included);
    Assert.Equal(["c.mp3"], item.ReviewedOrder.Excluded);
    Assert.Equal(expected.Cover, item.Cover);
    Assert.True(item.OmitCover);
    Assert.Equal("Opening", item.ChapterTitles!["a.mp3"]);
    Assert.Equal("Edited title", item.Metadata!["Title"]);
    Assert.Equal("", item.Metadata["Comment"]);
    Assert.Equal(QueueItemState.Succeeded, item.State);
    Assert.Equal(expected.PublishedPath, item.PublishedPath);
    Assert.Equal("note", item.Message);
    Assert.Equal("standard", items[1].Settings.QualityProfileId);
    Assert.Equal(new DateTime(2026, 10, 30, 1, 2, 3, DateTimeKind.Utc), loaded.Snapshot.SavedUtc);
  }

  [Fact]
  public void QueueOrderIsKept()
  {
    var items = Enumerable.Range(0, 5).Select(i => new QueueItemRecord(Path.GetFullPath($"books/{i}"), Settings())).ToArray();
    Store().Save(Snapshot(items.Reverse().ToArray()));

    var loaded = Store().Load().Snapshot!.Items.Select(item => item.Source);

    Assert.Equal(items.Reverse().Select(item => item.Source), loaded);
  }

  [Fact]
  public void MissingFileLoadsAsMissingAndCreatesNothing()
  {
    var result = Store().Load();

    Assert.Equal(QueueLoadCode.Missing, result.Code);
    Assert.Null(result.Snapshot);
    Assert.False(Directory.Exists(_directory));
  }

  [Fact]
  public void SavingReplacesTheFileAndLeavesNoTempFiles()
  {
    var store = Store();
    store.Save(Snapshot(FullItem()));
    store.Save(Snapshot());

    Assert.Empty(Store().Load().Snapshot!.Items);
    Assert.Equal([store.Path], Directory.GetFiles(_directory));
  }

  [Fact]
  public void AFailedReplaceKeepsThePreviousGoodFileAndCleansTheTempFile()
  {
    var store = Store();
    store.Save(Snapshot(FullItem()));
    var before = File.ReadAllBytes(store.Path);

    using (new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None))
      Assert.ThrowsAny<IOException>(() => store.Save(Snapshot()));

    Assert.Equal(before, File.ReadAllBytes(store.Path));
    Assert.Equal([store.Path], Directory.GetFiles(_directory));
    Assert.Single(Store().Load().Snapshot!.Items);
  }

  [Fact]
  public void ALeftoverTempFileFromAnInterruptedSaveIsIgnoredAndRemoved()
  {
    var store = Store();
    store.Save(Snapshot(FullItem()));
    var leftover = Path.Combine(_directory, ".queue.json." + Guid.NewGuid().ToString("N") + ".tmp");
    File.WriteAllText(leftover, "{\"schemaVersion\":1,\"items\":[{\"sour");

    var loaded = Store().Load();

    Assert.Equal(QueueLoadCode.Loaded, loaded.Code);
    Assert.Single(loaded.Snapshot!.Items);
    Assert.False(File.Exists(leftover));
  }

  [Theory]
  [InlineData("{ not json")]
  [InlineData("[]")]
  [InlineData("{\"items\":[]}")]
  [InlineData("{\"schemaVersion\":0,\"savedUtc\":\"2026-10-30T00:00:00Z\",\"items\":[]}")]
  [InlineData("{\"schemaVersion\":\"one\",\"savedUtc\":\"2026-10-30T00:00:00Z\",\"items\":[]}")]
  [InlineData("{\"schemaVersion\":1,\"savedUtc\":\"2026-10-30T00:00:00Z\"}")]
  [InlineData("{\"schemaVersion\":1,\"savedUtc\":\"2026-10-30T00:00:00Z\",\"items\":[null]}")]
  [InlineData("{\"schemaVersion\":1,\"savedUtc\":\"2026-10-30T00:00:00Z\",\"items\":[{\"source\":\"C:\\\\b\"}]}")]
  [InlineData("{\"schemaVersion\":1,\"savedUtc\":\"2026-10-30T00:00:00Z\",\"items\":[{\"source\":\"\",\"settings\":{}}]}")]
  public void ACorruptFileIsMovedAsideAndTheQueueStartsEmpty(string json)
  {
    var path = Write(json);
    var store = Store();

    var result = store.Load();

    Assert.Equal(QueueLoadCode.Corrupt, result.Code);
    Assert.Null(result.Snapshot);
    Assert.False(File.Exists(path));
    Assert.NotNull(result.PreservedPath);
    Assert.Contains(".corrupt-", result.PreservedPath, StringComparison.Ordinal);
    Assert.DoesNotContain(':', Path.GetFileName(result.PreservedPath));
    Assert.Equal(json, File.ReadAllText(result.PreservedPath));
    Assert.True(store.Save(Snapshot()));
    Assert.True(File.Exists(path));
  }

  [Fact]
  public void AnUnknownEnumValueOrStateIsCorrupt()
  {
    var good = ValidJson("\"state\":\"Succeeded\"");
    Write(good.Replace("Succeeded", "Exploded", StringComparison.Ordinal));

    Assert.Equal(QueueLoadCode.Corrupt, Store().Load().Code);
  }

  [Fact]
  public void ANewerVersionIsLeftUntouchedAndNeverOverwritten()
  {
    var json = "{\"schemaVersion\":2,\"savedUtc\":\"2026-10-30T00:00:00Z\",\"items\":[{\"shape\":\"unknown\"}]}";
    var path = Write(json);
    var store = Store();

    var result = store.Load();

    Assert.Equal(QueueLoadCode.FutureVersion, result.Code);
    Assert.Null(result.Snapshot);
    Assert.Equal(json, File.ReadAllText(path));
    Assert.True(store.IsWriteBlocked);
    Assert.False(store.Save(Snapshot(FullItem())));
    Assert.Equal(json, File.ReadAllText(path));
    Assert.Equal([path], Directory.GetFiles(_directory));
  }

  [Fact]
  public void AHandWrittenVersionOneFileLoadsAndIgnoresUnknownProperties()
  {
    var source = Path.GetFullPath("books/hand").Replace("\\", "\\\\", StringComparison.Ordinal);
    var output = Path.GetFullPath("out").Replace("\\", "\\\\", StringComparison.Ordinal);
    Write($$"""
      {
        "schemaVersion": 1,
        "savedUtc": "2026-10-30T00:00:00Z",
        "futureTopLevel": { "anything": [1, 2, 3] },
        "items": [
          {
            "source": "{{source}}",
            "settings": {
              "outputDirectory": "{{output}}", "qualityProfileId": "high-quality", "channelPolicy": "preserveSourceChannels",
              "createChapters": true, "collisionPolicy": "avoidCollision", "metadataProfileId": "GenericMp4",
              "validationLevel": "lightweight", "outputTemplate": "{Title}", "extraSetting": 7
            },
            "state": "interrupted",
            "futureField": "ignored"
          }
        ]
      }
      """);

    var result = Store().Load();

    Assert.Equal(QueueLoadCode.Loaded, result.Code);
    var item = Assert.Single(result.Snapshot!.Items);
    Assert.Equal(QueueItemState.Interrupted, item.State);
    Assert.Equal(ChannelPolicy.PreserveSourceChannels, item.Settings.ChannelPolicy);
    Assert.Null(item.Selection);
    Assert.Null(item.ReviewedOrder);
    Assert.False(item.OmitCover);
  }

  [Fact]
  public void TheMigrationStepClassifiesVersions()
  {
    Assert.Equal(QueueVersionCheck.Current, QueueSnapshotMigration.Check(System.Text.Json.Nodes.JsonNode.Parse("{\"schemaVersion\":1}")!.AsObject()));
    Assert.Equal(QueueVersionCheck.Future, QueueSnapshotMigration.Check(System.Text.Json.Nodes.JsonNode.Parse("{\"schemaVersion\":7}")!.AsObject()));
    Assert.Equal(QueueVersionCheck.Invalid, QueueSnapshotMigration.Check(System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject()));
    Assert.Equal(QueueVersionCheck.Invalid, QueueSnapshotMigration.Check(System.Text.Json.Nodes.JsonNode.Parse("{\"schemaVersion\":null}")!.AsObject()));
  }

  [Fact]
  public void AnUnreadableFileBlocksWritesAndIsNotMoved()
  {
    var path = Write(ValidJson(""));
    var store = Store();

    QueueLoadResult result;
    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) result = store.Load();

    Assert.Equal(QueueLoadCode.Unreadable, result.Code);
    Assert.True(store.IsWriteBlocked);
    Assert.False(store.Save(Snapshot()));
    Assert.True(File.Exists(path));
  }

  private static string ValidJson(string extraItemProperty)
  {
    var source = Path.GetFullPath("books/x").Replace("\\", "\\\\", StringComparison.Ordinal);
    var output = Path.GetFullPath("out").Replace("\\", "\\\\", StringComparison.Ordinal);
    var extra = extraItemProperty.Length == 0 ? "" : "," + extraItemProperty;
    return $$"""
      {"schemaVersion":1,"savedUtc":"2026-10-30T00:00:00Z","items":[{"source":"{{source}}","settings":{"outputDirectory":"{{output}}","qualityProfileId":"high-quality","channelPolicy":"preserveSourceChannels","createChapters":true,"collisionPolicy":"avoidCollision","metadataProfileId":"GenericMp4","validationLevel":"lightweight","outputTemplate":"{Title}"}{{extra}}}]}
      """;
  }
}
