namespace BookSplice.Core.Tests.Architecture;

public sealed class SolutionShapeTests
{
  [Fact]
  public void Core_does_not_reference_presentation_or_process_projects()
  {
    var references = typeof(BookSplice.Core.AssemblyMarker)
        .Assembly.GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    Assert.DoesNotContain("BookSplice.Gui", references);
    Assert.DoesNotContain("BookSplice.Cli", references);
    Assert.DoesNotContain("BookSplice.FFmpeg", references);
  }

  [Fact]
  public void SharedExecutionPortAndDtosBelongToCore()
  {
    var coreAssembly = typeof(BookSplice.Core.AssemblyMarker).Assembly;

    Assert.Equal(coreAssembly, typeof(BookSplice.Core.Execution.IConversionExecutor).Assembly);
    Assert.Equal(coreAssembly, typeof(BookSplice.Core.Execution.ConversionExecutionResult).Assembly);
    Assert.Equal(coreAssembly, typeof(BookSplice.Core.Execution.ConversionProgress).Assembly);
  }
}
