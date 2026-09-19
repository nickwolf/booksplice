using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Execution;
using BookSplice.Core.Ordering;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.ViewModels;

public sealed class MainWindowViewModel(IBookAnalyzer analyzer, IConversionService conversion, AppSettings settings, IConversionPlanner? planner = null) : ObservableModel, IDisposable
{
  private SemaphoreSlim _slots = new(settings.ConversionJobs ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 6));
  private string _message = "";
  public ObservableCollection<BookQueueItemViewModel> Items { get; } = [];
  private AppSettings _settings = settings;
  public AppSettings Settings
  {
    get => _settings;
    set
    {
      if (Items.Any(item => item.IsBusy)) throw new InvalidOperationException("Wait for active jobs before changing settings.");
      _settings = value;
      _slots.Dispose();
      _slots = new(value.ConversionJobs ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 6));
    }
  }
  public string Message { get => _message; private set { _message = value; Changed(); } }

  public async Task AddAsync(string path)
  {
    try
    {
      path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      if (Items.Any(item => string.Equals(item.Source, path, StringComparison.OrdinalIgnoreCase)))
      { Message = "This source is already in the queue."; return; }
      var item = new BookQueueItemViewModel(path, Settings);
      Items.Add(item);
      Message = "";
      await AnalyzeAsync(item);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
    { Message = exception.Message; }
  }

  public async Task ResolveOrderAsync(BookQueueItemViewModel item, OrderCandidateId candidate)
  {
    if (item.IsBusy || !item.CanEdit || !item.OrderCandidates.Any(value => value.Id == candidate)) return;
    item.SelectedOrder = candidate;
    await AnalyzeAsync(item);
  }

  private async Task AnalyzeAsync(BookQueueItemViewModel item)
  {
    item.IsBusy = true;
    item.Status = "Analyzing";
    using var cancellation = new CancellationTokenSource();
    item.Cancellation = cancellation;
    try
    {
      var result = await Task.Run(() => analyzer.AnalyzeAsync(item.Source, item.Settings.CreateChapters,
        new BookAnalysisOptions(item.SelectedOrder), cancellation.Token));
      item.SetAnalysis(result);
    }
    catch (OperationCanceledException) { item.Status = "Cancelled"; }
    catch (Exception exception) { item.Status = "Analysis failed"; item.Details = exception.Message; }
    finally { item.Cancellation = null; item.IsBusy = false; }
  }

  public async Task ConvertAsync(BookQueueItemViewModel item)
  {
    if (!item.CanConvert) return;
    item.IsBusy = true;
    item.Status = "Converting";
    item.PublishedPath = null;
    item.Percent = 0;
    using var cancellation = new CancellationTokenSource();
    item.Cancellation = cancellation;
    var acquired = false;
    var elapsed = Stopwatch.StartNew();
    var progress = new Progress<ConversionProgress>(value =>
    {
      if (!item.IsBusy) return;
      item.Percent = value.DisplayPercent ?? item.Percent;
      item.Details = $"{elapsed.Elapsed:g} elapsed | {value.Speed:0.0}x speed | {value.State}";
    });
    try
    {
      item.Status = "Waiting";
      await _slots.WaitAsync(cancellation.Token);
      acquired = true;
      item.Status = "Converting";
      var result = await conversion.ConvertAsync(new ConversionRequest(item.Source, item.Settings.CreateChapters, item.CreateOptions(singleConversionJob: true),
        AnalysisOptions: new BookAnalysisOptions(item.SelectedOrder), ExpectedSourcePaths: item.Analysis!.OrderedFiles.Select(file => file.FullPath).ToArray()), cancellation.Token, progress);
      item.Status = result.Status == ConversionTerminalStatus.Succeeded ? "Complete" : result.Status.ToString();
      item.PublishedPath = result.Status == ConversionTerminalStatus.Succeeded ? result.PublishedPath : null;
      item.Details = $"{elapsed.Elapsed:g} elapsed" + Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message));
      if (result.Status == ConversionTerminalStatus.Succeeded) item.Percent = 100;
    }
    catch (OperationCanceledException) { item.Status = "Cancelled"; }
    catch (Exception exception) { item.Status = "Conversion failed"; item.Details = exception.Message; }
    finally { if (acquired) _slots.Release(); item.Cancellation = null; item.IsBusy = false; }
  }

  public async Task PreviewAsync(BookQueueItemViewModel item)
  {
    if (planner is null || !item.CanConvert || item.Analysis is null) return;
    item.IsBusy = true;
    try
    {
      var result = await Task.Run(() => planner.CreateAsync(item.Analysis, item.CreateOptions(singleConversionJob: true), CancellationToken.None));
      item.Details = result.Plan is { } plan
        ? $"Output: {plan.OutputPath}{Environment.NewLine}Audio: {DescribeAudio(plan)}{Environment.NewLine}Validation: {plan.ValidationLevel} | Metadata: {plan.MetadataProfileId}"
        : string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message));
    }
    catch (Exception exception) { item.Details = exception.Message; }
    finally { item.IsBusy = false; }
  }

  private static string DescribeAudio(ConversionPlan plan) => plan.Strategy == AudioStrategy.AacStreamCopy
    ? $"AAC stream copy | {plan.ChannelLayout}"
    : $"{plan.Strategy} | {plan.QualityProfile.DisplayName} {plan.QualityProfile.AudioBitrateKbps} kbps | {plan.ChannelLayout}";

  public void Dispose() => _slots.Dispose();

  public async Task ConvertAllAsync()
  {
    await Task.WhenAll(Items.Where(item => item.CanConvert).ToArray().Select(ConvertAsync));
  }
}
