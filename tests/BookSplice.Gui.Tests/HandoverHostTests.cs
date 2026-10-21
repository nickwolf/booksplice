using System.Diagnostics;

namespace BookSplice.Gui.Tests;

public sealed class HandoverHostTests
{
  [Fact]
  public async Task RealWindowAndNativeLeaseRemainSafeAcrossHandoverInIsolatedProcess()
  {
    Assert.True(Environment.UserInteractive,
      "The handover host test requires an interactive Windows desktop.");

    var helper = Path.Combine(AppContext.BaseDirectory, "HandoverProbe",
      "BookSplice.Gui.HandoverProbe.exe");
    Assert.True(File.Exists(helper), $"The built WPF handover probe is missing: {helper}");

    using var process = new Process
    {
      StartInfo = new ProcessStartInfo(helper)
      {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
      },
      EnableRaisingEvents = true,
    };

    Assert.True(process.Start(), "The WPF handover probe could not be started.");
    var stdout = ReadBoundedAsync(process.StandardOutput, 16 * 1024);
    var stderr = ReadBoundedAsync(process.StandardError, 16 * 1024);
    try
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
      await process.WaitForExitAsync(timeout.Token);
      var output = await stdout;
      var error = await stderr;
      Assert.True(process.ExitCode == 0,
        $"WPF handover probe exited with code {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
      Assert.Contains("PASS: real WPF/native handover assertions completed", output);
    }
    catch (OperationCanceledException)
    {
      await StopAndWaitAsync(process);
      var diagnostics = $"WPF handover probe exceeded the 60 second limit.\n" +
        $"stdout:\n{await stdout}\nstderr:\n{await stderr}";
      Assert.Fail(diagnostics);
    }
    catch
    {
      await StopAndWaitAsync(process);
      throw;
    }
  }

  private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars)
  {
    var buffer = new char[1024];
    var result = new System.Text.StringBuilder(Math.Min(maxChars, 4096));
    var truncated = false;
    int count;
    while ((count = await reader.ReadAsync(buffer)) > 0)
    {
      var keep = Math.Min(count, maxChars - result.Length);
      if (keep > 0) result.Append(buffer, 0, keep);
      if (keep < count) truncated = true;
    }
    if (truncated) result.Append("\n[output truncated]");
    return result.ToString();
  }
  private static async Task StopAndWaitAsync(Process process)
  {
    try
    {
      if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    catch (InvalidOperationException) { }
    catch (System.ComponentModel.Win32Exception) { }
    try { await process.WaitForExitAsync(); }
    catch (InvalidOperationException) { }
  }
}
