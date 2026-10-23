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

  // Test-only settings (release directory, programs directory, faults): null in Release builds.
  public static string? TestSetting(string name) =>
    TestVariable(Flavor, name, Environment.GetEnvironmentVariable);

  // Test builds let the launcher chain inherit BOOKSPLICE_TEST_* settings; Release builds never do.
  internal static bool PassesToChildren(BuildFlavor flavor, string name) =>
    name.StartsWith("BOOKSPLICE_TEST_", StringComparison.OrdinalIgnoreCase) &&
    TestVariable(flavor, name, _ => "1") is not null;

  internal static string ProgramsDirectory(BuildFlavor flavor, Func<string, string?> environment) =>
    TestVariable(flavor, "BOOKSPLICE_TEST_PROGRAMS_DIR", environment) is { Length: > 0 } redirected ?
      redirected : Environment.GetFolderPath(Environment.SpecialFolder.Programs);

  internal static string ProgramsDirectory() => ProgramsDirectory(Flavor, Environment.GetEnvironmentVariable);
}
