namespace BookSplice.Updates.Policy;

public static class CheckSchedule
{
  public static DateTimeOffset NextDue(DateTimeOffset now, DateTimeOffset? lastSuccess, DateTimeOffset? retryAfter)
  {
    var earliest = now.AddSeconds(60);
    var due = lastSuccess is null || lastSuccess > now ? earliest : lastSuccess.Value.AddHours(24);
    if (due <= now)
    {
      due = earliest;
    }
    return retryAfter > due ? retryAfter.Value : due;
  }

  public static DateTimeOffset NextFailureDue(DateTimeOffset now, DateTimeOffset? retryAfter)
  {
    var due = now.AddHours(1);
    return retryAfter > due ? retryAfter.Value : due;
  }
}
