using System.Diagnostics;
using System.Text;

namespace BookSplice.FFmpeg.Execution;

public sealed class ProcessRunner : IProcessRunner
{
  internal static AsyncLocal<Action<int>?> ProcessStarted { get; } = new();
  private static readonly TimeSpan StopFallback = TimeSpan.FromSeconds(5);

  public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? progress, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(spec.FileName);
    using var process = new Process { StartInfo = CreateStartInfo(spec) };
    var cpuBefore = TimeSpan.Zero;
    try
    {
      if (!process.Start()) throw new InvalidOperationException($"Unable to start process '{spec.FileName}'.");
      ProcessStarted.Value?.Invoke(process.Id);
      cpuBefore = process.TotalProcessorTime;
      var stdout = new StringBuilder();
      var stderr = new StringBuilder();
      // A read from a redirected pipe blocks its thread until the process writes or exits, so each stream gets its own thread instead of holding a thread pool thread for the whole run.
      var outputTask = OnOwnThread(spec.StandardOutputLine is { } onLine ? () => Forward(process.StandardOutput, onLine) : () => Drain(process.StandardOutput, stdout, progress));
      var errorTask = OnOwnThread(() => Drain(process.StandardError, stderr, progress));
      var graceful = spec.GracefulCancellation ?? TimeSpan.FromMilliseconds(250);
      // Stopping starts from the thread that cancels, so a busy thread pool can't hold the kill back until the process finishes by itself.
      using var stopOnCancel = cancellationToken.Register(() => StartStopping(process, graceful));
      try
      {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        // The stop that cancelling started normally ends the process. Kill it here only if that didn't happen.
        await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(graceful + StopFallback, CancellationToken.None)).ConfigureAwait(false);
        if (!process.HasExited)
        {
          try { process.Kill(entireProcessTree: true); }
          catch (InvalidOperationException) when (process.HasExited) { }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        throw new OperationCanceledException(cancellationToken);
      }
      await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
      return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), process.TotalProcessorTime - cpuBefore);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      throw new ProcessExecutionException($"Unable to execute '{spec.FileName}': {exception.Message}", exception);
    }
  }

  private static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
  {
    var info = new ProcessStartInfo { FileName = spec.FileName, WorkingDirectory = spec.WorkingDirectory ?? "", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
    foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
    return info;
  }

  private static Task OnOwnThread(Action read) => Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

  private static void StartStopping(Process process, TimeSpan graceful)
    => new Thread(() =>
    {
      try
      {
        if (graceful <= TimeSpan.Zero || !process.WaitForExit(graceful)) process.Kill(entireProcessTree: true);
      }
      // Best effort: the process may exit or be disposed meanwhile, nothing can observe an error here, and an exception on this thread would end the app. A cancelled run still kills the process itself if this didn't.
#pragma warning disable CA1031
      catch (Exception) { }
#pragma warning restore CA1031
    })
    { IsBackground = true, Name = "BookSplice process stop" }.Start();

  private static void Drain(StreamReader reader, StringBuilder retained, IProgress<string>? progress)
  {
    while (reader.ReadLine() is { } line)
    {
      if (retained.Length > 0) retained.AppendLine();
      retained.Append(line);
      progress?.Report(SanitizeForProgress(line));
    }
  }

  // Large outputs go line by line to the caller instead of being kept in memory.
  private static void Forward(StreamReader reader, Action<string> onLine)
  {
    while (reader.ReadLine() is { } line) onLine(line);
  }

  internal static string SanitizeForProgress(string line)
    => System.Text.RegularExpressions.Regex.Replace(line, @"(?i)(?:[a-z]:|\\\\|/)[^\r\n]*", "<path>");
}

public sealed class ProcessExecutionException(string message, Exception innerException) : Exception(message, innerException);
