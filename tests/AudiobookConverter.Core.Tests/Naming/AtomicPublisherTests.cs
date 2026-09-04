using AudiobookConverter.Core.Naming;
using AudiobookConverter.Core.Validation;

namespace AudiobookConverter.Core.Tests.Naming;

public sealed class AtomicPublisherTests
{
  [Fact]
  public async Task PublishAsync_rejects_a_failed_report_without_creating_a_destination()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var final = root.GetPath("staging", "Book.m4b");

    var result = await new AtomicPublisher().PublishAsync(Plan(final), ValidationReport.Failed(temporary, [new ValidationCheck("audio.primary", false, "Audio is missing.")]), CancellationToken.None);

    Assert.Equal(PublicationStatus.Rejected, result.Status);
    Assert.False(Directory.Exists(Path.GetDirectoryName(final)!));
    Assert.True(File.Exists(temporary));
  }

  [Fact]
  public async Task PublishAsync_rejects_a_report_for_a_different_temporary_file()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var result = await new AtomicPublisher().PublishAsync(Plan(root.GetPath("staging", "Book.m4b")), ValidationReport.Passed(root.GetPath("other.m4b"), []), CancellationToken.None);

    Assert.Equal(PublicationStatus.Rejected, result.Status);
    Assert.True(File.Exists(temporary));
    Assert.False(Directory.Exists(root.GetPath("staging")));
  }

  [Fact]
  public async Task PublishAsync_copies_to_a_unique_partial_and_only_then_exposes_the_final_name()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var final = root.GetPath("staging", "Book.m4b");

    var result = await new AtomicPublisher().PublishAsync(Plan(final), ValidationReport.Passed(temporary, []), CancellationToken.None);

    Assert.Equal(PublicationStatus.Published, result.Status);
    Assert.Equal(Path.GetFullPath(final), result.FinalPath);
    Assert.Equal("validated media", await File.ReadAllTextAsync(final));
    Assert.False(File.Exists(temporary));
    Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(final)!, "*.partial", SearchOption.TopDirectoryOnly));
  }

  [Fact]
  public async Task PublishAsync_avoids_existing_names_with_the_same_suffixes_as_the_planner()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "new media");
    var staging = Directory.CreateDirectory(root.GetPath("staging")).FullName;
    await File.WriteAllTextAsync(Path.Combine(staging, "Book.m4b"), "first");
    await File.WriteAllTextAsync(Path.Combine(staging, "Book (2).m4b"), "second");

    var result = await new AtomicPublisher().PublishAsync(Plan(Path.Combine(staging, "Book.m4b")), ValidationReport.Passed(temporary, []), CancellationToken.None);

    Assert.Equal(Path.Combine(staging, "Book (3).m4b"), result.FinalPath);
    Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(staging, "Book.m4b")));
    Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(staging, "Book (2).m4b")));
  }

  [Fact]
  public async Task PublishAsync_overwrites_only_after_the_validated_partial_is_ready()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "new media");
    var final = root.GetPath("staging", "Book.m4b");
    Directory.CreateDirectory(Path.GetDirectoryName(final)!);
    await File.WriteAllTextAsync(final, "old media");

    var result = await new AtomicPublisher().PublishAsync(Plan(final, CollisionPolicy.Overwrite), ValidationReport.Passed(temporary, []), CancellationToken.None);

    Assert.Equal(PublicationStatus.Published, result.Status);
    Assert.Equal("new media", await File.ReadAllTextAsync(final));
  }

  [Fact]
  public async Task PublishAsync_preserves_the_temporary_output_when_cancelled_before_copying()
  {
    using var root = new TemporaryDirectory();
    var temporary = root.CreateFile("job.m4b", "validated media");
    var final = root.GetPath("staging", "Book.m4b");

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AtomicPublisher().PublishAsync(Plan(final), ValidationReport.Passed(temporary, []), new CancellationToken(true)));

    Assert.True(File.Exists(temporary));
    Assert.False(File.Exists(final));
  }

  private static AudiobookConverter.Core.Planning.ConversionPlan Plan(string outputPath, CollisionPolicy policy = CollisionPolicy.AvoidCollision)
    => new([], new AudiobookConverter.Core.Metadata.BookMetadata(new Dictionary<AudiobookConverter.Core.Metadata.SemanticField, AudiobookConverter.Core.Metadata.AggregatedValue>(), new Dictionary<string, string>(), new Dictionary<AudiobookConverter.Core.Metadata.SemanticField, IReadOnlyList<string>>()), null, [], AudiobookConverter.Core.Planning.QualityProfileCatalog.Version1[0], AudiobookConverter.Core.Settings.ValidationLevel.Lightweight, policy, outputPath, AudiobookConverter.Core.Planning.AudioStrategy.DirectTranscode, [], new AudiobookConverter.Core.Planning.SpaceEstimate(1, 1, 1, 1, 0), 1, "test", "GenericMp4");

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("audiobookconverter-publish-").FullName;
    public string Path { get; }
    public string GetPath(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public string CreateFile(string name, string contents) { var path = GetPath(name); File.WriteAllText(path, contents); return path; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
  }
}
