using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Globalization;
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
      Convert.ToHexString(supplied)
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
}
