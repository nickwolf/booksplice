using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Handover;

[SupportedOSPlatform("windows")]
public sealed class SelfCheckRunner
{
  public static Task<SelfCheckReport> RunAsync(StagedPackage package, SettingsSnapshot snapshot,
    CancellationToken cancellationToken) => RunAsync(package, snapshot, null, cancellationToken);

  internal static async Task<SelfCheckReport> RunAsync(StagedPackage package, SettingsSnapshot snapshot,
    Action<SupervisedProcess>? afterChildStart, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(package);
    ArgumentNullException.ThrowIfNull(snapshot);
    package.Inventory.ValidatePackage(package.Manifest);
    using var stage = SafeDirectoryHandle.OpenPath(package.Stage.Path, false);
    if (stage.Identity != package.Stage.Identity)
      throw new PackageException("The self-check stage changed.");
    using var scratch = SafeDirectoryHandle.CreateBorrowedChild(stage,
      "selfcheck-" + Guid.NewGuid().ToString("N"));
    using var data = SafeDirectoryHandle.CreateBorrowedChild(scratch, "data");
    using var settingsRoot = SafeDirectoryHandle.CreateBorrowedChild(data, "BookSplice");
    if (snapshot.Bytes is { } bytes)
    {
      using var handle = settingsRoot.CreateNewFile("settings.json");
      await using var stream = new FileStream(handle, FileAccess.Write);
      await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
      await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    var pipeName = UpdatePipe.NewPipeName();
    using var server = UpdatePipe.CreateServer(pipeName);
    using var executable = VerifiedExecutable.FromStaged(package);
    package.Inventory.Revalidate();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(60));
    using var child = ProcessSupervisor.Start(executable,
      ["--update-self-check", pipeName, package.Manifest.Version],
      new Dictionary<string, string> { ["BOOKSPLICE_LOCAL_APP_DATA"] = data.Path });
    try
    {
      afterChildStart?.Invoke(child);
      var reportBytes = await ReadReportAsync(server, child, timeout.Token).ConfigureAwait(false);
      await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
      var report = ValidateResult(child.ExitCode, reportBytes, package.Manifest.Version);
      package.Inventory.Revalidate();
      await snapshot.VerifyUnchangedAsync(Path.Combine(snapshot.ParentPath, "settings.json"),
        timeout.Token).ConfigureAwait(false);
      return report;
    }
    finally
    {
      if (!child.HasExited)
        await child.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
  }

  internal static SelfCheckReport ValidateResult(int exitCode, ReadOnlySpan<byte> report,
    string expectedVersion)
  {
    if (exitCode != 0) throw new InvalidDataException("Self-check process failed.");
    return SelfCheckReport.Parse(report, expectedVersion);
  }

  internal static async Task<byte[]> ReadReportAsync(NamedPipeServerStream server,
    SupervisedProcess child, CancellationToken cancellationToken)
  {
    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
    child.VerifyPeer(UpdatePipe.ClientPid(server.SafePipeHandle));
    await server.WriteAsync(new byte[] { 0xA5 }, cancellationToken).ConfigureAwait(false);
    await server.FlushAsync(cancellationToken).ConfigureAwait(false);
    var prefix = new byte[4];
    await server.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
    var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
    if (length is <= 0 or > SelfCheckReport.MaximumBytes)
      throw new InvalidDataException("Self-check output exceeds its limit.");
    var report = new byte[length];
    await server.ReadExactlyAsync(report, cancellationToken).ConfigureAwait(false);
    return report;
  }
}
