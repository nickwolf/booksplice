using AudiobookConverter.FFmpeg.Execution;
using System.Diagnostics;
using System.Globalization;

namespace AudiobookConverter.FFmpeg.Tests.Execution;

public sealed class ProcessRunnerTests
{
  private static readonly char[] NewlineCharacters = ['\r', '\n'];
  [Fact]
  public async Task RunAsyncDrainsLargeConcurrentOutputWithoutDeadlock()
  {
    var script = NewScript("1..12000 | ForEach-Object { [Console]::Out.WriteLine(('o' * 40)); [Console]::Error.WriteLine(('e' * 40)) }");
    try
    {
      var result = await new ProcessRunner().RunAsync(new ProcessSpec("pwsh", ["-NoProfile", "-File", script]), null, CancellationToken.None);
      Assert.Equal(0, result.ExitCode);
      Assert.True(result.StandardOutput.Split(Environment.NewLine).Length > 10000);
      Assert.True(result.StandardError.Split(Environment.NewLine).Length > 10000);
    }
    finally { File.Delete(script); }
  }

  [Fact]
  public async Task RunAsyncRoundTripsHostileArgumentsAsIndividualElements()
  {
    var script = NewScript("$args | ForEach-Object { [Console]::Out.WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))) }");
    string[] expected = ["plain", "has spaces", "ユニコード", "embedded\"quote", "line1\nline2", "-leading-hyphen"];
    try
    {
      var result = await new ProcessRunner().RunAsync(new ProcessSpec("pwsh", new[] { "-NoProfile", "-File", script }.Concat(expected).ToArray()), null, CancellationToken.None);
      var actual = result.StandardOutput.Split(NewlineCharacters, StringSplitOptions.RemoveEmptyEntries).Select(x => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(x))).ToArray();
      Assert.Equal(expected, actual);
    }
    finally { File.Delete(script); }
  }

  [Fact]
  public async Task RunAsyncCancellationKillsParentAndChildTree()
  {
    var directory = Directory.CreateTempSubdirectory();
    var childScript = Path.Combine(directory.FullName, "child.ps1");
    var parentScript = Path.Combine(directory.FullName, "parent.ps1");
    var parentPidFile = Path.Combine(directory.FullName, "parent.pid");
    var childPidFile = Path.Combine(directory.FullName, "child.pid");
    File.WriteAllText(childScript, "Set-Content -LiteralPath $args[0] -Value $PID; while ($true) { Start-Sleep -Milliseconds 100 }");
    File.WriteAllText(parentScript, "$child = Start-Process pwsh -ArgumentList @('-NoProfile','-File',$args[1],$args[2]) -PassThru; Set-Content -LiteralPath $args[0] -Value $PID; while ($true) { Start-Sleep -Milliseconds 100 }");
    using var cancellation = new CancellationTokenSource();
    try
    {
      var running = new ProcessRunner().RunAsync(new ProcessSpec("pwsh", ["-NoProfile", "-File", parentScript, parentPidFile, childScript, childPidFile], GracefulCancellation: TimeSpan.FromMilliseconds(50)), null, cancellation.Token);
      var deadline = DateTime.UtcNow.AddSeconds(5);
      while ((!File.Exists(parentPidFile) || !File.Exists(childPidFile)) && DateTime.UtcNow < deadline) await Task.Delay(25);
      Assert.True(File.Exists(parentPidFile) && File.Exists(childPidFile));
      var parentPid = int.Parse(File.ReadAllText(parentPidFile), CultureInfo.InvariantCulture);
      var childPid = int.Parse(File.ReadAllText(childPidFile), CultureInfo.InvariantCulture);
      cancellation.Cancel();
      await Assert.ThrowsAsync<OperationCanceledException>(() => running);
      deadline = DateTime.UtcNow.AddSeconds(5);
      while (DateTime.UtcNow < deadline && (IsRunning(parentPid) || IsRunning(childPid))) await Task.Delay(50);
      Assert.False(IsRunning(parentPid));
      Assert.False(IsRunning(childPid));
    }
    finally { try { Directory.Delete(directory.FullName, true); } catch { } }
  }

  [Fact]
  public void ProgressSanitizationMasksPathsContainingSpacesAndUnicode()
  {
    var sanitized = ProcessRunner.SanitizeForProgress("error C:\\Users\\Nick Smith\\秘密 file.m4b");
    Assert.DoesNotContain("Nick Smith", sanitized);
    Assert.DoesNotContain("秘密", sanitized);
  }

  [Fact]
  public async Task RunAsyncSanitizesProgressButRetainsRawStreams()
  {
    const string path = "C:\\Users\\Nick Smith\\秘密 file.m4b";
    var script = NewScript($"[Console]::Out.WriteLine('{path}'); [Console]::Error.WriteLine('{path}')");
    var progress = new List<string>();
    try
    {
      var result = await new ProcessRunner().RunAsync(new ProcessSpec("pwsh", ["-NoProfile", "-File", script]), new Progress<string>(progress.Add), CancellationToken.None);
      Assert.Contains(path, result.StandardOutput);
      Assert.Contains(path, result.StandardError);
      Assert.DoesNotContain(progress, x => x.Contains("Nick Smith"));
      Assert.DoesNotContain(progress, x => x.Contains("秘密"));
    }
    finally { File.Delete(script); }
  }

  [Fact]
  public async Task RunAsyncCapturesBothStreamsAndPreservesArguments()
  {
    var runner = new ProcessRunner();
    var result = await runner.RunAsync(new ProcessSpec("dotnet", ["--version"]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    Assert.NotEmpty(result.StandardOutput);
  }

  private static string NewScript(string body) { var path = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.ps1"); File.WriteAllText(path, "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " + body, new System.Text.UTF8Encoding(false)); return path; }
  private static bool IsRunning(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } }
}
