using System.Collections.Concurrent;
using BookSplice.Core.Analysis;

namespace BookSplice.Core.Discovery;

public interface ISourceDiscoverer
{
  Task<DiscoveryResult> DiscoverAsync(string inputPath, CancellationToken cancellationToken = default);
  Task<DiscoveryResult> DiscoverAsync(SourceSelection selection, CancellationToken cancellationToken = default);
}

public sealed partial class SourceDiscoverer : ISourceDiscoverer
{
  private readonly IMediaProbe _mediaProbe;

  public SourceDiscoverer(IMediaProbe mediaProbe, int analysisConcurrency)
  {
    _mediaProbe = mediaProbe ?? throw new ArgumentNullException(nameof(mediaProbe));
    if (analysisConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(analysisConcurrency), analysisConcurrency, "Analysis concurrency must be positive.");
    AnalysisConcurrency = analysisConcurrency;
  }

  public int AnalysisConcurrency { get; }

  public async Task<DiscoveryResult> DiscoverAsync(string inputPath, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
    cancellationToken.ThrowIfCancellationRequested();
    var errors = new List<DiscoveryDiagnostic>();
    var warnings = new List<DiscoveryDiagnostic>();
    var candidates = DiscoverCandidates(inputPath, errors, warnings, cancellationToken);
    return await ProbeAsync(candidates, errors, warnings, cancellationToken).ConfigureAwait(false);
  }

  public async Task<DiscoveryResult> DiscoverAsync(SourceSelection selection, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(selection);
    cancellationToken.ThrowIfCancellationRequested();
    var errors = new List<DiscoveryDiagnostic>();
    var warnings = new List<DiscoveryDiagnostic>();
    var candidates = new List<SourceCandidate>();
    foreach (var path in selection.Paths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var id = selection.FileId(path);
      if (FindReparsePoint(path) is { } reparsePoint)
        warnings.Add(new DiscoveryDiagnostic("discovery.reparse-point-skipped", "A reparse point was skipped during source discovery.", reparsePoint, id));
      else if (!File.Exists(path))
        errors.Add(new DiscoveryDiagnostic("discovery.input-unreadable", "The input path does not exist or cannot be read.", path, id));
      else if (!SupportedAudio.IsSupported(path))
        errors.Add(new DiscoveryDiagnostic("discovery.input-unsupported", "The input file is not a supported audio source.", path, id));
      else candidates.Add(new SourceCandidate(path, id));
    }
    return await ProbeAsync(DeduplicateCandidates(candidates), errors, warnings, cancellationToken).ConfigureAwait(false);
  }

