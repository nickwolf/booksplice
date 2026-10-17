using BookSplice.Updates.Windows;

var root = args[0];
IDisposable? lease = args[1] switch
{
  "job" => new ActivityLeaseStore(root).TryEnterJob(),
  "update" => new ActivityLeaseStore(root).TryEnterUpdate(),
  "gui" => GuiInstanceLease.TryAcquire(root),
  _ => throw new ArgumentException("Unknown lease kind."),
};

if (lease is null)
{
  Console.WriteLine("busy");
  return 2;
}

using (lease)
{
  Console.WriteLine("ready");
  Console.Out.Flush();
  Console.ReadLine();
}

return 0;
