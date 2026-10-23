using System.Text.Json;
using System.Diagnostics;
using BookSplice.Cli;
using BookSplice.Core.Settings;
using BookSplice.Updates.Windows;

namespace BookSplice.Cli.Tests;

public sealed class UpdateLeaseTests
{
  [Fact]
  public async Task BusyUpdateSkipsCompositionAndWritesFinalJson()
  {
    var leases = new FakeLeases { Busy = true };
    var output = new StringWriter();
    var error = new StringWriter();
    var exit = await CliExecutionHost.RunAsync(
      () => throw new InvalidOperationException("Factory must not be called"),
      leases, ["private source", "--json"], output, error, CancellationToken.None, null);

    Assert.Equal(CliExitCode.UsageError, exit);
    Assert.Equal(1, leases.Entries);
    Assert.Equal(0, leases.Disposals);
    Assert.DoesNotContain("private source", output.ToString() + error, StringComparison.Ordinal);
    using var final = JsonDocument.Parse(output.ToString());
    Assert.Equal("final", final.RootElement.GetProperty("event").GetString());
    Assert.Equal(2, final.RootElement.GetProperty("exitCode").GetInt32());
  }

  [Fact]
  public async Task CallerLeaseOutlivesTheApplicationRunAndCleanup()
  {
    var leases = new FakeLeases();
    var settings = new PendingSettings();
    var run = CliExecutionHost.RunAsync(
      () => new CliApplication(settings, null!),
      leases, ["source", "--dry-run"], TextWriter.Null, TextWriter.Null, CancellationToken.None, null);

    await settings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(1, leases.Entries);
    Assert.Equal(0, leases.Disposals);
    settings.Complete();
    await run;
    Assert.Equal(1, leases.Disposals);
  }

  [Fact]
  public async Task ProgramUsesTheSameSuffixedDataRootBeforeComposition()
  {
    if (!OperatingSystem.IsWindows()) return;
    var parent = Path.Combine(Path.GetTempPath(), "BookSpliceCliLeaseTests", Guid.NewGuid().ToString("N"));
    using var update = new ActivityLeaseStore(Path.Combine(parent, "BookSplice")).TryEnterUpdate();
    Assert.NotNull(update);
    var cli = Path.Combine(AppContext.BaseDirectory, "booksplice.dll");
    var start = new ProcessStartInfo("dotnet")
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    start.ArgumentList.Add(cli);
    start.ArgumentList.Add("source");
    start.ArgumentList.Add("--json");
    start.Environment["BOOKSPLICE_LOCAL_APP_DATA"] = parent;
    start.Environment["BOOKSPLICE_FFMPEG_DIR"] = Path.Combine(parent, "missing-tools");
    using var process = Process.Start(start)!;
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Assert.Equal(2, process.ExitCode);
    Assert.Contains("update is in progress", error, StringComparison.OrdinalIgnoreCase);
    using var final = JsonDocument.Parse(output);
    Assert.Equal("final", final.RootElement.GetProperty("event").GetString());
    Assert.Equal(2, final.RootElement.GetProperty("exitCode").GetInt32());
  }

  private sealed class FakeLeases : IActivityLeaseStore
  {
    public bool Busy { get; init; }
    public int Entries { get; private set; }
    public int Disposals { get; private set; }
    public IDisposable? TryEnterJob()
    {
      Entries++;
      return Busy ? null : new Lease(() => Disposals++);
    }
    public IDisposable? TryEnterUpdate() => throw new NotSupportedException();
  }

  private sealed class Lease(Action dispose) : IDisposable
  {
    public void Dispose() => dispose();
  }

  private sealed class PendingSettings : ISettingsStore
  {
    public string SettingsPath => "unused";
    private readonly TaskCompletionSource<SettingsLoadResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
      Entered.SetResult();
      return _completion.Task;
    }
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Complete() => _completion.SetException(new IOException("injected"));
  }
}
