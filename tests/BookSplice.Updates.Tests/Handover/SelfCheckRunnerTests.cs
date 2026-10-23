using System.Text.Json;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

public sealed class SelfCheckRunnerTests
{
  private static SelfCheckReport Passing() => new(1, "BookSplice", "0.2.0", "win-x64", 1,
    SelfCheckReport.RequiredChecks.ToDictionary(name => name,
      _ => new SelfCheckEntry(true, null), StringComparer.Ordinal));

  [Fact]
  public void PassingJsonStillFailsWhenProcessExitsNonzero()
  {
    var bytes = JsonSerializer.SerializeToUtf8Bytes(Passing());
    Assert.Throws<InvalidDataException>(() => SelfCheckRunner.ValidateResult(1, bytes, "0.2.0"));
    Assert.Equal("0.2.0", SelfCheckRunner.ValidateResult(0, bytes, "0.2.0").Version);
  }

  [Fact]
  public async Task RetainedFastExitingChildCanDeliverCompleteReport()
  {
    if (!OperatingSystem.IsWindows()) return;
    var pipeName = UpdatePipe.NewPipeName();
    var report = JsonSerializer.SerializeToUtf8Bytes(Passing());
    using var server = UpdatePipe.CreateServer(pipeName);
    using var executable = VerifiedExecutable.PinFile(HelperPath());
    using var child = ProcessSupervisor.Start(executable,
      ["selfcheck-report", pipeName, Convert.ToBase64String(report), "0"],
      new Dictionary<string, string>());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var received = await SelfCheckRunner.ReadReportAsync(server, child, timeout.Token);
    await child.WaitForExitAsync(timeout.Token);
    Assert.Equal(0, child.ExitCode);
    Assert.Equal("0.2.0", SelfCheckRunner.ValidateResult(child.ExitCode, received, "0.2.0").Version);
  }

  [Fact]
  public async Task WrongPipePeerAndTimeoutCannotSupplySelfCheckResult()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var executable = VerifiedExecutable.PinFile(HelperPath());
    var report = JsonSerializer.SerializeToUtf8Bytes(Passing());
    using (var server = UpdatePipe.CreateServer(UpdatePipe.NewPipeName()))
    using (var expected = ProcessSupervisor.Start(executable, ["hold"], new Dictionary<string, string>()))
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        SelfCheckRunner.ReadReportAsync(server, expected, timeout.Token));
      await expected.StopAsync();
    }
    var name = UpdatePipe.NewPipeName();
    using var otherServer = UpdatePipe.CreateServer(name);
    using var retained = ProcessSupervisor.Start(executable, ["hold"], new Dictionary<string, string>());
    using var rogue = ProcessSupervisor.Start(executable,
      ["selfcheck-report", name, Convert.ToBase64String(report), "0"],
      new Dictionary<string, string>());
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await Assert.ThrowsAsync<IOException>(() =>
      SelfCheckRunner.ReadReportAsync(otherServer, retained, deadline.Token));
    await rogue.StopAsync();
    await retained.StopAsync();
  }

  [Fact]
  public async Task VerifiedChildDoesNotInheritMediaToolOverrides()
  {
    if (!OperatingSystem.IsWindows()) return;
    var output = Path.Combine(Path.GetTempPath(), "booksplice-media-env-" + Guid.NewGuid().ToString("N"));
    var previousDirectory = Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR");
    var previousData = Environment.GetEnvironmentVariable("FFMPEG_DATADIR");
    try
    {
      Environment.SetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR", "untrusted-tool-directory");
      Environment.SetEnvironmentVariable("FFMPEG_DATADIR", "untrusted-media-data");
      using var executable = VerifiedExecutable.PinFile(HelperPath());
      using var child = ProcessSupervisor.Start(executable, ["write-media-env", output],
        new Dictionary<string, string>());
      await child.WaitForExitAsync();
      Assert.Equal(0, child.ExitCode);
      Assert.Equal("<missing>|<missing>", File.ReadAllText(output));
    }
    finally
    {
      Environment.SetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR", previousDirectory);
      Environment.SetEnvironmentVariable("FFMPEG_DATADIR", previousData);
      if (File.Exists(output)) File.Delete(output);
    }
  }

  [Fact]
  public void EveryNamedCheckIsRequired()
  {
    var report = Passing();
    foreach (var name in SelfCheckReport.RequiredChecks)
    {
      var missing = report with { Checks = new(report.Checks, StringComparer.Ordinal) };
      missing.Checks.Remove(name);
      Assert.Throws<InvalidDataException>(() =>
        SelfCheckReport.Parse(JsonSerializer.SerializeToUtf8Bytes(missing), "0.2.0"));
      var failed = report with { Checks = new(report.Checks, StringComparer.Ordinal) };
      failed.Checks[name] = new(false, null);
      Assert.Throws<InvalidDataException>(() =>
        SelfCheckReport.Parse(JsonSerializer.SerializeToUtf8Bytes(failed), "0.2.0"));
    }
  }

  [Fact]
  public void IdentityAndJsonAreExactAndBounded()
  {
    var report = Passing();
    var parsed = SelfCheckReport.Parse(JsonSerializer.SerializeToUtf8Bytes(report), "0.2.0");
    Assert.Equal(report.Version, parsed.Version);
    Assert.Equal(report.Checks.Keys.Order(), parsed.Checks.Keys.Order());
    foreach (var wrong in new[]
    {
      report with { Product = "Another" }, report with { Version = "0.3.0" },
      report with { Target = "win-arm64" }, report with { ProtocolVersion = 2 }
    })
      Assert.Throws<InvalidDataException>(() =>
        SelfCheckReport.Parse(JsonSerializer.SerializeToUtf8Bytes(wrong), "0.2.0"));
    Assert.ThrowsAny<Exception>(() => SelfCheckReport.Parse(new byte[SelfCheckReport.MaximumBytes + 1], "0.2.0"));
    Assert.ThrowsAny<Exception>(() => SelfCheckReport.Parse("{"u8, "0.2.0"));
    var duplicate = JsonSerializer.Serialize(report).Replace("\"Product\":\"BookSplice\"",
      "\"Product\":\"BookSplice\",\"Product\":\"BookSplice\"", StringComparison.Ordinal);
    Assert.Throws<InvalidDataException>(() => SelfCheckReport.Parse(System.Text.Encoding.UTF8.GetBytes(duplicate), "0.2.0"));
  }

  private static string HelperPath() => Tests.Windows.ProcessSupervisorTests.HelperPath();
}
