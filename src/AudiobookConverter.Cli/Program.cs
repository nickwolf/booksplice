using AudiobookConverter.Cli;
using AudiobookConverter.FFmpeg.Tools;
using System.Text;
using System.Text.Json;

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
  var localAppData = Environment.GetEnvironmentVariable("AUDIOBOOKCONVERTER_LOCAL_APP_DATA");
  if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  var mediaTools = Environment.GetEnvironmentVariable("AUDIOBOOKCONVERTER_FFMPEG_DIR");
  if (string.IsNullOrWhiteSpace(mediaTools)) mediaTools = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
  var application = CliComposition.Create(localAppData, mediaTools);
  return (int)await application.RunAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
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
