using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.ViewModels;

public sealed class FirstLaunchViewModel(FirstLaunchService service, AppSettings initial) : INotifyPropertyChanged
{
  private string _outputDirectory = initial.OutputDirectory;
  private string _qualityProfileId = initial.QualityProfileId;
  private bool _createChapters = initial.CreateChapters;
  private bool _isBusy;
  private string _errorMessage = "";
  public event PropertyChangedEventHandler? PropertyChanged;
  public IReadOnlyList<QualityProfile> QualityProfiles { get; } = QualityProfileCatalog.Version1;
  public string OutputDirectory { get => _outputDirectory; set { _outputDirectory = value; Changed(); } }
  public string QualityProfileId { get => _qualityProfileId; set { _qualityProfileId = value; Changed(); } }
  public bool CreateChapters { get => _createChapters; set { _createChapters = value; Changed(); } }
  public bool IsBusy { get => _isBusy; private set { _isBusy = value; Changed(); Changed(nameof(CanEdit)); } }
  public bool CanEdit => !IsBusy;
  public string ErrorMessage { get => _errorMessage; private set { _errorMessage = value; Changed(); } }
  public int? ConversionJobs { get; set; } = initial.ConversionJobs;
  public string MetadataProfileId { get; set; } = initial.MetadataProfileId;
  public ValidationLevel ValidationLevel { get; set; } = initial.ValidationLevel;
  public LogLevel LogLevel { get; set; } = initial.LogLevel;
  public IReadOnlyList<string> MetadataProfiles { get; } = ["GenericMp4", "NickMp3tag"];
  public IReadOnlyList<ValidationLevel> ValidationLevels { get; } = Enum.GetValues<ValidationLevel>();
  public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();
  public IReadOnlyList<int?> JobChoices { get; } = new int?[] { null }.Concat(Enumerable.Range(1, 32).Select(value => (int?)value)).ToArray();
  public bool Completed { get; private set; }
  public AppSettings? SavedSettings { get; private set; }

  public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
  {
    if (IsBusy || Completed) return false;
    IsBusy = true;
    ErrorMessage = "";
    try
    {
      cancellationToken.ThrowIfCancellationRequested();
      var settings = initial with { OutputDirectory = OutputDirectory.Trim(), QualityProfileId = QualityProfileId, CreateChapters = CreateChapters, ConversionJobs = ConversionJobs, MetadataProfileId = MetadataProfileId, ValidationLevel = ValidationLevel, LogLevel = LogLevel };
      var result = await service.CompleteAsync(settings, cancellationToken);
      if (!result.Completed)
      {
        ErrorMessage = result.Error ?? "Choose an existing output folder that you can write to.";
        return false;
      }
      SavedSettings = settings;
      Completed = true;
      Changed(nameof(Completed));
      return true;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    finally { IsBusy = false; }
  }

  private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
