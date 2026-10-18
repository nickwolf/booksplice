using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using BookSplice.Updates.Handover;
using Microsoft.Win32.SafeHandles;

namespace BookSplice.Updates.Windows;

public enum UpdateMessageKind { Ready, Commit, Abort, OldExited }

public sealed record UpdateMessage(UpdateMessageKind Kind, Guid TransactionId, int ProtocolVersion,
  ProcessIdentity Sender, ProcessIdentity Recipient);

[SupportedOSPlatform("windows")]
public sealed class UpdateSession : IDisposable
{
  private static readonly JsonSerializerOptions StrictJson = new()
  {
    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
  };
  private readonly PipeStream _pipe;
  private readonly SupervisedProcess _peer;
  private readonly Guid _transaction;
  private readonly byte[] _nonce;
  private readonly ProcessIdentity _local;
  private bool _aborted;
  private bool _disposed;

  internal UpdateSession(PipeStream pipe, SupervisedProcess peer, Guid transaction, byte[] nonce,
    ProcessIdentity local)
  {
    _pipe = pipe;
    _peer = peer;
    _transaction = transaction;
    _nonce = nonce;
    _local = local;
  }

  public async Task SendAsync(UpdateMessageKind kind, CancellationToken cancellationToken = default)
  {
    EnsureActive();
    VerifyPeer();
    var message = new WireMessage(1, kind.ToString(), _transaction,
      UpdateProtocol.ProtocolVersion, _local, _peer.Identity, Convert.ToBase64String(_nonce));
    await WriteAsync(_pipe, JsonSerializer.SerializeToUtf8Bytes(message), cancellationToken)
      .ConfigureAwait(false);
    if (kind == UpdateMessageKind.Abort) _aborted = true;
  }

  public async Task<UpdateMessage> ReceiveAsync(CancellationToken cancellationToken = default)
  {
    EnsureActive();
    try
    {
      VerifyPeer();
      var bytes = await ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
      using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
      {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
      });
      var root = document.RootElement;
      ValidateUniqueProperties(root);
      if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 7 ||
          root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 7)
        throw new InvalidDataException("The update message shape is invalid.");
      var message = root.Deserialize<WireMessage>(StrictJson) ??
        throw new InvalidDataException("The update message is missing.");
      if (message.SchemaVersion != 1 || message.ProtocolVersion != UpdateProtocol.ProtocolVersion ||
          message.TransactionId != _transaction || message.Sender != _peer.Identity ||
          message.Recipient != _local ||
          !Enum.TryParse<UpdateMessageKind>(message.Kind, false, out var kind) ||
          !Enum.IsDefined(kind))
        throw new InvalidDataException("The update message identity is invalid.");
      byte[] supplied;
      try { supplied = Convert.FromBase64String(message.Nonce); }
      catch (FormatException error) { throw new InvalidDataException("The update nonce is invalid.", error); }
      if (supplied.Length != 32 || !CryptographicOperations.FixedTimeEquals(supplied, _nonce))
        throw new InvalidDataException("The update nonce is invalid.");
      VerifyPeer();
      if (kind == UpdateMessageKind.Abort) _aborted = true;
      return new UpdateMessage(kind, _transaction, message.ProtocolVersion, message.Sender,
        message.Recipient);
    }
    catch
    {
      _aborted = true;
      throw;
    }
  }

  private static void ValidateUniqueProperties(JsonElement element)
  {
    if (element.ValueKind == JsonValueKind.Object)
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var property in element.EnumerateObject())
      {
        if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate update message field.");
        ValidateUniqueProperties(property.Value);
      }
    }
    else if (element.ValueKind == JsonValueKind.Array)
      foreach (var child in element.EnumerateArray()) ValidateUniqueProperties(child);
  }

  private void VerifyPeer()
  {
    var pid = _pipe switch
    {
      NamedPipeServerStream server => UpdatePipe.ClientPid(server.SafePipeHandle),
      NamedPipeClientStream client => UpdatePipe.ServerPid(client.SafePipeHandle),
      _ => throw new IOException("Unsupported update pipe.")
    };
    _peer.VerifyPeer(pid);
  }

  private void EnsureActive()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_aborted || !_pipe.IsConnected) throw new IOException("The update session is closed.");
  }

  internal static async Task WriteAsync(PipeStream pipe, byte[] bytes, CancellationToken token)
  {
    if (bytes.Length is < 1 or > 4096) throw new InvalidDataException("The update message is too large.");
    var length = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
    await pipe.WriteAsync(length, token).ConfigureAwait(false);
    await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
    await pipe.FlushAsync(token).ConfigureAwait(false);
  }

  internal static async Task<byte[]> ReadAsync(PipeStream pipe, CancellationToken token)
  {
    var length = new byte[4];
    await pipe.ReadExactlyAsync(length, token).ConfigureAwait(false);
    var count = BinaryPrimitives.ReadInt32LittleEndian(length);
    if (count is < 1 or > 4096) throw new InvalidDataException("The update message is too large.");
    var bytes = new byte[count];
    await pipe.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
    return bytes;
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    CryptographicOperations.ZeroMemory(_nonce);
    _pipe.Dispose();
  }

  private sealed record WireMessage(int SchemaVersion, string Kind, Guid TransactionId,
    int ProtocolVersion, ProcessIdentity Sender, ProcessIdentity Recipient, string Nonce);
}

