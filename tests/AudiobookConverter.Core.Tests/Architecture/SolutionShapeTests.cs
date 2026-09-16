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

  [Fact]
  public void SharedExecutionPortAndDtosBelongToCore()
  {
    var coreAssembly = typeof(AudiobookConverter.Core.AssemblyMarker).Assembly;

    Assert.Equal(coreAssembly, typeof(AudiobookConverter.Core.Execution.IConversionExecutor).Assembly);
    Assert.Equal(coreAssembly, typeof(AudiobookConverter.Core.Execution.ConversionExecutionResult).Assembly);
    Assert.Equal(coreAssembly, typeof(AudiobookConverter.Core.Execution.ConversionProgress).Assembly);
  }
}
