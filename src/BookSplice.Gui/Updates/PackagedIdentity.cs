using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BookSplice.Updates.Handover;

namespace BookSplice.Gui.Updates;

public static class PackagedIdentity
{
  public static string? Version => typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .SingleOrDefault(x => x.Key == "BookSplicePackageVersion")?.Value;

  public static bool Matches(string expectedVersion)
  {
    if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
        !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Version) ||
        !string.Equals(Version, expectedVersion, StringComparison.Ordinal)) return false;
    var versionFile = Path.Combine(AppContext.BaseDirectory, "VERSION.txt");
    if (!File.Exists(versionFile)) return false;
    var firstLine = File.ReadLines(versionFile).FirstOrDefault();
    return firstLine == "BookSplice " + expectedVersion &&
      UpdateProtocol.ProtocolVersion == 1;
  }
}
