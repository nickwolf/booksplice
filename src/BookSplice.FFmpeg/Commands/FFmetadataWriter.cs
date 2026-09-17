using System.Text;

namespace BookSplice.FFmpeg.Commands;

public sealed class FFmetadataWriter
{
  private readonly StringComparer _keyComparer = StringComparer.Ordinal;

  public string Write(IReadOnlyDictionary<string, string> tags)
  {
    ArgumentNullException.ThrowIfNull(tags);
    var output = new StringBuilder(";FFMETADATA1\n");
    foreach (var tag in tags.OrderBy(tag => tag.Key, _keyComparer))
    {
      output.Append(Escape(tag.Key));
      output.Append('=');
      output.Append(Escape(tag.Value));
      output.Append('\n');
    }

    return output.ToString();
  }

  private static string Escape(string value)
  {
    var output = new StringBuilder(value.Length);
    for (var index = 0; index < value.Length; index++)
    {
      var character = value[index];
      if (character == '\r')
      {
        if (index + 1 < value.Length && value[index + 1] == '\n') index++;
        output.Append("\\\n");
      }
      else
      {
        if (character is '\\' or '=' or ';' or '#' or '\n') output.Append('\\');
        output.Append(character);
      }
    }

    return output.ToString();
  }
}
