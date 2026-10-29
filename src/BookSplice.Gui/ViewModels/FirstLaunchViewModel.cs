using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.ViewModels;

public sealed record ChannelPolicyOption(ChannelPolicy Value, string DisplayName);

public sealed class FirstLaunchViewModel(FirstLaunchService service, AppSettings initial, MetadataProfileCatalog? profiles = null) : INotifyPropertyChanged
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
  public string ProfileNotice { get; } = ProfileNoticeText(profiles ?? MetadataProfileCatalog.BuiltIn);
  public string ErrorMessage { get => _errorMessage; private set { _errorMessage = value; Changed(); } }
  public int? ConversionJobs { get; set; } = initial.ConversionJobs;
  public int? BooksAtOnce { get; set; } = initial.BooksAtOnce ?? 1;
  public bool NotificationsEnabled { get; set; } = initial.NotificationsEnabled;
  public bool NotificationsHideDetails { get; set; } = initial.NotificationsHideDetails;
  public ChannelPolicy ChannelPolicy { get; set; } = initial.ChannelPolicy;
  public string MetadataProfileId { get; set; } = (profiles ?? MetadataProfileCatalog.BuiltIn).Find(initial.MetadataProfileId) is not null ? initial.MetadataProfileId : Core.Metadata.MetadataProfiles.GenericMp4.Name;
  public ValidationLevel ValidationLevel { get; set; } = initial.ValidationLevel;
  public LogLevel LogLevel { get; set; } = initial.LogLevel;
  public IReadOnlyList<ChannelPolicyOption> ChannelPolicies { get; } =
  [
    new(ChannelPolicy.PreserveSourceChannels, "Preserve source channels"),
    new(ChannelPolicy.ForceMono, "Force mono"),
    new(ChannelPolicy.ForceStereo, "Force stereo"),
  ];
  public IReadOnlyList<string> MetadataProfiles { get; } = (profiles ?? MetadataProfileCatalog.BuiltIn).Profiles.Select(profile => profile.Name).ToArray();
  public IReadOnlyList<ValidationLevel> ValidationLevels { get; } = Enum.GetValues<ValidationLevel>();
  public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();
  public IReadOnlyList<int?> JobChoices { get; } = new int?[] { null }.Concat(Enumerable.Range(1, 32).Select(value => (int?)value)).ToArray();
  public IReadOnlyList<int> BookChoices { get; } = Enumerable.Range(1, 32).ToArray();
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
      var settings = initial with { OutputDirectory = OutputDirectory.Trim(), QualityProfileId = QualityProfileId, CreateChapters = CreateChapters, ConversionJobs = ConversionJobs, BooksAtOnce = BooksAtOnce, NotificationsEnabled = NotificationsEnabled, NotificationsHideDetails = NotificationsHideDetails, ChannelPolicy = ChannelPolicy, MetadataProfileId = MetadataProfileId, ValidationLevel = ValidationLevel, LogLevel = LogLevel };
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

  public static string ProfileNoticeText(MetadataProfileCatalog catalog)
    => catalog.Errors.Count == 0 ? "" : $"{catalog.Errors.Count} metadata profile file(s) were skipped: " + string.Join("; ", catalog.Errors.Select(error => $"{error.FileName} ({error.Reason})"));

  private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
