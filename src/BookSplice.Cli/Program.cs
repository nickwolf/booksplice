using BookSplice.Cli;
using BookSplice.FFmpeg.Tools;
using System.Text;
using System.Text.Json;

if (args is ["--help"] or ["-h"])
{
  Console.WriteLine("""
    booksplice <source> [options]
      --output <folder>                 Existing output folder (or saved setting)
      --quality <profile>               efficient, balanced, high-quality, preserve-more
      --bitrate <32-320>                 Custom AAC bitrate in kbps
      --jobs <1-32>                      Parallel segment encoders
      --order <natural|metadata>        Explicit source ordering candidate
      --metadata-profile <name>          GenericMp4 or NickMp3tag
      --validation <lightweight|full>    Output validation level
      --channels <preserve|mono|stereo>   Output channel handling
      --chapters | --no-chapters         Chapter creation
      --overwrite                       Replace an existing output after validation
      --dry-run                         Plan without writing output or audit files
      --json                            Newline-delimited JSON; never prompts
    Terminal runs prompt when track order needs a decision. Redirected input never prompts.
    Use the BookSplice GUI Settings window to save defaults.
    """);
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
  var localAppData = Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA");
  if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  var mediaTools = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
  if (string.IsNullOrWhiteSpace(mediaTools)) mediaTools = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
  var application = CliComposition.Create(localAppData, mediaTools);
  return (int)await application.RunAsync(args, Console.Out, Console.Error, cancellation.Token, Console.IsInputRedirected ? null : Console.In).ConfigureAwait(false);
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
