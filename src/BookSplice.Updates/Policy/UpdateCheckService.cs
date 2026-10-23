using BookSplice.Updates.Transport;

namespace BookSplice.Updates.Policy;

public enum CheckKind { Skipped, Success, UnavailableAsset, Ambiguous, TransportFailure, RateLimited }

public sealed record CheckResult(CheckKind Kind, SelectionResult? Selection, string? Error, DateTimeOffset? RetryAfter);

public interface ICheckState
{
  DateTimeOffset? LastSuccess { get; set; }
  DateTimeOffset? NextScheduledCheck { get; set; }
}

public sealed class MemoryCheckState : ICheckState
{
  public DateTimeOffset? LastSuccess { get; set; }
  public DateTimeOffset? NextScheduledCheck { get; set; }
}

public sealed class UpdateCheckService(IReleaseClient client, ICheckState state, TimeProvider clock)
{
  private readonly object _sync = new();
  private Task<CheckResult>? _inflight;

  public Task<CheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
  {
    Task<CheckResult> check;
    lock (_sync)
    {
      if (_inflight is not null)
      {
        return _inflight.WaitAsync(cancellationToken);
      }

      var now = clock.GetUtcNow();
      if (!manual)
      {
        state.NextScheduledCheck ??= CheckSchedule.NextDue(now, state.LastSuccess, null);
        if (now < state.NextScheduledCheck.Value)
        {
          return Task.FromResult(new CheckResult(CheckKind.Skipped, null, null, state.NextScheduledCheck));
        }
      }

      check = Task.Run(RunCheckAsync);
      _inflight = check;
      _ = check.ContinueWith(_ =>
      {
        lock (_sync)
        {
          if (ReferenceEquals(_inflight, check))
          {
            _inflight = null;
          }
        }
      }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    return check.WaitAsync(cancellationToken);
  }

  private async Task<CheckResult> RunCheckAsync()
  {
    try
    {
      var releases = await client.FetchAsync(CancellationToken.None);
      var selection = ReleaseSelector.Select(releases);
      var now = clock.GetUtcNow();
      state.LastSuccess = now;
      state.NextScheduledCheck = CheckSchedule.NextDue(now, now, null);
      return selection.Kind switch
      {
        SelectionKind.Ambiguous => new CheckResult(CheckKind.Ambiguous, selection, selection.Error, null),
        SelectionKind.Available when !selection.Installable => new CheckResult(CheckKind.UnavailableAsset, selection, selection.Error, null),
        _ => new CheckResult(CheckKind.Success, selection, null, null)
      };
    }
    catch (ReleaseTransportException exception)
    {
      var now = clock.GetUtcNow();
      state.NextScheduledCheck = CheckSchedule.NextFailureDue(now, exception.RetryAfter);
      return new CheckResult(exception.RetryAfter is null ? CheckKind.TransportFailure : CheckKind.RateLimited, null, exception.Message, exception.RetryAfter);
    }
    catch (HttpRequestException exception)
    {
      var now = clock.GetUtcNow();
      state.NextScheduledCheck = CheckSchedule.NextFailureDue(now, null);
      return new CheckResult(CheckKind.TransportFailure, null, exception.Message, null);
    }
  }
}
