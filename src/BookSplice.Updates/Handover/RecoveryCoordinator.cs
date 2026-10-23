using System.ComponentModel;
using System.Runtime.Versioning;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

public enum RecoveryOutcomeKind { NothingToRecover, Recovered, UpdateInProgress, CandidateStillRunning }

[SupportedOSPlatform("windows")]
public sealed class RecoveryCoordinator(UpdateJournal journal, IActivityLeaseStore activity,
  CurrentTargetStore? targets = null)
{
  internal Func<UpdateTransaction, bool> EndCandidate { get; init; } = EndRecordedCandidate;

  public RecoveryOutcomeKind Recover()
  {
    if (journal.Read().Value is not { } transaction || transaction.IsFinished())
      return RecoveryOutcomeKind.NothingToRecover;
    using var admission = activity.TryEnterUpdate();
    if (admission is null) return RecoveryOutcomeKind.UpdateInProgress;
    if (journal.Read().Value is { } current && !current.IsFinished())
    {
      // A launcher that died after Starting can leave the candidate running with the instance lease;
      // it must be gone before cleanup or another launch.
      if (!EndCandidate(current)) return RecoveryOutcomeKind.CandidateStillRunning;
      // The pointer moves only after an authenticated Ready, so a moved pointer means the update took effect.
      var committed = current.Phase == UpdatePhase.Starting &&
        RecoveryPolicy.PointsAtNewTarget(current, targets?.Read());
      journal.Transition(current.Id, current.Phase,
        committed ? UpdatePhase.Committed : UpdatePhase.Failed);
    }
    return RecoveryOutcomeKind.Recovered;
  }

  private static bool EndRecordedCandidate(UpdateTransaction transaction)
  {
    if (transaction.Phase != UpdatePhase.Starting || transaction.NewProcess is not { } recorded)
      return true;
    var sha256 = transaction.NewTarget?.Package?.Files.SingleOrDefault(file =>
      file.RelativePath == "BookSplice.Gui.exe")?.Sha256;
    if (transaction.NewTarget is not { } target || sha256 is null) return false;
    using var observation = new RecoveryProcessProbe().Observe(recorded, target.ExecutableIdentity, sha256);
    if (observation.Life == ProcessLife.Exited) return true;
    if (observation.Process is not { } candidate) return false;
    try
    {
      // Without its launcher the candidate exits on its own when the pipe closes; stop it if it lingers.
      using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
      {
        try { candidate.WaitForExitAsync(grace.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
      }
      if (!candidate.HasExited)
      {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        candidate.StopAsync(deadline.Token).GetAwaiter().GetResult();
      }
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or
      OperationCanceledException or Win32Exception)
    { }
    return candidate.HasExited;
  }
}
