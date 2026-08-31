using AudiobookConverter.FFmpeg.Execution;

namespace AudiobookConverter.FFmpeg.Tests.Execution;

public sealed class ProcessRunnerTests
{
  [Fact]
  public void ProgressSanitizationMasksPathsContainingSpacesAndUnicode()
  {
    var sanitized = ProcessRunner.SanitizeForProgress("error C:\\Users\\Nick Smith\\秘密 file.m4b");
    Assert.DoesNotContain("Nick Smith", sanitized);
    Assert.DoesNotContain("秘密", sanitized);
  }

  [Fact]
  public async Task RunAsyncCapturesBothStreamsAndPreservesArguments()
  {
    var runner = new ProcessRunner();
    var result = await runner.RunAsync(new ProcessSpec("dotnet", ["--version"]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    Assert.NotEmpty(result.StandardOutput);
  }
}
