using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Tests.Windows;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

public sealed class UpdatePipeTests
{
  [Fact]
  public void NativePipeAclIsProtectedAndGrantsOnlyCurrentUser()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var server = UpdatePipe.CreateServer(UpdatePipe.NewPipeName());
    var security = server.GetAccessControl();
    var user = WindowsIdentity.GetCurrent().User!;
    Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
    Assert.True(security.AreAccessRulesProtected);
    var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
      .Cast<PipeAccessRule>().ToArray();
    Assert.Single(rules);
    Assert.Equal(user, rules[0].IdentityReference);
    Assert.Equal(AccessControlType.Allow, rules[0].AccessControlType);
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public async Task NativePeerAcceptsReadyAndRejectsWrongNonceOrOversize(bool wrongNonce,
    bool oversize)
  {
    if (!OperatingSystem.IsWindows()) return;
    var pipeName = UpdatePipe.NewPipeName();
    var nonce = UpdatePipe.NewNonce();
    var transaction = Guid.NewGuid();
    using var server = UpdatePipe.CreateServer(pipeName);
    using var local = SupervisedProcess.CaptureCurrent();
    using var capability = VerifiedExecutable.PinFile(ProcessSupervisorTests.HelperPath());
    var supplied = wrongNonce ? UpdatePipe.NewNonce() : nonce;
    var args = oversize ? new[] { "pipe-oversize", pipeName } : new[]
    {
      "pipe-ready", pipeName, transaction.ToString("D"),
      local.Identity.Pid.ToString(CultureInfo.InvariantCulture),
      local.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture), local.Identity.ExecutablePath,
      Convert.ToHexString(supplied), local.ImageIdentity.VolumeSerial.ToString(CultureInfo.InvariantCulture),
      local.ImageIdentity.FileIdHex, local.ImageSha256
    };
    using var child = ProcessSupervisor.Start(capability, args, new Dictionary<string, string>());
    try
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      using var session = await UpdatePipe.AcceptAsync(server, child, transaction, nonce, local,
        timeout.Token);
      if (wrongNonce || oversize)
        await Assert.ThrowsAnyAsync<Exception>(() => session.ReceiveAsync(timeout.Token));
      else
        Assert.Equal(UpdateMessageKind.Ready, (await session.ReceiveAsync(timeout.Token)).Kind);
    }
    finally { await child.StopAsync(); }
  }

  [Fact]
  public void PipeNameAndNonceHaveFixedBounds()
  {
    Assert.Equal(32, UpdatePipe.NewNonce().Length);
    Assert.Equal(66, UpdatePipe.NewPipeName().Length);
    if (OperatingSystem.IsWindows())
      Assert.Throws<ArgumentException>(() => UpdatePipe.CreateServer("arbitrary"));
  }

  [Fact]
  public async Task RejectsDifferentRetainedPeerEvenWhenItConnects()
  {
    if (!OperatingSystem.IsWindows()) return;
    var pipeName = UpdatePipe.NewPipeName();
    var nonce = UpdatePipe.NewNonce();
    var transaction = Guid.NewGuid();
    using var server = UpdatePipe.CreateServer(pipeName);
    using var local = SupervisedProcess.CaptureCurrent();
    using var capability = VerifiedExecutable.PinFile(ProcessSupervisorTests.HelperPath());
    using var child = ProcessSupervisor.Start(capability, ["pipe-oversize", pipeName],
      new Dictionary<string, string>());
    try
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      await Assert.ThrowsAsync<IOException>(() => UpdatePipe.AcceptAsync(server, local,
        transaction, nonce, local, timeout.Token));
    }
    finally { await child.StopAsync(); }
  }

  [Fact]
  public async Task CancellationDoesNotCompleteAnUnconnectedSession()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var server = UpdatePipe.CreateServer(UpdatePipe.NewPipeName());
    using var local = SupervisedProcess.CaptureCurrent();
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      UpdatePipe.AcceptAsync(server, local, Guid.NewGuid(), UpdatePipe.NewNonce(), local,
        cancel.Token));
  }

  [Fact]
  public async Task PendingAcceptKeepsTheOriginalNonceSnapshot()
  {
    if (!OperatingSystem.IsWindows()) return;
    var name = UpdatePipe.NewPipeName();
    using var server = UpdatePipe.CreateServer(name);
    using var local = SupervisedProcess.CaptureCurrent();
    var nonce = UpdatePipe.NewNonce();
    var original = nonce.ToArray();
    var transaction = Guid.NewGuid();
    var pending = UpdatePipe.AcceptAsync(server, local, transaction, nonce, local,
      CancellationToken.None);
    Array.Fill(nonce, (byte)0);
    using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut,
      PipeOptions.Asynchronous);
    await raw.ConnectAsync();
    using var session = await pending;
    await UpdateSession.WriteAsync(raw, Wire(local.Identity, local.Identity, transaction,
      original), CancellationToken.None);
    Assert.Equal(UpdateMessageKind.Ready, (await session.ReceiveAsync()).Kind);
  }

  [Fact]
  public async Task PendingConnectKeepsTheOriginalNonceSnapshot()
  {
    if (!OperatingSystem.IsWindows()) return;
    var name = UpdatePipe.NewPipeName();
    using var local = SupervisedProcess.CaptureCurrent();
    var nonce = UpdatePipe.NewNonce();
    var original = nonce.ToArray();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var pending = UpdatePipe.ConnectAsync(name, local, Guid.NewGuid(), nonce, local,
      timeout.Token);
    Array.Fill(nonce, (byte)0);
    using var server = UpdatePipe.CreateServer(name);
    await server.WaitForConnectionAsync(timeout.Token);
    using var session = await pending;
    await session.SendAsync(UpdateMessageKind.Ready, timeout.Token);
    using var frame = JsonDocument.Parse(await UpdateSession.ReadAsync(server, timeout.Token));
    Assert.Equal(Convert.ToBase64String(original),
      frame.RootElement.GetProperty("Nonce").GetString());
  }

  [Theory]
  [InlineData("numeric")]
  [InlineData("null")]
  [InlineData("duplicate")]
  [InlineData("malformed")]
  [InlineData("transaction")]
  [InlineData("recipient")]
  public async Task RejectsMalformedOrMisbindingFrame(string variant)
  {
    if (!OperatingSystem.IsWindows()) return;
    using var pair = await LocalPairAsync();
    var valid = Encoding.UTF8.GetString(Wire(pair.Local.Identity, pair.Local.Identity,
      pair.Transaction, pair.Nonce));
    var altered = variant switch
    {
      "numeric" => valid.Replace("\"Kind\":\"Ready\"", "\"Kind\":\"0\"", StringComparison.Ordinal),
      "null" => valid.Replace("\"Kind\":\"Ready\"", "\"Kind\":null", StringComparison.Ordinal),
      "duplicate" => valid.Replace("\"Sender\":{\"Pid\":", "\"Sender\":{\"Pid\":1,\"Pid\":", StringComparison.Ordinal),
      "malformed" => "{",
      "transaction" => valid.Replace(pair.Transaction.ToString("D"), Guid.NewGuid().ToString("D"),
        StringComparison.OrdinalIgnoreCase),
      "recipient" => valid.Replace("\"Recipient\":{\"Pid\":" + pair.Local.Identity.Pid.ToString(CultureInfo.InvariantCulture),
        "\"Recipient\":{\"Pid\":999999", StringComparison.Ordinal),
      _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };
    await UpdateSession.WriteAsync(pair.Client, Encoding.UTF8.GetBytes(altered),
      CancellationToken.None);
    await Assert.ThrowsAnyAsync<Exception>(() => pair.Session.ReceiveAsync());
  }

  [Fact]
  public async Task PendingReadyIsRejectedAfterLocalAbortAndLateFramesStayRejected()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var pair = await LocalPairAsync();
    var ready = Wire(pair.Local.Identity, pair.Local.Identity, pair.Transaction, pair.Nonce);
    var length = BitConverter.GetBytes(ready.Length);
    await pair.Client.WriteAsync(length);
    await pair.Client.WriteAsync(ready.AsMemory(0, ready.Length - 1));
    var pending = pair.Session.ReceiveAsync();
    await pair.Session.SendAsync(UpdateMessageKind.Abort);
    await pair.Client.WriteAsync(ready.AsMemory(ready.Length - 1));
    await Assert.ThrowsAnyAsync<Exception>(() => pending);
    await Assert.ThrowsAnyAsync<Exception>(() => pair.Session.ReceiveAsync());
  }

  [Fact]
  public async Task PendingReadyIsRejectedAfterCancellationOrDispose()
  {
    if (!OperatingSystem.IsWindows()) return;
    using var pair = await LocalPairAsync();
    using var cancellation = new CancellationTokenSource();
    var pending = pair.Session.ReceiveAsync(cancellation.Token);
    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    using var another = await LocalPairAsync();
    var pendingDisposed = another.Session.ReceiveAsync();
    another.Session.Dispose();
    await Assert.ThrowsAnyAsync<Exception>(() => pendingDisposed);
  }

  [Fact]
  public async Task ClientRejectsAConnectedServerWithTheWrongRetainedHandle()
  {
    if (!OperatingSystem.IsWindows()) return;
    var name = UpdatePipe.NewPipeName();
    using var server = UpdatePipe.CreateServer(name);
    using var local = SupervisedProcess.CaptureCurrent();
    using var capability = VerifiedExecutable.PinFile(ProcessSupervisorTests.HelperPath());
    using var other = ProcessSupervisor.Start(capability, ["hold"], new Dictionary<string, string>());
    var transaction = Guid.NewGuid();
    var nonce = UpdatePipe.NewNonce();
    try
    {
      var waiting = server.WaitForConnectionAsync();
      await Assert.ThrowsAsync<IOException>(() => UpdatePipe.ConnectAsync(name, other,
        transaction, nonce, local, CancellationToken.None));
      await waiting;
    }
    finally { await other.StopAsync(); }
  }

  private static byte[] Wire(ProcessIdentity sender, ProcessIdentity recipient,
    Guid transaction, byte[] nonce) => JsonSerializer.SerializeToUtf8Bytes(new
    {
      SchemaVersion = 1,
      Kind = "Ready",
      TransactionId = transaction,
      ProtocolVersion = UpdateProtocol.ProtocolVersion,
      Sender = sender,
      Recipient = recipient,
      Nonce = Convert.ToBase64String(nonce)
    });

  private static async Task<LocalPipePair> LocalPairAsync()
  {
    var name = UpdatePipe.NewPipeName();
    var server = UpdatePipe.CreateServer(name);
    var local = SupervisedProcess.CaptureCurrent();
    var transaction = Guid.NewGuid();
    var nonce = UpdatePipe.NewNonce();
    var pending = UpdatePipe.AcceptAsync(server, local, transaction, nonce, local,
      CancellationToken.None);
    var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
      PipeOptions.Asynchronous);
    await client.ConnectAsync();
    return new LocalPipePair(local, client, await pending, transaction, nonce);
  }

  private sealed class LocalPipePair(SupervisedProcess local, NamedPipeClientStream client,
    UpdateSession session, Guid transaction, byte[] nonce) : IDisposable
  {
    public SupervisedProcess Local { get; } = local;
    public NamedPipeClientStream Client { get; } = client;
    public UpdateSession Session { get; } = session;
    public Guid Transaction { get; } = transaction;
    public byte[] Nonce { get; } = nonce;

    public void Dispose()
    {
      Session.Dispose();
      Client.Dispose();
      Local.Dispose();
    }
  }
}
