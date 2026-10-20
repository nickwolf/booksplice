using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

public sealed record OldHostRpcTicket(int ProtocolVersion, string PipeName,
  Guid TransactionId, string Nonce,
  ProcessIdentity OldGui, FileIdentity OldGuiImage, string OldGuiSha256)
{
  private static readonly JsonSerializerOptions StrictJson = new()
  {
    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true
  };

  public string Encode()
  {
    Validate();
    return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
  }

  public static OldHostRpcTicket Parse(string encoded)
  {
    if (encoded.Length is 0 or > 4096) throw new InvalidDataException("Old-host ticket size is invalid.");
    var bytes = Convert.FromBase64String(encoded);
    if (bytes.Length > 3072) throw new InvalidDataException("Old-host ticket size is invalid.");
    _ = new UTF8Encoding(false, true).GetString(bytes);
    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    RejectDuplicates(document.RootElement);
    if (document.RootElement.ValueKind != JsonValueKind.Object ||
        document.RootElement.EnumerateObject().Count() != 7 ||
        document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 7)
      throw new InvalidDataException("Old-host ticket shape is invalid.");
    var ticket = document.RootElement.Deserialize<OldHostRpcTicket>(StrictJson) ??
      throw new InvalidDataException("Old-host ticket is missing.");
    ticket.Validate();
    return ticket;
  }

  public void Validate()
  {
    if (ProtocolVersion != UpdateProtocol.ProtocolVersion || PipeName is not { Length: 66 } ||
        !PipeName.StartsWith("booksplice-update-", StringComparison.Ordinal) ||
        !PipeName[18..].All(Uri.IsHexDigit) || TransactionId == Guid.Empty ||
        Nonce is not { Length: 44 } || Convert.FromBase64String(Nonce).Length != 32 ||
        OldGui is null || OldGui.Pid <= 0 || OldGui.StartTimeUtcTicks <= 0 ||
        !Path.IsPathFullyQualified(OldGui.ExecutablePath) || !OldGuiImage.IsValid ||
        OldGuiSha256 is not { Length: 64 } || !OldGuiSha256.All(Uri.IsHexDigit))
      throw new InvalidDataException("Old-host ticket identity is invalid.");
  }

  private static void RejectDuplicates(JsonElement value)
  {
    if (value.ValueKind != JsonValueKind.Object) return;
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var field in value.EnumerateObject())
    {
      if (!names.Add(field.Name)) throw new InvalidDataException("Old-host ticket has duplicate fields.");
      RejectDuplicates(field.Value);
    }
  }
}

[SupportedOSPlatform("windows")]
public sealed class OldHostRpcServer : IDisposable
{
  private readonly NamedPipeServerStream _pipe;
  private readonly SupervisedProcess _local;
  private readonly byte[] _nonce;
  private bool _used;

  public OldHostRpcServer(SupervisedProcess local, Guid transactionId)
  {
    _local = local ?? throw new ArgumentNullException(nameof(local));
    if (transactionId == Guid.Empty) throw new ArgumentException("Transaction ID is empty.");
    _nonce = UpdatePipe.NewNonce();
    var pipeName = UpdatePipe.NewPipeName();
    _pipe = UpdatePipe.CreateServer(pipeName);
    Ticket = new OldHostRpcTicket(UpdateProtocol.ProtocolVersion, pipeName, transactionId,
      Convert.ToBase64String(_nonce), local.Identity, local.ImageIdentity,
      local.ImageSha256);
  }

  public OldHostRpcTicket Ticket { get; }

