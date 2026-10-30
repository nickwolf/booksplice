using System.Text;
using BookSplice.Core.Metadata;

namespace BookSplice.Cli;

public static class CliHelp
{
  public static string Build(MetadataProfileCatalog catalog, string? profilesDirectory = null)
  {
    var text = new StringBuilder("""
      booksplice <source> [options]
        --output <folder>                 Existing output folder (or saved setting)
        --quality <profile>               efficient, balanced, high-quality, preserve-more
        --bitrate <32-320>                 Custom AAC bitrate in kbps
        --jobs <1-32>                      Parallel segment encoders
        --order <natural|metadata>        Explicit source ordering candidate
        --metadata-profile <name>          Metadata profile ID (see below)
        --validation <lightweight|full>    Output validation level
        --channels <preserve|mono|stereo>   Output channel handling
        --output-template <template>       Folders and file name, e.g. {author}\{series}\{title}
        --chapters | --no-chapters         Chapter creation
        --preset <name>                    Start from a saved preset; other options still win
        --list-presets                     List saved presets and whether each is usable
        --overwrite                       Replace an existing output after validation
        --dry-run                         Plan without writing output or audit files
        --json                            Newline-delimited JSON; never prompts
      Terminal runs prompt when track order needs a decision. Redirected input never prompts.
      Use the BookSplice GUI Settings window to save defaults.

      """);
    text.AppendLine(FormattableString.Invariant($"Metadata profiles: {string.Join(", ", catalog.Profiles.Select(profile => profile.Name))}"));
    if (profilesDirectory is not null) text.AppendLine(FormattableString.Invariant($"Profiles folder: {profilesDirectory}"));
    foreach (var error in catalog.Errors) text.AppendLine(FormattableString.Invariant($"  Skipped {error.FileName}: {error.Reason}"));
    return text.ToString().TrimEnd('\r', '\n').Replace("\r\n", "\n");
  }
}