[SupportedOSPlatform("windows")]
public static class UpdatePipe
{
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

  internal static int ClientPid(SafePipeHandle pipe) =>
    GetNamedPipeClientProcessId(pipe, out var pid) ? checked((int)pid) :
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the pipe client.");

  internal static int ServerPid(SafePipeHandle pipe) =>
    GetNamedPipeServerProcessId(pipe, out var pid) ? checked((int)pid) :
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the pipe server.");

  public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(32);

  public static string NewPipeName() => "booksplice-update-" + Convert.ToHexStringLower(
    RandomNumberGenerator.GetBytes(24));

  public static NamedPipeServerStream CreateServer(string pipeName)
  {
    if (!ValidPipeName(pipeName)) throw new ArgumentException("Invalid update pipe name.");
    var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("No current user SID.");
    var security = new PipeSecurity();
    security.SetOwner(user);
    security.SetAccessRuleProtection(true, false);
    security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl,
      AccessControlType.Allow));
    return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
      PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
      4096, 4096, security);
  }

  public static async Task<UpdateSession> AcceptAsync(NamedPipeServerStream server,
    SupervisedProcess expectedClient, Guid transactionId, ReadOnlyMemory<byte> nonce,
    SupervisedProcess local, CancellationToken cancellationToken)
  {
    ValidateArguments(expectedClient, local, transactionId, nonce);
    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      expectedClient.VerifyPeer(ClientPid(server.SafePipeHandle));
      return new UpdateSession(server, expectedClient, transactionId, nonce.ToArray(),
        local.Identity);
    }
    catch { server.Dispose(); throw; }
  }

  public static async Task<UpdateSession> ConnectAsync(string pipeName,
    SupervisedProcess expectedServer, Guid transactionId, ReadOnlyMemory<byte> nonce,
    SupervisedProcess local, CancellationToken cancellationToken)
  {
    if (!ValidPipeName(pipeName)) throw new ArgumentException("Invalid update pipe name.");
    ValidateArguments(expectedServer, local, transactionId, nonce);
    var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
      PipeOptions.Asynchronous);
    try
    {
      await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
      expectedServer.VerifyPeer(ServerPid(client.SafePipeHandle));
      return new UpdateSession(client, expectedServer, transactionId, nonce.ToArray(),
        local.Identity);
    }
    catch { client.Dispose(); throw; }
  }

  private static bool ValidPipeName(string name) => name.StartsWith("booksplice-update-",
    StringComparison.Ordinal) && name.Length == 66 && name[18..].All(Uri.IsHexDigit);

  private static void ValidateArguments(SupervisedProcess peer, SupervisedProcess local,
    Guid transaction, ReadOnlyMemory<byte> nonce)
  {
    ArgumentNullException.ThrowIfNull(peer);
    ArgumentNullException.ThrowIfNull(local);
    if (transaction == Guid.Empty || nonce.Length != 32)
      throw new ArgumentException("The update transaction or nonce is invalid.");
  }
}
