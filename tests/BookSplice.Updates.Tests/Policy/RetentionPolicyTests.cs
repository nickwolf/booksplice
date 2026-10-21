using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Policy;

public sealed class RetentionPolicyTests
{
  private static readonly FileIdentity Identity = new(1, "0123456789ABCDEF0123456789ABCDEF");

  private static readonly string[] StructuralKinds =
    ["managed-root", "records", "versions", "staging", "trash"];

  private static OwnedDirectory Owned(string path, string kind) =>
    new(path, Identity, Guid.NewGuid(), Guid.NewGuid(), kind);

  [Fact]
  public void ProtectedPathsAndStructuralDirectoriesAreNeverSelected()
  {
    var current = Owned(@"C:\m\versions\2.0.0", "version");
    var previous = Owned(@"C:\m\versions\1.9.0", "version");
    var old = Owned(@"C:\m\versions\1.0.0", "version");
    var stage = Owned(@"C:\m\staging\op", "stage");
    var structural = StructuralKinds.Select(kind => Owned(@"C:\m\" + kind, kind));

    var selected = RetentionPolicy.Select([current, previous, old, stage, .. structural],
      new ProtectedTargets([current.Path.ToUpperInvariant(), previous.Path]));

    Assert.Equal([old, stage], selected);
  }
}
