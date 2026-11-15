using BookSplice.Core.Execution;

namespace BookSplice.Core.Validation;

public interface IExistingFileChecker
{
  Task<ExistingFileCheckReport> CheckAsync(string path, bool fullDecode, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null);
}
