using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.ViewModels;

// UI Automation names a data item by its ToString, not DisplayMemberPath, so each option returns its label.
public sealed record ChannelPolicyOption(ChannelPolicy Value, string DisplayName)
{
  public override string ToString() => DisplayName;
}

// Null is Automatic. A null item in a ComboBox shows as a blank row that can't be selected again, so every choice is an object.
public sealed record JobChoice(int? Value, string DisplayName)
{
  public override string ToString() => DisplayName;
}

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
  public string OutputTemplate { get; set; } = initial.OutputTemplate;
  public string TemplateHelp { get; } = OutputTemplateHelp.Text;
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
  public IReadOnlyList<JobChoice> JobChoices { get; } = [new(null, $"Automatic ({ConversionPlanner.AutomaticConversionJobs})"), .. Enumerable.Range(1, 32).Select(value => new JobChoice(value, value.ToString(System.Globalization.CultureInfo.InvariantCulture)))];
  public string JobsHelp { get; } = $"How many audio encodes run at once inside one book. Automatic runs {ConversionPlanner.AutomaticConversionJobs}.";
  public JobChoice SelectedJobChoice { get => JobChoices.FirstOrDefault(choice => choice.Value == ConversionJobs) ?? JobChoices[0]; set => ConversionJobs = value?.Value; }
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
      if (OutputPathTemplate.Validate(OutputTemplate) is { } templateError) { ErrorMessage = templateError; return false; }
      var settings = initial with { OutputDirectory = OutputDirectory.Trim(), QualityProfileId = QualityProfileId, CreateChapters = CreateChapters, ConversionJobs = ConversionJobs, BooksAtOnce = BooksAtOnce, OutputTemplate = OutputTemplate.Trim(), NotificationsEnabled = NotificationsEnabled, NotificationsHideDetails = NotificationsHideDetails, ChannelPolicy = ChannelPolicy, MetadataProfileId = MetadataProfileId, ValidationLevel = ValidationLevel, LogLevel = LogLevel };
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
