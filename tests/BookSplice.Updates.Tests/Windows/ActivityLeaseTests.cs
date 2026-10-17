using System.Diagnostics;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

[CollectionDefinition("ActivityLeaseSerial", DisableParallelization = true)]
public sealed class ActivityLeaseSerialGroup;

[Collection("ActivityLeaseSerial")]
public sealed class ActivityLeaseTests
{
  [Fact]
  public void IndependentHandlesPreserveCallerOwnership()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = TempRoot();
    var first = new ActivityLeaseStore(root);
    var second = new ActivityLeaseStore(root);
    using var job1 = first.TryEnterJob();
    using var job2 = second.TryEnterJob();
    Assert.NotNull(job1);
    Assert.NotNull(job2);
    Assert.Null(first.TryEnterUpdate());
    job1.Dispose();
    Assert.Null(first.TryEnterUpdate());
    job2.Dispose();
    using var update = first.TryEnterUpdate();
    Assert.NotNull(update);
    Assert.Null(second.TryEnterJob());
    Assert.Null(second.TryEnterUpdate());
    update.Dispose();
    using var resumed = second.TryEnterJob();
    Assert.NotNull(resumed);
  }

  [Fact]
  public async Task ProcessDeathReleasesJobAndUpdateLocks()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = TempRoot();
    using var child1 = await StartAsync(root, "job");
    using var child2 = await StartAsync(root, "job");
    var store = new ActivityLeaseStore(root + Path.DirectorySeparatorChar);
    Assert.Null(store.TryEnterUpdate());
    child1.Kill();
    await child1.WaitForExitAsync();
    Assert.Null(store.TryEnterUpdate());
    child2.Kill();
    await child2.WaitForExitAsync();
    using (var update = await AcquireEventuallyAsync(store.TryEnterUpdate)) Assert.NotNull(update);
    using var updater = await StartAsync(root, "update");
    Assert.Null(store.TryEnterJob());
    updater.Kill();
    await updater.WaitForExitAsync();
    using var resumed = await AcquireEventuallyAsync(store.TryEnterJob);
    Assert.NotNull(resumed);
  }

  [Fact]
  public async Task GuiInstanceLeaseIsIndependentFromActivity()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = TempRoot();
    using var child = await StartAsync(root, "gui");
    Assert.Null(GuiInstanceLease.TryAcquire(root));
    using var job = new ActivityLeaseStore(root).TryEnterJob();
    Assert.NotNull(job);
    child.Kill();
    await child.WaitForExitAsync();
    using var resumed = await AcquireEventuallyAsync(() => GuiInstanceLease.TryAcquire(root));
    Assert.NotNull(resumed);
  }

  [Fact]
  public async Task RootReplacementIsRejectedAndLiveFilesCannotBeReplaced()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = TempRoot();
    var store = new ActivityLeaseStore(root + Path.DirectorySeparatorChar);
    using (var job = store.TryEnterJob())
    {
      Assert.NotNull(job);
      Assert.ThrowsAny<Exception>(() => Directory.Move(root, root + "-moved"));
      Assert.ThrowsAny<Exception>(() => File.Move(Path.Combine(root, "activity.lock"), Path.Combine(root, "activity.old")));
    }

    var moved = false;
    var deadline = DateTime.UtcNow.AddSeconds(5);
    do
    {
      try { Directory.Move(root, root + "-moved"); moved = true; }
      catch (IOException) { await Task.Delay(25); }
    } while (!moved && DateTime.UtcNow < deadline);
    Assert.True(moved);
    Directory.CreateDirectory(root);
    Assert.Throws<InvalidOperationException>(() => store.TryEnterJob());
    using var fresh = new ActivityLeaseStore(root).TryEnterJob();
    Assert.NotNull(fresh);
  }

  [Fact]
  public void JunctionRootCannotBeAdopted()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = TempRoot();
    var target = TempRoot();
    Directory.CreateDirectory(target);
    using var process = Process.Start(new ProcessStartInfo("cmd.exe")
    {
      ArgumentList = { "/d", "/c", "mklink", "/J", root, target },
      UseShellExecute = false,
      CreateNoWindow = true
    })!;
    process.WaitForExit();
    Assert.Equal(0, process.ExitCode);
    Assert.Throws<NotSupportedException>(() => new ActivityLeaseStore(root));
    Assert.Throws<NotSupportedException>(() => GuiInstanceLease.TryAcquire(root));
  }

  [Fact]
  public void MissingRootBelowJunctionNeverCreatesOutsideDirectory()
  {
    if (!OperatingSystem.IsWindows()) return;
    var link = TempRoot();
    var outside = TempRoot();
    Directory.CreateDirectory(outside);
    using var process = Process.Start(new ProcessStartInfo("cmd.exe")
    {
      ArgumentList = { "/d", "/c", "mklink", "/J", link, outside },
      UseShellExecute = false,
      CreateNoWindow = true
    })!;
    process.WaitForExit();
    Assert.Equal(0, process.ExitCode);
    var missingRoot = Path.Combine(link, "BookSplice");
    Assert.Throws<NotSupportedException>(() => new ActivityLeaseStore(missingRoot));
    Assert.Throws<NotSupportedException>(() => GuiInstanceLease.TryAcquire(missingRoot));
    Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
  }

  private static string TempRoot() => Path.Combine(Path.GetTempPath(), "BookSpliceLeaseTests", Guid.NewGuid().ToString("N"));

  private static async Task<T?> AcquireEventuallyAsync<T>(Func<T?> acquire) where T : class
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    do
    {
      var lease = acquire();
      if (lease is not null) return lease;
      await Task.Delay(25);
    } while (DateTime.UtcNow < deadline);
    return null;
  }

  private static async Task<Process> StartAsync(string root, string kind)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "BookSplice.slnx"))) current = current.Parent;
    Assert.NotNull(current);
    var host = Path.Combine(current.FullName, "tests", "BookSplice.LeaseHost", "bin", "Release", "net10.0-windows", "BookSplice.LeaseHost.dll");
    var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(host);
    start.ArgumentList.Add(root);
    start.ArgumentList.Add(kind);
    var process = Process.Start(start)!;
    try
    {
      var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
      Assert.Equal("ready", line);
      return process;
    }
    catch
    {
      if (!process.HasExited) process.Kill();
      process.Dispose();
      throw;
    }
  }
}