  public async Task ServeAsync(SupervisedProcess expectedCoordinator,
    IOldApplicationHost host, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(expectedCoordinator);
    ArgumentNullException.ThrowIfNull(host);
    if (_used) throw new InvalidOperationException("Old-host RPC accepts one coordinator.");
    _used = true;
    await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
    expectedCoordinator.VerifyPeer(UpdatePipe.ClientPid(_pipe.SafePipeHandle));
    var sequence = 0;
    while (true)
    {
      expectedCoordinator.VerifyPeer(UpdatePipe.ClientPid(_pipe.SafePipeHandle));
      var request = await OldHostWire.ReadAsync(_pipe, Ticket.TransactionId,
        _nonce, expectedCoordinator.Identity, _local.Identity, ++sequence,
        cancellationToken).ConfigureAwait(false);
      var result = request switch
      {
        "Freeze" => await host.FreezeAsync(cancellationToken).ConfigureAwait(false),
        "Hide" => await host.HideAndReleaseGuiAsync(cancellationToken).ConfigureAwait(false),
        "Restore" => await host.RestoreAndAcquireGuiAsync(cancellationToken).ConfigureAwait(false),
        "Exit" => true,
        _ => throw new InvalidDataException("Old-host request is invalid.")
      };
      expectedCoordinator.VerifyPeer(UpdatePipe.ClientPid(_pipe.SafePipeHandle));
      await OldHostWire.WriteAsync(_pipe, Ticket.TransactionId, _nonce,
        _local.Identity, expectedCoordinator.Identity, sequence,
        result ? "Accepted" : "Refused", cancellationToken).ConfigureAwait(false);
      if (request == "Exit")
      {
        await host.ExitAsync(cancellationToken).ConfigureAwait(false);
        return;
      }
    }
  }

  public void Dispose()
  {
    _pipe.Dispose();
    CryptographicOperations.ZeroMemory(_nonce);
  }
}

[SupportedOSPlatform("windows")]
public sealed class OldHostRpcClient : IOldApplicationHost, IDisposable
{
  private readonly NamedPipeClientStream _pipe;
  private readonly SupervisedProcess _old;
  private readonly SupervisedProcess _local;
  private readonly byte[] _nonce;
  private readonly Guid _transactionId;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private int _sequence;
  private bool _exiting;

  private OldHostRpcClient(NamedPipeClientStream pipe, SupervisedProcess old,
    SupervisedProcess local, OldHostRpcTicket ticket, byte[] nonce)
  {
    _pipe = pipe;
    _old = old;
    _local = local;
    _transactionId = ticket.TransactionId;
    _nonce = nonce;
  }

  public SupervisedProcess OldProcess => _old;

  public static async Task<OldHostRpcClient> ConnectAsync(OldHostRpcTicket ticket,
    SupervisedProcess expectedOld, SupervisedProcess local,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(ticket);
    ArgumentNullException.ThrowIfNull(expectedOld);
    ArgumentNullException.ThrowIfNull(local);
    ticket.Validate();
    if (ticket.OldGui != expectedOld.Identity ||
        ticket.OldGuiImage != expectedOld.ImageIdentity ||
        !string.Equals(ticket.OldGuiSha256, expectedOld.ImageSha256,
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The old-host ticket differs from the retained old GUI.");
    var nonce = Convert.FromBase64String(ticket.Nonce);
    NamedPipeClientStream? pipe = null;
    try
    {
      pipe = new NamedPipeClientStream(".", ticket.PipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous);
      await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
      expectedOld.VerifyPeer(UpdatePipe.ServerPid(pipe.SafePipeHandle));
      return new OldHostRpcClient(pipe, expectedOld, local, ticket, nonce);
    }
    catch
    {
      pipe?.Dispose();
      CryptographicOperations.ZeroMemory(nonce);
      throw;
    }
  }

  public Task<bool> FreezeAsync(CancellationToken cancellationToken = default) =>
    CallAsync("Freeze", cancellationToken);
  public Task<bool> HideAndReleaseGuiAsync(CancellationToken cancellationToken = default) =>
    CallAsync("Hide", cancellationToken);
  public Task<bool> RestoreAndAcquireGuiAsync(CancellationToken cancellationToken = default) =>
    CallAsync("Restore", cancellationToken);

  public async Task ExitAsync(CancellationToken cancellationToken = default)
  {
    if (!await CallAsync("Exit", cancellationToken).ConfigureAwait(false))
      throw new IOException("Old GUI refused the exit request.");
  }

  private async Task<bool> CallAsync(string method, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      if (_exiting) throw new IOException("Old GUI exit was already requested.");
      _old.VerifyPeer(UpdatePipe.ServerPid(_pipe.SafePipeHandle));
      var sequence = ++_sequence;
      await OldHostWire.WriteAsync(_pipe, _transactionId, _nonce, _local.Identity,
        _old.Identity, sequence, method, cancellationToken).ConfigureAwait(false);
      _old.VerifyPeer(UpdatePipe.ServerPid(_pipe.SafePipeHandle));
      var response = await OldHostWire.ReadAsync(_pipe, _transactionId, _nonce,
        _old.Identity, _local.Identity, sequence, cancellationToken).ConfigureAwait(false);
      if (response is not ("Accepted" or "Refused"))
        throw new InvalidDataException("Old-host response is invalid.");
      if (method == "Exit" && response == "Accepted") _exiting = true;
      return response == "Accepted";
    }
    finally { _gate.Release(); }
  }

  public void Dispose()
  {
    _pipe.Dispose();
    CryptographicOperations.ZeroMemory(_nonce);
    _gate.Dispose();
  }
}

[SupportedOSPlatform("windows")]
internal static class OldHostWire
{
  private sealed record Frame(int SchemaVersion, int ProtocolVersion, string Kind, Guid TransactionId,
    ProcessIdentity Sender, ProcessIdentity Recipient, int Sequence, string Nonce);
  private static readonly JsonSerializerOptions StrictJson = new()
  {
    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true
  };

