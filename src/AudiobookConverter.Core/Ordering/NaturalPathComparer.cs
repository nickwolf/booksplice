namespace AudiobookConverter.Core.Ordering;

public sealed class NaturalPathComparer : IComparer<string>
{
  public int Compare(string? left, string? right)
  {
    if (ReferenceEquals(left, right)) return 0;
    if (left is null) return -1;
    if (right is null) return 1;
    var leftIndex = 0;
    var rightIndex = 0;
    while (leftIndex < left.Length && rightIndex < right.Length)
    {
      if (char.IsAsciiDigit(left[leftIndex]) && char.IsAsciiDigit(right[rightIndex]))
      {
        var numericResult = CompareNumericRun(left, ref leftIndex, right, ref rightIndex, out var sourceSpellingsDiffer);
        if (numericResult != 0) return numericResult;
        if (sourceSpellingsDiffer) return string.Compare(left, right, StringComparison.Ordinal);
        continue;
      }
      var insensitiveResult = string.Compare(left, leftIndex, right, rightIndex, 1, StringComparison.OrdinalIgnoreCase);
      if (insensitiveResult != 0) return insensitiveResult;
      leftIndex++;
      rightIndex++;
    }
    return string.Compare(left, right, StringComparison.Ordinal);
  }

  private static int CompareNumericRun(string left, ref int leftIndex, string right, ref int rightIndex, out bool sourceSpellingsDiffer)
  {
    var leftStart = leftIndex;
    var rightStart = rightIndex;
    while (leftIndex < left.Length && char.IsAsciiDigit(left[leftIndex])) leftIndex++;
    while (rightIndex < right.Length && char.IsAsciiDigit(right[rightIndex])) rightIndex++;
    var leftSignificant = left[leftStart..leftIndex].TrimStart('0');
    var rightSignificant = right[rightStart..rightIndex].TrimStart('0');
    sourceSpellingsDiffer = !left.AsSpan(leftStart, leftIndex - leftStart).SequenceEqual(right.AsSpan(rightStart, rightIndex - rightStart));
    if (leftSignificant.Length == 0) leftSignificant = "0";
    if (rightSignificant.Length == 0) rightSignificant = "0";
    var lengthResult = leftSignificant.Length.CompareTo(rightSignificant.Length);
    return lengthResult != 0 ? lengthResult : string.Compare(leftSignificant, rightSignificant, StringComparison.Ordinal);
  }
}
