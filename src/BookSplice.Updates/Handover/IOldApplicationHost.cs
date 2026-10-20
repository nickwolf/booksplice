namespace BookSplice.Updates.Handover;

public interface IOldApplicationHost
{
  Task<bool> FreezeAsync(CancellationToken cancellationToken = default);
  Task<bool> HideAndReleaseGuiAsync(CancellationToken cancellationToken = default);
  Task<bool> RestoreAndAcquireGuiAsync(CancellationToken cancellationToken = default);
  Task ExitAsync(CancellationToken cancellationToken = default);
}
