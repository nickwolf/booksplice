using BookSplice.Core.Settings;

namespace BookSplice.Gui.Notifications;

public enum BookOutcome { Success, Warning, Failure, Cancelled }

// Title is the book name. ResultKey identifies the queue item so a click can find its result later.
public sealed record BookFinished(string ResultKey, string Title, BookOutcome Outcome, string? OutputPath);

public sealed record NotificationRequest(string Title, string Body, string? ResultKey);

public interface IConversionNotifier
{
  void Show(NotificationRequest request);
}

public static class NotificationPolicy
{
  public static NotificationRequest? ForBook(BookFinished book, AppSettings settings)
  {
    if (!settings.NotificationsEnabled) return null;
    var verb = Verb(book.Outcome);
    if (settings.NotificationsHideDetails) return new($"Conversion {verb}", "Open BookSplice to see the result.", book.ResultKey);
    return new($"Conversion {verb}", book.Outcome is BookOutcome.Success or BookOutcome.Warning && book.OutputPath is { Length: > 0 } path
      ? $"{book.Title}\nSaved to {path}" : book.Title, book.ResultKey);
  }

  // A batch of one gets the per-book wording so a single book never reads as "1 books".
  public static NotificationRequest? ForBatch(IReadOnlyList<BookFinished> books, AppSettings settings)
  {
    if (!settings.NotificationsEnabled || books.Count == 0) return null;
    if (books.Count == 1) return ForBook(books[0], settings);
    var counts = new[] { BookOutcome.Success, BookOutcome.Warning, BookOutcome.Failure, BookOutcome.Cancelled }
      .Select(outcome => (outcome, count: books.Count(book => book.Outcome == outcome))).Where(pair => pair.count > 0)
      .Select(pair => $"{pair.count} {Verb(pair.outcome)}").ToArray();
    var problems = books.Any(book => book.Outcome is BookOutcome.Failure or BookOutcome.Warning);
    var summary = string.Join(", ", counts);
    var body = settings.NotificationsHideDetails || !problems ? summary
      : summary + "\n" + string.Join(", ", books.Where(book => book.Outcome is BookOutcome.Failure or BookOutcome.Warning).Select(book => book.Title));
    return new($"{books.Count} conversions finished", body, null);
  }

  private static string Verb(BookOutcome outcome) => outcome switch
  {
    BookOutcome.Success => "succeeded",
    BookOutcome.Warning => "finished with warnings",
    BookOutcome.Failure => "failed",
    _ => "cancelled",
  };
}
