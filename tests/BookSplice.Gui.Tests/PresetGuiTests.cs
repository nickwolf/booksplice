using System.Windows;
using System.Windows.Controls;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.Core.Presets;
using BookSplice.Core.Settings;
using BookSplice.Gui.ViewModels;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class PresetGuiTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("booksplice-presets-gui-").FullName;
  private readonly AppSettings _defaults;

  public PresetGuiTests() => _defaults = AppSettings.Defaults with { OutputDirectory = Path.Combine(_root, "out") };

  public void Dispose() => Directory.Delete(_root, true);

  private PresetStore Store => new(Path.Combine(_root, "BookSplice", PresetStore.FileName));

  private PresetsViewModel Model()
  {
    var model = new PresetsViewModel(Store);
    model.Reload();
    return model;
  }

  private BookQueueItemViewModel Book(string name = "book") => new(Path.Combine(_root, name), _defaults);

  private static ConversionPreset Commute(string name = "Commute") =>
    new(name, "efficient", ChannelPolicy.ForceMono, false, ValidationLevel.Full, "GenericMp4", "{author}\\{title}");

  [Fact]
  public void ApplyCopiesTheValuesIntoTheSelectedBookOnly()
  {
    Store.Save([Commute()]);
    var model = Model();
    var selected = Book("selected");
    var other = Book("other");

    Assert.True(model.Apply(selected, "commute"));

    Assert.Equal("efficient", selected.QualityProfileId);
    Assert.Equal(ChannelPolicy.ForceMono, selected.ChannelPolicy);
    Assert.False(selected.Settings.CreateChapters);
    Assert.Equal(ValidationLevel.Full, selected.ValidationLevel);
    Assert.Equal("{author}\\{title}", selected.OutputTemplate);
    Assert.Equal(_defaults.OutputDirectory, selected.OutputDirectory);
    Assert.Equal(_defaults, other.Settings);
  }

  [Fact]
  public void ApplyRaisesChangeNotificationsForTheBoundSettings()
  {
    Store.Save([Commute()]);
    var model = Model();
    var book = Book();
    var changed = new List<string?>();
    book.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

    model.Apply(book, "Commute");

    Assert.Contains(nameof(BookQueueItemViewModel.QualityProfileId), changed);
    Assert.Contains(nameof(BookQueueItemViewModel.OutputTemplate), changed);
  }

  [Fact]
  public void LaterEditsAndDeletionOfAPresetLeaveAnAppliedBookUnchanged()
  {
    Store.Save([Commute()]);
    var model = Model();
    var book = Book();
    model.Apply(book, "Commute");
    var applied = book.Settings;

    Assert.True(model.UpdateFrom("Commute", _defaults with { QualityProfileId = "preserve-more", ChannelPolicy = ChannelPolicy.ForceStereo }));
    Assert.Equal(applied, book.Settings);
    Assert.True(model.Rename("Commute", "Renamed"));
    Assert.Equal(applied, book.Settings);
    Assert.True(model.Delete("Renamed"));
    Assert.Equal(applied, book.Settings);
    Assert.Empty(Store.Load().Presets);
  }

  [Fact]
  public void ApplyIsRefusedWhileTheBookIsConvertingAndAfterItIsComplete()
  {
    Store.Save([Commute()]);
    var model = Model();
    var converting = Book("converting");
    converting.IsBusy = true;
    var finished = Book("finished");
    finished.Status = "Complete";

    Assert.False(model.Apply(converting, "Commute"));
    Assert.Contains("converting", model.Message);
    Assert.False(model.Apply(finished, "Commute"));
    Assert.Contains("already finished", model.Message);

    Assert.Equal(_defaults, converting.Settings);
    Assert.Equal(_defaults, finished.Settings);
  }

  [Fact]
  public void InvalidPresetIsRefusedWithAMessageAndChangesNothing()
  {
    Store.Save([Commute() with { QualityProfileId = "gone", MetadataProfileId = "Missing", ChannelPolicy = ChannelPolicy.ForceStereo }]);
    var model = Model();
    var book = Book();

    Assert.False(model.Apply(book, "Commute"));

    Assert.Equal(_defaults, book.Settings);
    Assert.Contains("Preset 'Commute': qualityProfileId 'gone'", model.Message);
    Assert.Contains("Preset 'Commute': metadataProfileId 'Missing'", model.Message);
    Assert.False(Assert.Single(model.Presets).IsValid);
  }

  [Fact]
  public void SavingTheBooksSettingsCreatesAPresetWithoutTheFolderByDefault()
  {
    var model = Model();
    var book = Book();
    book.QualityProfileId = "balanced";

    Assert.True(model.SaveNew("  Mine ", book.Settings));

    var saved = Assert.Single(Store.Load().Presets);
    Assert.Equal("Mine", saved.Name);
    Assert.Equal("balanced", saved.QualityProfileId);
    Assert.Null(saved.OutputDirectory);
    Assert.True(model.SaveNew("With folder", book.Settings, includeOutputDirectory: true));
    Assert.Equal(_defaults.OutputDirectory, Store.Load().Find("With folder")!.OutputDirectory);
  }

  [Fact]
  public void SavingOverAnExistingNameIsReportedNotOverwritten()
  {
    Store.Save([Commute()]);
    var model = Model();

    Assert.False(model.SaveNew("COMMUTE", _defaults));

    Assert.Contains("already exists", model.Message);
    Assert.Equal(Commute(), Assert.Single(Store.Load().Presets));
    Assert.False(model.Rename("Commute", "  "));
  }

  [Fact]
  public void ACorruptPresetsFileBlocksChangesAndIsLeftAlone()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(Store.Path)!);
    File.WriteAllText(Store.Path, "{ broken");
    var model = Model();

    Assert.False(model.CanChange);
    Assert.True(model.HasFileProblem);
    Assert.False(model.SaveNew("New", _defaults));
    Assert.Contains("needs fixing", model.Message);
    Assert.Equal("{ broken", File.ReadAllText(Store.Path));
  }

  [Fact]
  public void ExportOmitsTheOutputFolderUnlessIncludedAndMatchesTheFile()
  {
    Store.Save([Commute() with { OutputDirectory = Path.Combine(_root, "secret-folder") }, Commute("Other")]);
    var model = Model();

    var plain = model.BuildExport(["Commute"], includeOutputDirectory: false);
    var full = model.BuildExport(["Commute"], includeOutputDirectory: true);
    var target = Path.Combine(_root, "share", "presets.json");

    Assert.DoesNotContain("secret-folder", plain);
    Assert.DoesNotContain("Other", plain);
    Assert.Contains("secret-folder", full);
    Assert.True(model.ExportToFile(target, ["Commute"], includeOutputDirectory: false));
    Assert.Equal(plain, File.ReadAllText(target));
    Assert.Contains("\"Other\"", model.BuildExport(null, false));
  }

  [Fact]
  public void ImportAddsNewPresetsAndReportsClashesAndInvalidOnes()
  {
    Store.Save([Commute()]);
    var model = Model();
    var incoming = Path.Combine(_root, "incoming.json");
    File.WriteAllText(incoming, PresetPortable.Export([Commute("commute") with { QualityProfileId = "preserve-more" }, Commute("Fresh"), Commute("Bad") with { MetadataProfileId = "Nope" }], false));

    var report = model.ImportFile(incoming);

    Assert.NotNull(report);
    Assert.Equal(["Fresh"], report.Accepted.Select(preset => preset.Name));
    Assert.Equal(["Commute", "Fresh"], Store.Load().Presets.Select(preset => preset.Name));
    Assert.Equal("efficient", Store.Load().Find("Commute")!.QualityProfileId);
    Assert.Contains("already exists", model.Message);
    Assert.Contains("Skipped 'Bad'", model.Message);
  }

  [Fact]
  public void ImportOfAFutureFileChangesNothing()
  {
    Store.Save([Commute()]);
    var model = Model();
    var incoming = Path.Combine(_root, "future.json");
    File.WriteAllText(incoming, "{\"schemaVersion\":5,\"presets\":[]}");

    model.ImportFile(incoming);

    Assert.Equal([Commute()], Store.Load().Presets);
    Assert.Contains("does not understand", model.Message);
  }

  [Fact]
  public void ExportWindowPreviewShowsExactlyWhatWillBeWrittenAndStartsWithoutTheFolder()
  {
    Store.Save([Commute() with { OutputDirectory = Path.Combine(_root, "secret-folder") }]);
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        var model = Model();
        var window = new PresetExportWindow(model, ["Commute"]);
        Assert.False(window.IncludeFolder.IsChecked);
        Assert.Equal(model.BuildExport(["Commute"], false), window.Preview.Text);
        Assert.DoesNotContain("secret-folder", window.Preview.Text);
        window.IncludeFolder.IsChecked = true;
        Assert.Equal(model.BuildExport(["Commute"], true), window.Preview.Text);
        Assert.Contains("secret-folder", window.Preview.Text);
        window.Close();
      }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
    Assert.Null(failure);
  }

  [Fact]
  public void CustomMetadataProfilesCountAsAvailableWhenApplying()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    File.WriteAllText(Path.Combine(profiles, "custom.json"), """{"schemaVersion":1,"name":"SyntheticCustom","version":"1","mappings":[{"field":"BookTitle","keys":["TITLE"]}]}""");
    var catalog = MetadataProfileCatalog.Load(profiles);
    Store.Save([Commute() with { MetadataProfileId = "SyntheticCustom" }]);
    var model = new PresetsViewModel(Store, catalog);
    model.Reload();
    var book = new BookQueueItemViewModel(Path.Combine(_root, "book"), _defaults, catalog);

    Assert.True(model.Apply(book, "Commute"));
    Assert.Equal("SyntheticCustom", book.MetadataProfileId);
  }
}
