using System.Reflection;

namespace BookSplice.Updates;

public enum BuildFlavor
{
  Test,
  Release,
}

public static class UpdateBuild
{
  public static BuildFlavor Flavor { get; } = Parse(typeof(UpdateBuild).Assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "BookSplice.BuildFlavor")?.Value);

  internal static BuildFlavor Parse(string? value) => value switch
  {
    "Release" => BuildFlavor.Release,
    "Test" => BuildFlavor.Test,
    _ => throw new InvalidOperationException("Build flavor metadata is missing or invalid."),
  };

  // The only way to read a test endpoint, key or fault variable: Release builds never see one.
  internal static string? TestVariable(BuildFlavor flavor, string name, Func<string, string?> environment) =>
    flavor == BuildFlavor.Release ? null : environment(name);
}
