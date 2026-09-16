namespace AudiobookConverter.Core.Analysis;

public interface IJobLogWriter
{
  Task WriteAsync(ConversionAuditRecord record, CancellationToken cancellationToken);
}
