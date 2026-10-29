using System.Diagnostics;
using BookSplice.Core.Settings;

namespace BookSplice.Gui.Notifications;

// Collects terminal results and shows one summary. Delivery problems are logged and swallowed so they never reach a conversion.
public sealed class BatchNotifier(IConversionNotifier notifier, AppSettings settings)
{
  private readonly List<BookFinished> _finished = [];

  public void Add(BookFinished book) { lock (_finished) _finished.Add(book); }

  public void Complete()
  {
    BookFinished[] books;
    lock (_finished) books = [.. _finished];
    Deliver(notifier, NotificationPolicy.ForBatch(books, settings));
  }

  public static void Deliver(IConversionNotifier notifier, NotificationRequest? request)
  {
    if (request is null) return;
    try { notifier.Show(request); }
    catch (Exception error) { Trace.TraceWarning($"Notification was not shown: {error.GetType().Name}: {error.Message}"); }
  }
}
