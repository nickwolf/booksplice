using BookSplice.Gui.Updates;

namespace BookSplice.Gui.Tests;

public sealed class UpdatePreferencesStoreTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "booksplice-prefs-" + Guid.NewGuid().ToString("N"));

  public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

  [Fact]
  public async Task PreferencesRoundTripAtomicallyAndStaySeparateFromSettings()
  {
    var store = new UpdatePreferencesStore(_root);
    Assert.Equal(new UpdatePreferences(), store.Load());
    var value = new UpdatePreferences(false, new DateTimeOffset(2026, 10, 21, 1, 2, 3, TimeSpan.Zero), "v0.2.0");
    await store.SaveAsync(value);
    Assert.Equal(value, store.Load());
    Assert.Equal(["preferences.json"], Directory.GetFiles(Path.Combine(_root, "updates")).Select(Path.GetFileName));
    Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
  }

  [Fact]
  public void CorruptPreferencesFallBackToDefaults()
  {
    Directory.CreateDirectory(Path.Combine(_root, "updates"));
    File.WriteAllText(Path.Combine(_root, "updates", "preferences.json"), "{not json");
    Assert.Equal(new UpdatePreferences(), new UpdatePreferencesStore(_root).Load());
  }
}