  private async Task<DiscoveryResult> ProbeAsync(SourceCandidate[] candidates, List<DiscoveryDiagnostic> errors, List<DiscoveryDiagnostic> warnings, CancellationToken cancellationToken)
  {
    var files = new ConcurrentBag<SourceFile>();
    var probeErrors = new ConcurrentBag<DiscoveryDiagnostic>();

    await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = AnalysisConcurrency, CancellationToken = cancellationToken }, async (candidate, token) =>
    {
      try
      {
        var identity = SourceIdentity.Capture(candidate.FullPath);
        var result = await _mediaProbe.ProbeAsync(candidate.FullPath, token).ConfigureAwait(false);
        files.Add(new SourceFile(candidate.FullPath, candidate.RelativePath, result, identity));
      }
      catch (OperationCanceledException) { throw; }
      catch (Exception exception)
      {
        probeErrors.Add(new DiscoveryDiagnostic("discovery.probe-failed", exception.Message, candidate.FullPath, candidate.RelativePath));
      }
    }).ConfigureAwait(false);

    errors.AddRange(probeErrors);
    return new DiscoveryResult(
      files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase),
      errors.OrderBy(error => error.RelativePath, StringComparer.Ordinal).ThenBy(error => error.FullPath, StringComparer.OrdinalIgnoreCase),
      warnings.OrderBy(warning => warning.RelativePath, StringComparer.Ordinal).ThenBy(warning => warning.FullPath, StringComparer.OrdinalIgnoreCase));
  }

  private static SourceCandidate[] DiscoverCandidates(string inputPath, List<DiscoveryDiagnostic> errors, List<DiscoveryDiagnostic> warnings, CancellationToken cancellationToken)
  {
    var fullInputPath = Path.GetFullPath(inputPath);
    if (FindReparsePoint(fullInputPath) is { } reparsePoint)
    {
      warnings.Add(new DiscoveryDiagnostic("discovery.reparse-point-skipped", "A reparse point was skipped during source discovery.", reparsePoint, RelativePath(Path.GetDirectoryName(fullInputPath) ?? fullInputPath, reparsePoint)));
      return [];
    }

    var candidates = new List<SourceCandidate>();
    if (File.Exists(fullInputPath))
    {
      if (SupportedAudio.IsSupported(fullInputPath)) candidates.Add(new SourceCandidate(fullInputPath, Path.GetFileName(fullInputPath)));
      else errors.Add(new DiscoveryDiagnostic("discovery.input-unsupported", "The input file is not a supported audio source.", fullInputPath));
    }
    else if (Directory.Exists(fullInputPath))
    {
      EnumerateDirectory(fullInputPath, fullInputPath, candidates, errors, warnings, cancellationToken);
    }
    else
    {
      errors.Add(new DiscoveryDiagnostic("discovery.input-unreadable", "The input path does not exist or cannot be read.", fullInputPath));
    }

    return DeduplicateCandidates(candidates);
  }

  private static SourceCandidate[] DeduplicateCandidates(IEnumerable<SourceCandidate> candidates)
  {
    var normalized = candidates
      .GroupBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
      .Select(group => group.First())
      .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
      .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
      .ToArray();

    return normalized
      .GroupBy(candidate => SourceIdentity.TryGetPhysicalId(candidate.FullPath) ?? candidate.FullPath, StringComparer.OrdinalIgnoreCase)
      .Select(group => group.First())
      .ToArray();
  }

  private static void EnumerateDirectory(string rootPath, string directoryPath, List<SourceCandidate> candidates, List<DiscoveryDiagnostic> errors, List<DiscoveryDiagnostic> warnings, CancellationToken cancellationToken)
  {
    IEnumerable<string> entries;
    try { entries = Directory.EnumerateFileSystemEntries(directoryPath).ToArray(); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      errors.Add(new DiscoveryDiagnostic("discovery.path-unreadable", exception.Message, directoryPath, RelativePath(rootPath, directoryPath)));
      return;
    }

    foreach (var entry in entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
      FileAttributes attributes;
      try { attributes = File.GetAttributes(entry); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        errors.Add(new DiscoveryDiagnostic("discovery.path-unreadable", exception.Message, entry, RelativePath(rootPath, entry)));
        continue;
      }

      if ((attributes & FileAttributes.ReparsePoint) != 0)
      {
        warnings.Add(new DiscoveryDiagnostic("discovery.reparse-point-skipped", "A reparse point was skipped during source discovery.", entry, RelativePath(rootPath, entry)));
        continue;
      }

      if ((attributes & FileAttributes.Directory) != 0)
      {
        EnumerateDirectory(rootPath, entry, candidates, errors, warnings, cancellationToken);
      }
      else if (SupportedAudio.IsSupported(entry))
      {
        var fullPath = Path.GetFullPath(entry);
        candidates.Add(new SourceCandidate(fullPath, RelativePath(rootPath, fullPath)));
      }
    }
  }

  private static string? FindReparsePoint(string fullPath)
  {
    var root = Path.GetPathRoot(fullPath);
    if (string.IsNullOrEmpty(root)) return null;
    var current = root;
    foreach (var segment in Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    {
      current = Path.Combine(current, segment);
      try
      {
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return current;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
    return null;
  }

  private static string RelativePath(string rootPath, string path) => Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

  private sealed record SourceCandidate(string FullPath, string RelativePath);
}
