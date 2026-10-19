using BookSplice.Updates.Windows;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Handover;
using System.IO.Pipes;
using System.Globalization;
using System.Buffers.Binary;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--update-self-check")
{
  var isolatedData = Environment.GetEnvironmentVariable("BOOKSPLICE_LOCAL_APP_DATA")
    ?? throw new InvalidDataException("Isolated data root was not supplied.");
  var settings = Path.Combine(isolatedData, "BookSplice", "settings.json");
  if (!File.Exists(settings)) return 2;
  using var document = JsonDocument.Parse(File.ReadAllBytes(settings));
  if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1) return 2;
  var checks = SelfCheckReport.RequiredChecks.ToDictionary(name => name,
    _ => new SelfCheckEntry(true, null), StringComparer.Ordinal);
  var report = JsonSerializer.SerializeToUtf8Bytes(new SelfCheckReport(1, "BookSplice", args[2],
    "win-x64", 1, checks));
  using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
    PipeOptions.Asynchronous);
  await pipe.ConnectAsync();
  var acknowledgement = new byte[1];
  await pipe.ReadExactlyAsync(acknowledgement);
  if (acknowledgement[0] != 0xA5) return 2;
  var prefix = new byte[4];
  BinaryPrimitives.WriteInt32LittleEndian(prefix, report.Length);
  await pipe.WriteAsync(prefix);
  await pipe.WriteAsync(report);
  await pipe.FlushAsync();
  return 0;
}

if (args.Length == 4 && args[0] == "selfcheck-report")
{
  using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
    PipeOptions.Asynchronous);
  await pipe.ConnectAsync();
  var acknowledgement = new byte[1];
  await pipe.ReadExactlyAsync(acknowledgement);
  if (acknowledgement[0] != 0xA5) return 2;
  var report = Convert.FromBase64String(args[2]);
  var prefix = new byte[4];
  BinaryPrimitives.WriteInt32LittleEndian(prefix, report.Length);
  await pipe.WriteAsync(prefix);
  await pipe.WriteAsync(report);
  await pipe.FlushAsync();
  return int.Parse(args[3], CultureInfo.InvariantCulture);
}

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

if (args.Length == 2 && args[0] == "write-media-env")
{
  File.WriteAllText(args[1],
    (Environment.GetEnvironmentVariable("BOOKSPLICE_FFMPEG_DIR") ?? "<missing>") + "|" +
    (Environment.GetEnvironmentVariable("FFMPEG_DATADIR") ?? "<missing>"));
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
