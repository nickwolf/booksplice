namespace BookSplice.Updates.Handover;

public static class UpdateProtocol
{
  public const int ProtocolVersion = 1;
}

public enum UpdatePhase
{
  Preparing,
  Prepared,
  Starting,
  Ready,
  Committing,
  Committed,
  RollingBack,
  Failed
}

public sealed record ProcessIdentity(int Pid, long StartTimeUtcTicks, string ExecutablePath);
