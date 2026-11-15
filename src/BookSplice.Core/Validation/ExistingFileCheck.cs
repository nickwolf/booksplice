using BookSplice.Core.Analysis;

namespace BookSplice.Core.Validation;

public enum ExistingCheckStatus { Passed, Failed, NotChecked, Note }

public sealed record ExistingFileCheck(string Code, ExistingCheckStatus Status, string Message, bool Required = true);

public sealed class ExistingFileCheckReport
{
  public ExistingFileCheckReport(string path, IEnumerable<ExistingFileCheck> checks, IEnumerable<string> notEstablished, MediaProbeResult? facts = null, IEnumerable<string>? warnings = null)
  {
    Path = System.IO.Path.GetFullPath(path);
    Checks = Array.AsReadOnly(checks.ToArray());
    NotEstablished = Array.AsReadOnly(notEstablished.ToArray());
    Facts = facts;
    Warnings = Array.AsReadOnly((warnings ?? []).ToArray());
  }

  public string Path { get; }
  public IReadOnlyList<ExistingFileCheck> Checks { get; }
  public IReadOnlyList<string> NotEstablished { get; }
  public MediaProbeResult? Facts { get; }
  public IReadOnlyList<string> Warnings { get; }
  public bool IsValid => !Checks.Any(check => check.Required && check.Status == ExistingCheckStatus.Failed);
}

public static class ExistingCheckText
{
  public static string Label(ExistingCheckStatus status) => status switch
  {
    ExistingCheckStatus.Passed => "Passed",
    ExistingCheckStatus.Failed => "Failed",
    ExistingCheckStatus.NotChecked => "Not checked",
    _ => "Note",
  };

  // One line per check, named by its code like the conversion result card. A pass needs no message.
  public static string Line(ExistingFileCheck check)
    => check.Status == ExistingCheckStatus.Passed ? $"Passed: {check.Code}" : $"{Label(check.Status)}: {check.Code}. {check.Message}";
}
