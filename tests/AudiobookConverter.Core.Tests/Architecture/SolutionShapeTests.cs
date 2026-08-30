namespace AudiobookConverter.Core.Tests.Architecture;

public sealed class SolutionShapeTests
{
  [Fact]
  public void Core_does_not_reference_presentation_or_process_projects()
  {
    var references = typeof(AudiobookConverter.Core.AssemblyMarker)
        .Assembly.GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    Assert.DoesNotContain("AudiobookConverter.Gui", references);
    Assert.DoesNotContain("AudiobookConverter.Cli", references);
    Assert.DoesNotContain("AudiobookConverter.FFmpeg", references);
  }
}
