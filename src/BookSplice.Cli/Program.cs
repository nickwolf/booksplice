using BookSplice.Cli;
using BookSplice.FFmpeg.Tools;
using BookSplice.Updates.Windows;
using System.Text;
using System.Text.Json;

var localAppData = Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA");
if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (args is ["--help"] or ["-h"])
{
  var profiles = BookSplice.Core.Metadata.MetadataProfileCatalog.BuiltIn;
  try { profiles = BookSplice.Core.Metadata.MetadataProfileCatalog.Load(CliComposition.ProfilesDirectory(localAppData)); }
  catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
  Console.WriteLine(CliHelp.Build(profiles, CliComposition.ProfilesDirectory(localAppData)));
  return 0;
}

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var cancellation = new CancellationTokenSource();
var cancellationCount = 0;
ConsoleCancelEventHandler handler = (_, eventArgs) =>
{
  if (Interlocked.Increment(ref cancellationCount) == 1)
  {
    eventArgs.Cancel = true;
    cancellation.Cancel();
  }
  else
  {
    eventArgs.Cancel = false;
  }
};
Console.CancelKeyPress += handler;
try
{
  var mediaTools = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
  if (string.IsNullOrWhiteSpace(mediaTools)) mediaTools = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
  var dataRoot = Path.Combine(Path.GetFullPath(localAppData), "BookSplice");
  IActivityLeaseStore leases = OperatingSystem.IsWindows() ? new ActivityLeaseStore(dataRoot) : new NoOpActivityLeaseStore();
  return (int)await CliExecutionHost.RunAsync(
    () => CliComposition.Create(localAppData, mediaTools),
    leases, args, Console.Out, Console.Error, cancellation.Token,
    Console.IsInputRedirected ? null : Console.In).ConfigureAwait(false);
}
catch (MediaToolException exception)
{
  Console.Error.WriteLine(exception.Message);
  WriteFailureEvent(args, "ExecutionFailed", CliExitCode.ExecutionFailure);
  return (int)CliExitCode.ExecutionFailure;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
  WriteFailureEvent(args, "Cancelled", CliExitCode.Cancelled);
  return (int)CliExitCode.Cancelled;
}
catch (Exception)
{
  Console.Error.WriteLine("The command failed unexpectedly.");
  WriteFailureEvent(args, "UnexpectedFailure", CliExitCode.UnexpectedFailure);
  return (int)CliExitCode.UnexpectedFailure;
}
finally
{
  Console.CancelKeyPress -= handler;
}

static void WriteFailureEvent(IEnumerable<string> arguments, string status, CliExitCode exitCode)
{
  if (!arguments.Contains("--json", StringComparer.Ordinal)) return;
  Console.Out.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, @event = "final", status, exitCode = (int)exitCode }));
}
