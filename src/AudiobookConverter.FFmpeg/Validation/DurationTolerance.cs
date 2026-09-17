namespace AudiobookConverter.FFmpeg.Validation;

public static class DurationTolerance
{
  public static long Calculate(long expectedMicroseconds, int sourceFileCount)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(expectedMicroseconds);
    ArgumentOutOfRangeException.ThrowIfNegative(sourceFileCount);
    var percentage = decimal.Ceiling(checked((decimal)expectedMicroseconds / 1000m));
    var perSource = checked((decimal)sourceFileCount * 20_000m);
    return checked((long)decimal.Max(2_000_000m, decimal.Max(percentage, perSource)));
  }
}
