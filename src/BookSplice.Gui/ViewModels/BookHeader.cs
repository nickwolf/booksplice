using System.IO;
using BookSplice.Core.Analysis;
using BookSplice.Core.Metadata;
using BookSplice.Core.Planning;

namespace BookSplice.Gui.ViewModels;

/// <summary>What the selected-book header shows for one planning pass: the planned file and audio strategy, or a short reason there is no plan.</summary>
public sealed record BookHeader(string? FileName, string? FullPath, string? AudioText, string InfoText, string? RefusalText)
{
  public bool HasPlan => FileName is not null;
  public bool HasRefusal => RefusalText is not null;
  public bool HasInfo => InfoText.Length > 0 && InfoText != RefusalText;

  public static BookHeader Refused(string reason, string? info = null) => new(null, null, null, info ?? reason, reason);

  public static BookHeader From(ConversionPlanningResult result)
    => result.Plan is { } plan ? FromPlan(plan, result.Diagnostics) : FromRefusal(result);

  private static BookHeader FromPlan(ConversionPlan plan, IReadOnlyList<AnalysisDiagnostic> notes)
  {
    var explanation = StrategyExplanation.Create(plan.Strategy, plan.StrategyReasonCodes);
    var lines = new List<string> { $"Output: {plan.OutputPath}", $"Audio: {DescribeAudio(plan)}" };
    if (explanation.Reasons.Count > 0) { lines.Add("Why re-encode:"); lines.AddRange(explanation.Reasons.Select(reason => $"  - {reason}")); }
    lines.AddRange(notes.Where(note => note.Code.StartsWith("output.", StringComparison.Ordinal)).Select(note => $"Note: {note.Message}"));
    lines.AddRange(SpaceEstimateDescription.Lines(plan.Space));
    lines.Add($"Validation: {plan.ValidationLevel} | Metadata: {MetadataProfileCatalog.PublicLabel(plan.MetadataProfileId)}");
    return new(Path.GetFileName(plan.OutputPath), plan.OutputPath, explanation.Summary, string.Join(Environment.NewLine, lines), null);
  }

  private static BookHeader FromRefusal(ConversionPlanningResult result)
  {
    var lines = result.Diagnostics.Select(diagnostic => diagnostic.Message).ToList();
    var reason = lines.FirstOrDefault() ?? "The output could not be planned.";
    if (lines.Count == 0) lines.Add(reason);
    if (result.Space is { } space) lines.AddRange(SpaceEstimateDescription.Lines(space));
    return Refused(reason, string.Join(Environment.NewLine, lines));
  }

  private static string DescribeAudio(ConversionPlan plan) => plan.Strategy == AudioStrategy.AacStreamCopy
    ? $"AAC stream copy | {plan.ChannelLayout}"
    : $"{plan.Strategy} | {plan.QualityProfile.DisplayName} {plan.QualityProfile.AudioBitrateKbps} kbps | {plan.ChannelLayout}";
}
