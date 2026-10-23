using BookSplice.Updates.Storage;

namespace BookSplice.Updates.Tests.Storage;

public sealed class SettingsSnapshotTests
{
  [Fact]
  public async Task CapturesExactBytesAndDetectsReplacement()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-settings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var path = Path.Combine(root, "settings.json");
      var old = System.Text.Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"unknown\": [2,1] }\r\n");
      await File.WriteAllBytesAsync(path, old);
      var snapshot = await SettingsSnapshot.CaptureAsync(path);
      Assert.Equal(old, snapshot.Bytes);
      var exposed = snapshot.Bytes!;
      exposed[0] = 0;
      Assert.Equal(old, snapshot.Bytes);
      await snapshot.VerifyUnchangedAsync(path);
      await File.WriteAllTextAsync(path, "{\"schemaVersion\":1}");
      await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.VerifyUnchangedAsync(path));
    }
    finally { Directory.Delete(root, true); }
  }

  [Theory]
  [InlineData("{\"schemaVersion\":0}")]
  [InlineData("{\"schemaVersion\":2}")]
  [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
  [InlineData("{\"schemaVersion\":1,\"x\":{\"a\":1,\"a\":2}}")]
  public async Task RejectsUnsupportedOrDuplicateSettings(string json)
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-settings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var path = Path.Combine(root, "settings.json");
      await File.WriteAllTextAsync(path, json);
      await Assert.ThrowsAnyAsync<Exception>(() => SettingsSnapshot.CaptureAsync(path));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task VerifiedAbsenceChangesWhenFileAppears()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-settings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var path = Path.Combine(root, "settings.json");
      var snapshot = await SettingsSnapshot.CaptureAsync(path);
      Assert.True(snapshot.IsAbsent);
      await File.WriteAllTextAsync(path, "{\"schemaVersion\":1}");
      await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.VerifyUnchangedAsync(path));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task ConcurrentReplacementCannotMixFileContentsDuringCapture()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-settings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var path = Path.Combine(root, "settings.json");
      var replacement = Path.Combine(root, "replacement.json");
      var old = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"value\":\"old\"}");
      var next = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"value\":\"new\"}");
      await File.WriteAllBytesAsync(path, old);
      await File.WriteAllBytesAsync(replacement, next);
      var snapshot = await SettingsSnapshot.CaptureAsync(path,
        () => Assert.ThrowsAny<IOException>(() => File.Replace(replacement, path, null)),
        CancellationToken.None);
      Assert.Equal(old, snapshot.Bytes);
      Assert.Equal(old, await File.ReadAllBytesAsync(path));
      Assert.Equal(next, await File.ReadAllBytesAsync(replacement));
      await snapshot.VerifyUnchangedAsync(path);
    }
    finally { Directory.Delete(root, true); }
  }
}
