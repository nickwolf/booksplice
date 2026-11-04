using System.Windows;
using BookSplice.Core.Analysis;
using BookSplice.FFmpeg.Execution;
using BookSplice.Gui.History;
using BookSplice.Gui.Views;

namespace BookSplice.Gui.Tests;

[Collection("WPF")]
public sealed class ConversionHistoryTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("booksplice-history-").FullName;

  public void Dispose() => Directory.Delete(_root, true);

  private static ConversionAuditRecord Record(Guid id, ConversionTerminalStatus status, DateTimeOffset started, TimeSpan elapsed, string? published = null, params ServiceDiagnostic[] diagnostics)
    => new(ConversionAuditRecord.CurrentSchemaVersion, id, started, started + elapsed, status, null, null, null, null, null, [], null, null, null, null, [], diagnostics, published);

  private async Task<Guid> WriteAsync(ConversionTerminalStatus status, DateTimeOffset started, TimeSpan elapsed, string? published = null, params ServiceDiagnostic[] diagnostics)
  {
    var id = Guid.NewGuid();
    await new JsonJobLogWriter(_root).WriteAsync(Record(id, status, started, elapsed, published, diagnostics), CancellationToken.None);
    return id;
  }

  [Fact]
  public async Task ReadsRecordsWrittenByTheJobLogWriterAndKeepsOutcomesApart()
  {
    var output = Path.Combine(_root, "book.m4b");
    await File.WriteAllTextAsync(output, "audio");
    var now = DateTimeOffset.UtcNow;
    var done = await WriteAsync(ConversionTerminalStatus.Succeeded, now.AddMinutes(-3), TimeSpan.FromSeconds(75), output, new ServiceDiagnostic("validation.warning", ServiceDiagnosticSeverity.Warning, "Odd chapter title"));
    var cancelled = await WriteAsync(ConversionTerminalStatus.Cancelled, now.AddMinutes(-2), TimeSpan.FromSeconds(5));
    var failed = await WriteAsync(ConversionTerminalStatus.ExecutionFailed, now.AddMinutes(-1), TimeSpan.Zero, null, new ServiceDiagnostic("execution.failed", ServiceDiagnosticSeverity.Error, "ffmpeg exited with code 1"));

    var load = new ConversionHistoryReader(_root).Read();

    Assert.Empty(load.Skipped);
    Assert.Equal(new[] { failed, cancelled, done }, load.Entries.Select(entry => entry.JobId).ToArray());
    var completed = load.Entries[2];
    Assert.Equal(HistoryOutcome.Completed, completed.Outcome);
    Assert.Equal("Completed with warnings", completed.OutcomeText);
    Assert.Equal(["Odd chapter title"], completed.Warnings);
    Assert.Equal(TimeSpan.FromSeconds(75), completed.Elapsed);
    Assert.Equal("1m 15s", completed.ElapsedText);
    Assert.True(completed.CanOpenOutput);
    Assert.Equal(HistoryOutcome.Cancelled, load.Entries[1].Outcome);
    Assert.Equal(HistoryOutcome.Failed, load.Entries[0].Outcome);
    Assert.Contains("ffmpeg exited with code 1", load.Entries[0].Summary, StringComparison.Ordinal);
    Assert.False(load.Entries[0].CanOpenOutput);
    Assert.Equal("No output was published.", load.Entries[0].OutputText);
  }

  [Fact]
  public async Task CopiedDiagnosticsDoNotCarryOutputOrTemporaryPaths()
  {
    var output = Path.Combine(_root, "Secret Title.m4b");
    var temp = Path.Combine(_root, "temp", "job-1", "work.m4b");
    var id = Guid.NewGuid();
    await File.WriteAllTextAsync(Path.Combine(_root, $"{id:D}.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
      schemaVersion = 1,
      jobId = id,
      startedAt = DateTimeOffset.UtcNow,
      finishedAt = DateTimeOffset.UtcNow,
      terminalStatus = "PublicationFailed",
      plan = new { strategy = "DirectTranscode", validationLevel = "Full", outputPath = output },
      execution = new { temporaryOutputPath = temp, processes = new[] { new { exitCode = 1, standardError = $"could not write {temp} next to {output}" } }, diagnostics = new[] { new { code = "x", message = $"failed at {temp}" } } },
      publication = new { status = "Failed", finalPath = output, diagnostics = new[] { $"cannot replace {output}" } },
      commandEvidence = new[] { $"ffmpeg -i in.mp3 {temp}" },
      diagnostics = new[] { new { code = "publication.failed", severity = "Error", message = $"Could not publish {output}" } },
      publishedPath = output,
    }));
    var reader = new ConversionHistoryReader(_root);
    var entry = Assert.Single(reader.Read().Entries);

    var text = reader.ReadDiagnostics(entry);

    Assert.Contains("publication.failed", text, StringComparison.Ordinal);
    Assert.DoesNotContain("Secret Title", text, StringComparison.Ordinal);
    Assert.DoesNotContain("job-1", text, StringComparison.Ordinal);
    Assert.DoesNotContain(_root, text, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task MissingOutputIsReportedAndNotOpenable()
  {
    await WriteAsync(ConversionTerminalStatus.Succeeded, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), Path.Combine(_root, "gone.m4b"));

    var entry = Assert.Single(new ConversionHistoryReader(_root).Read().Entries);

    Assert.False(entry.OutputExists);
    Assert.False(entry.CanOpenOutput);
    Assert.EndsWith("(file is missing)", entry.OutputText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ReadsPlanFieldsFromTheRecordDocument()
  {
    var id = Guid.NewGuid();
    await File.WriteAllTextAsync(Path.Combine(_root, $"{id:D}.json"), $$"""
      {"schemaVersion":1,"jobId":"{{id}}","startedAt":"2026-10-01T10:00:00+00:00","finishedAt":"2026-10-01T10:02:00+00:00","terminalStatus":"Succeeded",
       "plan":{"strategy":"AacStreamCopy","validationLevel":"Full"},"validation":{"warnings":["Late warning"]},"diagnostics":[{"code":"validation.warning","severity":"Warning","message":"Late warning"}]}
      """);

    var entry = Assert.Single(new ConversionHistoryReader(_root).Read().Entries);

    Assert.Equal("AacStreamCopy", entry.Strategy);
    Assert.Equal("Full", entry.ValidationLevel);
    Assert.Equal(["Late warning"], entry.Warnings);
    Assert.True(entry.Matches("streamcopy"));
    Assert.True(entry.Matches("late FULL"));
    Assert.False(entry.Matches("cancelled"));
  }

  [Fact]
  public async Task BadRecordsAreSkippedWithoutBlockingTheRest()
  {
    var good = await WriteAsync(ConversionTerminalStatus.Succeeded, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    var newer = Guid.NewGuid();
    await File.WriteAllTextAsync(Path.Combine(_root, $"{newer:D}.json"), $$"""{"schemaVersion":2,"jobId":"{{newer}}"}""");
    await File.WriteAllTextAsync(Path.Combine(_root, $"{Guid.NewGuid():D}.json"), "{ not json");
    await File.WriteAllTextAsync(Path.Combine(_root, $"{Guid.NewGuid():D}.json"), "[]");
    await File.WriteAllTextAsync(Path.Combine(_root, $"{Guid.NewGuid():D}.json"), """{"schemaVersion":1,"jobId":"x","startedAt":5}""");
    await File.WriteAllBytesAsync(Path.Combine(_root, $"{Guid.NewGuid():D}.json"), []);
    var stale = Path.Combine(_root, $".{Guid.NewGuid():D}.abc.partial");
    await File.WriteAllTextAsync(stale, "{");
    File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
    await File.WriteAllTextAsync(Path.Combine(_root, $".{Guid.NewGuid():D}.live.partial"), "{");

    var load = new ConversionHistoryReader(_root).Read();

    Assert.Equal(good, Assert.Single(load.Entries).JobId);
    Assert.Equal(6, load.Skipped.Count);
    Assert.Contains(load.Skipped, value => value.Reason.Contains("schema version 2", StringComparison.Ordinal));
  }

  [Fact]
  public async Task MissingFolderIsEmptyHistoryAndOldRecordsPastTheCapAreCounted()
  {
    Assert.Empty(new ConversionHistoryReader(Path.Combine(_root, "none")).Read().Entries);
    for (var index = 0; index < 4; index++) await WriteAsync(ConversionTerminalStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-index), TimeSpan.Zero);

    var load = new ConversionHistoryReader(_root, 3).Read();

    Assert.Equal(3, load.Entries.Count);
    Assert.Equal(1, load.NotLoaded);
  }

  [Fact]
  public async Task ClearingRemovesRecordsOnlyAndNeverTheOutput()
  {
    var output = Path.Combine(_root, "book.m4b");
    await File.WriteAllTextAsync(output, "audio");
    var other = Path.Combine(_root, "notes.txt");
    await File.WriteAllTextAsync(other, "keep");
    await WriteAsync(ConversionTerminalStatus.Succeeded, DateTimeOffset.UtcNow, TimeSpan.Zero, output);
    await File.WriteAllTextAsync(Path.Combine(_root, $"{Guid.NewGuid():D}.json"), "{ corrupt");
    var reader = new ConversionHistoryReader(_root);

    Assert.Equal(1, reader.Delete(reader.Read().Entries));
    Assert.Equal(1, reader.DeleteAll());

    Assert.True(File.Exists(output));
    Assert.True(File.Exists(other));
    Assert.Empty(reader.Read().Skipped);
  }

  [Fact]
  public void ViewModelFiltersAndLoadsOffTheCallingThread() => DispatcherTestHost.Run(async _ =>
  {
    await WriteAsync(ConversionTerminalStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.Zero);
    await WriteAsync(ConversionTerminalStatus.Cancelled, DateTimeOffset.UtcNow, TimeSpan.Zero);
    using var model = new ConversionHistoryViewModel(new ConversionHistoryReader(_root));

    await model.LoadAsync();
    Assert.Equal(2, model.Entries.Count);
    model.SearchText = "cancelled";
    Assert.Equal(HistoryOutcome.Cancelled, Assert.Single(model.Entries).Outcome);
    model.Selected = model.Entries[0];
    await model.ClearAsync();
    Assert.Empty(model.Entries);
    Assert.Null(model.Selected);
  });

  [Fact]
  public void HistoryPageNamesTheSearchAndPutsTheRecordActionsOnEachRow() => DispatcherTestHost.Run(_ =>
  {
    var page = new HistoryPage(new ConversionHistoryViewModel(new ConversionHistoryReader(_root)));
    page.Measure(new Size(640, 460));
    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(Assert.IsType<System.Windows.Controls.TextBox>(page.FindName("Search")))));
    var records = Assert.IsType<System.Windows.Controls.ListView>(page.FindName("Records"));
    var actions = Assert.IsType<System.Windows.Controls.GridView>(records.View).Columns.First(column => (string)column.Header == "Actions");
    var row = (System.Windows.Controls.StackPanel)actions.CellTemplate.LoadContent();
    var buttons = row.Children.OfType<BookSplice.Gui.Controls.IconButton>().ToArray();
    Assert.Equal(["Open output folder", "Copy diagnostics", "Delete record"], buttons.Select(button => System.Windows.Automation.AutomationProperties.GetName(button)));
    Assert.Equal(["Open output folder", "Copy diagnostics", "Delete record"], buttons.Select(button => button.ToolTip));
    Assert.All(buttons, button => Assert.False(string.IsNullOrEmpty(button.Glyph)));
    Assert.DoesNotContain(Descendants(page), element => element is System.Windows.Controls.Button { Content: string text } && (text.Contains("output folder", StringComparison.OrdinalIgnoreCase) || text.Contains("diagnostics", StringComparison.OrdinalIgnoreCase) || text.Contains("record", StringComparison.OrdinalIgnoreCase)));
    return Task.CompletedTask;
  });

  private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
  {
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
    {
      yield return child;
      foreach (var descendant in Descendants(child)) yield return descendant;
    }
  }
}
