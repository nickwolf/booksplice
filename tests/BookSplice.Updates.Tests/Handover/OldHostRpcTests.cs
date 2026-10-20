using System.Text;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

public sealed class OldHostRpcTests
{
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
