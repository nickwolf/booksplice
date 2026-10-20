using System.Text;
using System.Globalization;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Tests.Windows;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

public sealed class OldHostRpcTests
{
  [Fact]
  public async Task SeparateNativeProcessesCompleteAuthenticatedHostCommands()
  {
    if (!OperatingSystem.IsWindows()) return;
    var result = await RunSeparateProcessAsync(wrongExpectedPeer: false);
    Assert.Equal("ok|False|freeze,hide,restore,exit", result);
  }

  [Fact]
  public async Task WrongNativeCoordinatorPeerLeavesOldHostFrozen()
  {
    if (!OperatingSystem.IsWindows()) return;
    var result = await RunSeparateProcessAsync(wrongExpectedPeer: true);
    Assert.Equal("IOException|True|", result);
  }

  private static async Task<string> RunSeparateProcessAsync(bool wrongExpectedPeer)
  {
    var directory = Path.Combine(Path.GetTempPath(),
      "booksplice-rpc-native-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      using var local = SupervisedProcess.CaptureCurrent();
      using var executable = VerifiedExecutable.PinFile(ProcessSupervisorTests.HelperPath());
      using var decoy = wrongExpectedPeer ? ProcessSupervisor.Start(executable, ["hold"],
        new Dictionary<string, string>()) : null;
      var expected = decoy ?? local;
      var ticketPath = Path.Combine(directory, "ticket.txt");
      var resultPath = Path.Combine(directory, "result.txt");
      var transaction = Guid.NewGuid();
      var arguments = new[]
      {
        "rpc-old-host", ticketPath, resultPath, transaction.ToString("D"),
        expected.Identity.Pid.ToString(CultureInfo.InvariantCulture),
        expected.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
        expected.Identity.ExecutablePath,
        expected.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
        expected.ImageIdentity.FileIdHex, expected.ImageSha256, "frozen"
      };
      using var child = ProcessSupervisor.Start(executable, arguments,
        new Dictionary<string, string>());
      try
      {
        await WaitForFileAsync(ticketPath, child, timeout.Token);
        var ticket = OldHostRpcTicket.Parse(File.ReadAllText(ticketPath));
        if (wrongExpectedPeer)
        {
          await Assert.ThrowsAnyAsync<Exception>(async () =>
          {
            using var client = await OldHostRpcClient.ConnectAsync(ticket, child,
              local, timeout.Token);
            await client.RestoreAndAcquireGuiAsync(timeout.Token);
          });
        }
        else
        {
          using var client = await OldHostRpcClient.ConnectAsync(ticket, child,
            local, timeout.Token);
          Assert.True(await client.FreezeAsync(timeout.Token));
          Assert.True(await client.HideAndReleaseGuiAsync(timeout.Token));
          Assert.True(await client.RestoreAndAcquireGuiAsync(timeout.Token));
          await client.ExitAsync(timeout.Token);
        }
        if (wrongExpectedPeer)
        {
          await WaitForFileAsync(resultPath, child, timeout.Token);
          Assert.False(child.HasExited);
          Assert.Equal("IOException|True|", File.ReadAllText(resultPath));
          await child.StopAsync(CancellationToken.None);
        }
        else await child.WaitForExitAsync(timeout.Token);
        Assert.True(File.Exists(resultPath));
        return File.ReadAllText(resultPath);
      }
      finally
      {
        if (!child.HasExited) await child.StopAsync(CancellationToken.None);
        if (decoy is { HasExited: false }) await decoy.StopAsync(CancellationToken.None);
      }
    }
    finally { Directory.Delete(directory, true); }
  }

  private static async Task WaitForFileAsync(string path, SupervisedProcess child,
    CancellationToken token)
  {
    while (!File.Exists(path))
    {
      token.ThrowIfCancellationRequested();
      if (child.HasExited) throw new IOException("The native RPC helper exited before publishing its ticket.");
      await Task.Delay(20, token);
    }
  }

  [Fact]
  public async Task NativeConnectionDispatchesTheOrderedOldHostCommands()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var local = SupervisedProcess.CaptureCurrent();
    using var server = new OldHostRpcServer(local, Guid.NewGuid());
    var host = new RecordingHost();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var serving = server.ServeAsync(local, host, timeout.Token);
    using var client = await OldHostRpcClient.ConnectAsync(
      OldHostRpcTicket.Parse(server.Ticket.Encode()), local, local, timeout.Token);
    Assert.True(await client.FreezeAsync(timeout.Token));
    Assert.True(await client.HideAndReleaseGuiAsync(timeout.Token));
    Assert.True(await client.RestoreAndAcquireGuiAsync(timeout.Token));
    await client.ExitAsync(timeout.Token);
    await serving;
    Assert.Equal(["freeze", "hide", "restore", "exit"], host.Events);
  }

  [Fact]
  public async Task WrongNonceCannotInvokeTheFrozenHost()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var local = SupervisedProcess.CaptureCurrent();
    using var server = new OldHostRpcServer(local, Guid.NewGuid());
    var host = new RecordingHost();
    Assert.True(await host.FreezeAsync());
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var serving = server.ServeAsync(local, host, timeout.Token);
    var wrong = server.Ticket with
    {
      Nonce = Convert.ToBase64String(UpdatePipe.NewNonce())
    };
    using var client = await OldHostRpcClient.ConnectAsync(wrong, local, local,
      timeout.Token);
    var command = client.RestoreAndAcquireGuiAsync(timeout.Token);
    await Assert.ThrowsAsync<InvalidDataException>(() => serving);
    server.Dispose();
    await Assert.ThrowsAnyAsync<Exception>(() => command);
    Assert.Equal(["freeze"], host.Events);
  }

  [Fact]
  public void TicketRejectsNullAndDuplicateNestedIdentity()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var local = SupervisedProcess.CaptureCurrent();
    using var server = new OldHostRpcServer(local, Guid.NewGuid());
    var encoded = server.Ticket.Encode();
    var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    var nullIdentity = json.Replace("\"OldGui\":{", "\"OldGui\":null,\"spare\":{",
      StringComparison.Ordinal);
    Assert.ThrowsAny<Exception>(() => OldHostRpcTicket.Parse(
      Convert.ToBase64String(Encoding.UTF8.GetBytes(nullIdentity))));
    var duplicate = json.Replace("\"Pid\":", "\"Pid\":999,\"Pid\":",
      StringComparison.Ordinal);
    Assert.Throws<InvalidDataException>(() => OldHostRpcTicket.Parse(
      Convert.ToBase64String(Encoding.UTF8.GetBytes(duplicate))));
  }

  private sealed class RecordingHost : IOldApplicationHost
  {
    public List<string> Events { get; } = [];
    public Task<bool> FreezeAsync(CancellationToken cancellationToken = default)
    { Events.Add("freeze"); return Task.FromResult(true); }
    public Task<bool> HideAndReleaseGuiAsync(CancellationToken cancellationToken = default)
    { Events.Add("hide"); return Task.FromResult(true); }
    public Task<bool> RestoreAndAcquireGuiAsync(CancellationToken cancellationToken = default)
    { Events.Add("restore"); return Task.FromResult(true); }
    public Task ExitAsync(CancellationToken cancellationToken = default)
    { Events.Add("exit"); return Task.CompletedTask; }
  }
}
