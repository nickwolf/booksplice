using BookSplice.Updates.Windows;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Handover;
using System.IO.Pipes;
using System.Globalization;

if (args.Length == 0)
{
  await Task.Delay(TimeSpan.FromSeconds(30));
  return 0;
}

if (args.Length > 0 && args[0] == "hold")
{
  await Task.Delay(TimeSpan.FromSeconds(30));
  return 0;
}

if (args.Length == 2 && args[0] == "write-data-env")
{
  File.WriteAllText(args[1], Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA") ??
    "<missing>");
  return 0;
}

if (args.Length > 0 && args[0] == "pipe-ready")
{
  using var coordinator = SupervisedProcess.CaptureExpected(new ProcessIdentity(
    int.Parse(args[3], CultureInfo.InvariantCulture),
    long.Parse(args[4], CultureInfo.InvariantCulture), args[5]),
    new FileIdentity(ulong.Parse(args[7], CultureInfo.InvariantCulture), args[8]), args[9]);
  using var self = SupervisedProcess.CaptureCurrent();
  using var session = await UpdatePipe.ConnectAsync(args[1], coordinator, Guid.Parse(args[2]),
    Convert.FromHexString(args[6]), self, CancellationToken.None);
  await session.SendAsync(UpdateMessageKind.Ready);
  Console.WriteLine("sent");
  Console.Out.Flush();
  await Task.Delay(TimeSpan.FromSeconds(30));
  return 0;
}

if (args.Length > 0 && args[0] == "pipe-oversize")
{
  using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
    PipeOptions.Asynchronous);
  await pipe.ConnectAsync();
  await pipe.WriteAsync(BitConverter.GetBytes(4097));
  await pipe.FlushAsync();
  Console.WriteLine("sent");
  Console.Out.Flush();
  await Task.Delay(TimeSpan.FromSeconds(30));
  return 0;
}

if (args.Length > 1 && args[1] == "state-increment")
{
  var identity = new FileIdentity(ulong.Parse(args[2], CultureInfo.InvariantCulture), args[3]);
  var installationId = Guid.Parse(args[4]);
  var owned = new OwnedDirectory(args[0], identity, installationId, Guid.Parse(args[5]), "managed");
  var store = new AtomicStateStore(owned,
    [new StateRecordDefinition<CounterState>("counter.json", value =>
      value.SchemaVersion == 1 && value.Count >= 0)]);
  var path = Path.Combine(args[0], "counter.json");
  for (var index = 0; index < int.Parse(args[6], CultureInfo.InvariantCulture); index++)
    store.Transition<CounterState>(path, current => new CounterState(1, current.Value!.Count + 1));
  return 0;
}

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

internal sealed record CounterState(int SchemaVersion, int Count);
