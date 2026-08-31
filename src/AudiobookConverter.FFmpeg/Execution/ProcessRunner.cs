using System.Diagnostics;
using System.Text;

namespace AudiobookConverter.FFmpeg.Execution;

public sealed class ProcessRunner : IProcessRunner
{
  public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(spec.FileName);
    using var process = new Process { StartInfo = CreateStartInfo(spec) };
    try
    {
      if (!process.Start()) throw new InvalidOperationException($"Unable to start process '{spec.FileName}'.");
      var stdout = new StringBuilder();
      var stderr = new StringBuilder();
      var outputTask = DrainAsync(process.StandardOutput, stdout, progress);
      var errorTask = DrainAsync(process.StandardError, stderr, progress);
      try
      {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        if (!process.HasExited)
        {
          var graceful = spec.GracefulCancellation ?? TimeSpan.FromMilliseconds(250);
          if (graceful > TimeSpan.Zero)
          {
            await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(graceful, CancellationToken.None)).ConfigureAwait(false);
          }
        }
        if (!process.HasExited)
        {
          process.Kill(entireProcessTree: true);
          await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        throw;
      }
      await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
      return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      throw new ProcessExecutionException($"Unable to execute '{spec.FileName}': {exception.Message}", exception);
    }
  }

  private static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
  {
    var info = new ProcessStartInfo { FileName = spec.FileName, WorkingDirectory = spec.WorkingDirectory ?? "", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
    return info;
  }

  private static async Task DrainAsync(StreamReader reader, StringBuilder retained, IProgress<string>? progress)
  {
    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
    {
      if (retained.Length > 0) retained.AppendLine();
      retained.Append(line);
      progress?.Report(Sanitize(line));
    }
  }

  private static string Sanitize(string line)
    => System.Text.RegularExpressions.Regex.Replace(line, @"(?i)([A-Z]:\\|/|\\\\)[^\s]+", "<path>");
}

public sealed class ProcessExecutionException(string message, Exception innerException) : Exception(message, innerException);