  public static Task WriteAsync(PipeStream pipe, Guid transaction, byte[] nonce,
    ProcessIdentity sender, ProcessIdentity recipient, int sequence, string kind,
    CancellationToken token) => UpdateSession.WriteAsync(pipe,
      JsonSerializer.SerializeToUtf8Bytes(new Frame(1, UpdateProtocol.ProtocolVersion,
        kind, transaction, sender,
        recipient, sequence, Convert.ToBase64String(nonce))), token);

  public static async Task<string> ReadAsync(PipeStream pipe, Guid transaction,
    byte[] nonce, ProcessIdentity sender, ProcessIdentity recipient, int sequence,
    CancellationToken token)
  {
    var bytes = await UpdateSession.ReadAsync(pipe, token).ConfigureAwait(false);
    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    RejectDuplicates(document.RootElement);
    if (document.RootElement.ValueKind != JsonValueKind.Object ||
        document.RootElement.EnumerateObject().Count() != 8 ||
        document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 8)
      throw new InvalidDataException("Old-host frame shape is invalid.");
    var frame = document.RootElement.Deserialize<Frame>(StrictJson) ??
      throw new InvalidDataException("Old-host frame is missing.");
    if (frame.SchemaVersion != 1 || frame.ProtocolVersion != UpdateProtocol.ProtocolVersion ||
        frame.TransactionId != transaction ||
        frame.Sender != sender || frame.Recipient != recipient ||
        frame.Sequence != sequence || frame.Nonce is null)
      throw new InvalidDataException("Old-host frame identity is invalid.");
    byte[] supplied;
    try { supplied = Convert.FromBase64String(frame.Nonce); }
    catch (FormatException error) { throw new InvalidDataException("Old-host nonce is invalid.", error); }
    if (supplied.Length != 32 || !CryptographicOperations.FixedTimeEquals(supplied, nonce))
      throw new InvalidDataException("Old-host nonce is invalid.");
    return frame.Kind;
  }

  private static void RejectDuplicates(JsonElement value)
  {
    if (value.ValueKind == JsonValueKind.Object)
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var field in value.EnumerateObject())
      {
        if (!names.Add(field.Name))
          throw new InvalidDataException("Old-host frame has duplicate fields.");
        RejectDuplicates(field.Value);
      }
    }
    else if (value.ValueKind == JsonValueKind.Array)
      foreach (var child in value.EnumerateArray()) RejectDuplicates(child);
  }
}
