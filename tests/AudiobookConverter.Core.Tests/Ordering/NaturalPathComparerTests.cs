using AudiobookConverter.Core.Ordering;

namespace AudiobookConverter.Core.Tests.Ordering;

public sealed class NaturalPathComparerTests
{
  private readonly NaturalPathComparer _comparer = new();

  [Fact]
  public void Compare_orders_numeric_runs_by_numeric_value()
  {
    var paths = new[] { "10.m4a", "2.m4a", "1.m4a" };

    Array.Sort(paths, _comparer);

    Assert.Equal((IEnumerable<string>)["1.m4a", "2.m4a", "10.m4a"], paths);
  }

  [Fact]
  public void Compare_uses_ordinal_tie_breaking_for_zero_padded_numbers()
  {
    var paths = new[] { "2.m4a", "02.m4a", "002.m4a" };

    Array.Sort(paths, _comparer);

    Assert.Equal((IEnumerable<string>)["002.m4a", "02.m4a", "2.m4a"], paths);
  }

  [Fact]
  public void Compare_is_deterministic_for_unicode_and_case()
  {
    var paths = new[] { "z.m4a", "\u00E4.m4a", "A.m4a", "a.m4a" };

    Array.Sort(paths, _comparer);

    Assert.Equal((IEnumerable<string>)["A.m4a", "a.m4a", "z.m4a", "\u00E4.m4a"], paths);
  }

  [Fact]
  public void Compare_orders_numbered_parts_and_nested_disc_paths()
  {
    var paths = new[] { "CD02/Track01.m4a", "Part 2.m4a", "CD01/Track01.m4a", "Part 1.m4a" };

    Array.Sort(paths, _comparer);

    Assert.Equal((IEnumerable<string>)["CD01/Track01.m4a", "CD02/Track01.m4a", "Part 1.m4a", "Part 2.m4a"], paths);
  }
}
