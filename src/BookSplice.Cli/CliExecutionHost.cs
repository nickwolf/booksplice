using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using BookSplice.Updates.Windows;

namespace BookSplice.Cli;

public static class CliExecutionHost
{
  [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "The host contract places optional input after cancellation.")]
  public static async Task<CliExitCode> RunAsync(
    Func<CliApplication> createApplication,
    IActivityLeaseStore leases,
    IReadOnlyList<string> arguments,
    TextWriter output,
    TextWriter error,
    CancellationToken cancellationToken,
    TextReader? input = null)
  {
    ArgumentNullException.ThrowIfNull(createApplication);
    ArgumentNullException.ThrowIfNull(leases);
    cancellationToken.ThrowIfCancellationRequested();
    using var lease = leases.TryEnterJob();
    if (lease is null)
    {
      error.WriteLine("An update is in progress. Try again when it finishes.");
      if (arguments.Contains("--json", StringComparer.Ordinal))
        output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, @event = "final", status = "InvalidInput", exitCode = (int)CliExitCode.UsageError }));
      return CliExitCode.UsageError;
    }

    return await createApplication().RunAsync(arguments, output, error, cancellationToken, input).ConfigureAwait(false);
  }
}
