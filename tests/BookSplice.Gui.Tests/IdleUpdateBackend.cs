using BookSplice.Gui.Updates;
using BookSplice.Updates.Handover;
using BookSplice.Updates.Policy;

namespace BookSplice.Gui.Tests;

internal sealed class IdleUpdateBackend : IUpdateBackend
{
  public Task<CheckResult> CheckAsync(bool manual, CancellationToken cancellationToken) =>
    Task.FromResult(new CheckResult(CheckKind.Skipped, null, null, null));
  public Task<string> ReadManifestDigestAsync(ReleaseCandidate release, CancellationToken cancellationToken) => Task.FromResult("");
  public bool HasPrevious() => false;
  public Task<UpdateOutcome> InstallAsync(ReleaseCandidate release, ReleaseIdentity identity, string manifestSha256,
    Action shutdown, CancellationToken cancellationToken) => throw new NotSupportedException();
  public Task<UpdateOutcome> RollbackAsync(Action shutdown, CancellationToken cancellationToken) => throw new NotSupportedException();
}
