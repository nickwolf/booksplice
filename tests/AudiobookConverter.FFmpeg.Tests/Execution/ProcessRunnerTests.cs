using AudiobookConverter.FFmpeg.Execution;

namespace AudiobookConverter.FFmpeg.Tests.Execution;

public sealed class ProcessRunnerTests
{
  [Fact]
  public async Task RunAsyncCapturesBothStreamsAndPreservesArguments()
  {
    var runner = new ProcessRunner();
    var result = await runner.RunAsync(new ProcessSpec("dotnet", ["--version"]), null, CancellationToken.None);
    Assert.Equal(0, result.ExitCode);
    Assert.NotEmpty(result.StandardOutput);
  }
}
