using BookSplice.Core.Metadata;
using BookSplice.Core.Naming;
using BookSplice.Core.Planning;
using BookSplice.Core.Settings;
using BookSplice.Core.Validation;

namespace BookSplice.Core.Tests.Naming;

public sealed class AtomicPublisherBindingTests
{
  [Fact]
  public async Task PublishAsyncRejectsSuccessfulReportBoundToADifferentExistingTemporaryOutput()
  {
    using var root = new TemporaryDirectory();
    var firstTemporary = root.CreateFile("first.m4b", "first media");
    var secondTemporary = root.CreateFile("second.m4b", "second media");
    var plan = Plan(root.GetPath("staging", "Book.m4b"));
    var otherPlan = Plan(root.GetPath("other-staging", "Other.m4b"));
    var report = ValidationReport.Passed(secondTemporary, [], otherPlan.PlanId);

    var result = await new AtomicPublisher().PublishAsync(plan, report, CancellationToken.None);

    Assert.Equal(PublicationStatus.Rejected, result.Status);
    Assert.True(File.Exists(firstTemporary));
    Assert.True(File.Exists(secondTemporary));
    Assert.False(Directory.Exists(Path.GetDirectoryName(plan.OutputPath)!));
  }

  [Fact]
  public async Task PublishAsyncRejectsAnUnboundSuccessfulReportBeforeDestinationMutation()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var plan = Plan(root.GetPath("staging", "Book.m4b"));

    var result = await new AtomicPublisher().PublishAsync(plan, ValidationReport.Passed(temporary, []), CancellationToken.None);

    Assert.Equal(PublicationStatus.Rejected, result.Status);
    Assert.True(File.Exists(temporary));
    Assert.False(Directory.Exists(Path.GetDirectoryName(plan.OutputPath)!));
  }

  private static ConversionPlan Plan(string outputPath)
    => new([], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, CollisionPolicy.AvoidCollision, outputPath, AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4");

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("booksplice-publish-binding-").FullName;
    public string Path { get; }
    public string GetPath(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public string CreateFile(string name, string contents) { var path = GetPath(name); File.WriteAllText(path, contents); return path; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}
