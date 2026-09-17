using AudiobookConverter.Core.Analysis;

namespace AudiobookConverter.Cli;

public enum CliExitCode
{
  Success = 0,
  UnexpectedFailure = 1,
  UsageError = 2,
  InvalidInput = 3,
  OrderingDecisionRequired = 4,
  ExecutionFailure = 5,
  ValidationFailure = 6,
  PublicationFailure = 7,
  Cancelled = 8,
}

public static class CliExitCodes
{
  public static CliExitCode FromStatus(ConversionTerminalStatus status) => status switch
  {
    ConversionTerminalStatus.Succeeded or ConversionTerminalStatus.DryRun => CliExitCode.Success,
    ConversionTerminalStatus.InvalidInput => CliExitCode.InvalidInput,
    ConversionTerminalStatus.DecisionRequired => CliExitCode.OrderingDecisionRequired,
    ConversionTerminalStatus.ExecutionFailed => CliExitCode.ExecutionFailure,
    ConversionTerminalStatus.ValidationFailed => CliExitCode.ValidationFailure,
    ConversionTerminalStatus.PublicationFailed => CliExitCode.PublicationFailure,
    ConversionTerminalStatus.Cancelled => CliExitCode.Cancelled,
    _ => CliExitCode.UnexpectedFailure,
  };
}
