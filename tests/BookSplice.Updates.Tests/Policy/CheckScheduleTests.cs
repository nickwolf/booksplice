using BookSplice.Updates.Policy;
using BookSplice.Updates.Transport;

namespace BookSplice.Updates.Tests.Policy;

public sealed class CheckScheduleTests
{
  private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

  [Fact]
  public void NullOrFutureSuccessWaitsSixtySeconds()
  {
    Assert.Equal(Now.AddSeconds(60), CheckSchedule.NextDue(Now, null, null));
    Assert.Equal(Now.AddSeconds(60), CheckSchedule.NextDue(Now, Now.AddHours(1), null));
  }

  [Fact]
  public void SuccessWaitsTwentyFourHours()
  {
    Assert.Equal(Now.AddHours(24), CheckSchedule.NextDue(Now, Now, null));
    Assert.Equal(Now.AddSeconds(60), CheckSchedule.NextDue(Now, Now.AddDays(-2), null));
  }

  [Fact]
  public void FailureWaitsOneHourAndLaterResetWins()
  {
    Assert.Equal(Now.AddHours(1), CheckSchedule.NextFailureDue(Now, null));
    Assert.Equal(Now.AddHours(3), CheckSchedule.NextFailureDue(Now, Now.AddHours(3)));
  }

  [Fact]
  public async Task SimultaneousManualAndScheduledRequestsShareOneFetch()
  {
    var source = new TaskCompletionSource<IReadOnlyList<ReleaseCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new DeferredClient(source.Task);
    var state = new MemoryCheckState { NextScheduledCheck = Now };
    var service = new UpdateCheckService(client, state, new FixedClock(Now));
    var scheduled = service.CheckAsync(false, CancellationToken.None);
    var manual = service.CheckAsync(true, CancellationToken.None);
    source.SetResult([]);
    Assert.Equal(CheckKind.Success, (await scheduled).Kind);
    Assert.Equal(CheckKind.Success, (await manual).Kind);
    Assert.Equal(1, client.Calls);
  }

  [Fact]
  public async Task RateLimitDeadlineControlsNextCheck()
  {
    var state = new MemoryCheckState { NextScheduledCheck = Now };
    var service = new UpdateCheckService(new FailingClient(new ReleaseTransportException("limited", Now.AddHours(3))), state, new FixedClock(Now));
    Assert.Equal(CheckKind.RateLimited, (await service.CheckAsync(true, CancellationToken.None)).Kind);
    Assert.Equal(Now.AddHours(3), state.NextScheduledCheck);
  }

  [Fact]
  public async Task CancelledWaiterDoesNotCancelSharedFetch()
  {
    var source = new TaskCompletionSource<IReadOnlyList<ReleaseCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new DeferredClient(source.Task);
    var service = new UpdateCheckService(client, new MemoryCheckState(), new FixedClock(Now));
    using var cancellation = new CancellationTokenSource();
    var cancelled = service.CheckAsync(true, cancellation.Token);
    var surviving = service.CheckAsync(true, CancellationToken.None);
    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
    source.SetResult([]);
    Assert.Equal(CheckKind.Success, (await surviving).Kind);
    Assert.Equal(1, client.Calls);
  }

  [Fact]
  public async Task SuccessfulCheckSchedulesFromCompletionTime()
  {
    var source = new TaskCompletionSource<IReadOnlyList<ReleaseCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = new MovingClock(Now);
    var state = new MemoryCheckState();
    var service = new UpdateCheckService(new DeferredClient(source.Task), state, clock);
    var check = service.CheckAsync(true, CancellationToken.None);
    clock.Now = Now.AddMinutes(5);
    source.SetResult([]);
    await check;
    Assert.Equal(Now.AddMinutes(5), state.LastSuccess);
    Assert.Equal(Now.AddMinutes(5).AddHours(24), state.NextScheduledCheck);
  }

  private sealed class DeferredClient(Task<IReadOnlyList<ReleaseCandidate>> task) : IReleaseClient
  {
    public int Calls { get; private set; }
    public Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(CancellationToken cancellationToken)
    {
      Calls++;
      return task;
    }
  }

  private sealed class FailingClient(Exception error) : IReleaseClient
  {
    public Task<IReadOnlyList<ReleaseCandidate>> FetchAsync(CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<ReleaseCandidate>>(error);
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }

  private sealed class MovingClock(DateTimeOffset now) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
  }
}
