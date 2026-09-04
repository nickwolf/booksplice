using AudiobookConverter.Core.Metadata;
using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Planning;
using AudiobookConverter.Core.Settings;
using AudiobookConverter.Core.Validation;

namespace AudiobookConverter.Core.Tests.Naming;

public sealed class AtomicPublisherFaultTests
{
  private static readonly Guid TestPlanId = Guid.NewGuid();

  [Fact]
  public async Task PublishAsyncCleansItsPartialAndPreservesTemporaryWhenCopyIsCancelled()
  {
    using var root = new TemporaryDirectory();
    using var cancellation = new CancellationTokenSource();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var final = root.GetPath("staging", "Book.m4b");
    var operations = new HookedOperations { OnCopy = () => cancellation.Cancel() };

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AtomicPublisher(operations).PublishAsync(Plan(final), ValidationReport.Passed(temporary, [], TestPlanId), cancellation.Token));

    Assert.True(File.Exists(temporary));
    Assert.False(File.Exists(final));
    Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(final)!, "*.partial"));
  }

  [Fact]
  public async Task PublishAsyncCleansItsPartialAndPreservesDestinationWhenCancelledBeforeRename()
  {
    using var root = new TemporaryDirectory();
    using var cancellation = new CancellationTokenSource();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var final = root.GetPath("staging", "Book.m4b");
    Directory.CreateDirectory(Path.GetDirectoryName(final)!);
    await File.WriteAllTextAsync(final, "existing media");
    var operations = new HookedOperations { OnCopy = cancellation.Cancel };

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AtomicPublisher(operations).PublishAsync(Plan(final, CollisionPolicy.Overwrite), ValidationReport.Passed(temporary, [], TestPlanId), cancellation.Token));

    Assert.True(File.Exists(temporary));
    Assert.Equal("existing media", await File.ReadAllTextAsync(final));
    Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(final)!, "*.partial"));
  }

  [Fact]
  public async Task PublishAsyncPreservesExistingOverwriteDestinationWhenFinalMoveFails()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var final = root.GetPath("staging", "Book.m4b");
    Directory.CreateDirectory(Path.GetDirectoryName(final)!);
    await File.WriteAllTextAsync(final, "existing media");
    var operations = new HookedOperations { FailOverwriteMove = true };

    var result = await new AtomicPublisher(operations).PublishAsync(Plan(final, CollisionPolicy.Overwrite), ValidationReport.Passed(temporary, [], TestPlanId), CancellationToken.None);

    Assert.Equal(PublicationStatus.Failed, result.Status);
    Assert.Equal("existing media", await File.ReadAllTextAsync(final));
    Assert.True(File.Exists(temporary));
    Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(final)!, "*.partial"));
  }

  [Fact]
  public async Task PublishAsyncRetriesAfterALiveWriterWinsTheRequestedDestination()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var final = root.GetPath("staging", "Book.m4b");
    var operations = new HookedOperations { CreateCompetingDestinationBeforeFirstMove = true };

    var result = await new AtomicPublisher(operations).PublishAsync(Plan(final), ValidationReport.Passed(temporary, [], TestPlanId), CancellationToken.None);

    Assert.Equal(PublicationStatus.Published, result.Status);
    Assert.Equal(Path.Combine(Path.GetDirectoryName(final)!, "Book (2).m4b"), result.FinalPath);
    Assert.Equal("competing media", await File.ReadAllTextAsync(final));
    Assert.Equal("validated media", await File.ReadAllTextAsync(result.FinalPath!));
  }

  private static ConversionPlan Plan(string outputPath, CollisionPolicy policy = CollisionPolicy.AvoidCollision)
    => new([], new BookMetadata(new Dictionary<SemanticField, AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<SemanticField, IReadOnlyList<string>>()), null, [], QualityProfileCatalog.Version1[0], ValidationLevel.Lightweight, policy, outputPath, AudioStrategy.DirectTranscode, [], new SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4", planId: TestPlanId);

  private sealed class HookedOperations : IAtomicPublisherFileOperations
  {
    public Action? OnCopy { get; init; }
    public bool FailOverwriteMove { get; init; }
    public bool CreateCompetingDestinationBeforeFirstMove { get; init; }
    private bool _competingDestinationCreated;

    public async Task CopyDurablyAsync(string source, string partialPath, CancellationToken cancellationToken)
    {
      await File.WriteAllBytesAsync(partialPath, await File.ReadAllBytesAsync(source, cancellationToken), cancellationToken);
      OnCopy?.Invoke();
      cancellationToken.ThrowIfCancellationRequested();
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void Delete(string path) => File.Delete(path);
    public bool Exists(string path) => File.Exists(path);
    public void Move(string source, string destination, bool overwrite)
    {
      if (overwrite && FailOverwriteMove) throw new IOException("Injected final-move failure.");
      if (!overwrite && CreateCompetingDestinationBeforeFirstMove && !_competingDestinationCreated)
      {
        _competingDestinationCreated = true;
        File.WriteAllText(destination, "competing media");
      }
      File.Move(source, destination, overwrite);
    }
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("audiobookconverter-publish-fault-").FullName;
    public string Path { get; }
    public string GetPath(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public string CreateFile(string name, string contents) { var path = GetPath(name); File.WriteAllText(path, contents); return path; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}
