using BookSplice.Updates.Handover;

namespace BookSplice.Updates.Tests.Handover;

[Collection("WindowsOwnedStorage")]
public sealed class PreparedFailureTests
{
  [Fact]
  public async Task PreparedTransactionCanBeMarkedFailedWithoutChangingTheMapping()
  {
    using var harness = new HandoverHarness();
    var id = await harness.PrepareUpdateAsync();
    Assert.Equal(UpdatePhase.Prepared, harness.Transaction!.Phase);
    harness.Journal.Transition(id, UpdatePhase.Prepared, UpdatePhase.Failed);
    Assert.Equal(UpdatePhase.Failed, harness.Transaction!.Phase);
    Assert.Equal(harness.OldTarget.ExecutablePath, harness.Mapping!.Current.ExecutablePath);
  }
}
