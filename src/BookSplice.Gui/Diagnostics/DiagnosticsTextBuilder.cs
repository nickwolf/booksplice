using System.Globalization;
using System.Text;
using BookSplice.Core.Analysis;
using BookSplice.Gui.ViewModels;

namespace BookSplice.Gui.Diagnostics;

public static class DiagnosticsTextBuilder
{
  public static string Build(BookQueueItemViewModel item)
  {
    ArgumentNullException.ThrowIfNull(item);
    var text = new StringBuilder().AppendLine("BookSplice diagnostics");
    text.Append("Status: ").AppendLine(item.Status);
    text.Append("Progress: ").Append(item.Percent.ToString("0.#", CultureInfo.InvariantCulture)).AppendLine("%");
    text.Append("Files: ").AppendLine((item.Analysis?.OrderedFiles.Count ?? 0).ToString(CultureInfo.InvariantCulture));
    text.Append("Order: ").AppendLine(item.Analysis?.OrderResolution?.Status.ToString() ?? "Unavailable");
    text.Append("Quality: ").AppendLine(item.Settings.QualityProfileId);
    text.Append("Channels: ").AppendLine(item.Settings.ChannelPolicy.ToString());
    text.Append("Validation: ").AppendLine(item.Settings.ValidationLevel.ToString());
    text.Append("Metadata profile: ").AppendLine(item.Settings.MetadataProfileId);
    text.Append("Create chapters: ").AppendLine(item.Settings.CreateChapters.ToString(CultureInfo.InvariantCulture));

    if (!string.IsNullOrWhiteSpace(item.Details))
    {
      var paths = item.Analysis?.OrderedFiles.Select(file => file.FullPath).ToList() ?? [];
      paths.AddRange([item.Source, item.PublishedPath ?? "", item.Settings.OutputDirectory]);
      text.AppendLine("Details:").AppendLine(PublicTextRedactor.Sanitize(item.Details, paths));
    }

    return text.ToString().TrimEnd();
  }
}
