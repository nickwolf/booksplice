using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using BookSplice.Core.Settings;
using BookSplice.Updates.Handover;

namespace BookSplice.Gui.Updates;

public static class UpdateSelfCheck
{
  private static readonly string[] RequiredFiles =
  [
    "BookSplice.Gui.exe", "booksplice.exe", "VERSION.txt",
    "tools/ffmpeg/ffmpeg.exe", "tools/ffmpeg/ffprobe.exe", "sources/manifest.json"
  ];

  public static async Task<int> RunAsync(string pipeName, string expectedVersion,
    string isolatedDataRoot, CancellationToken cancellationToken)
  {
    var checks = new Dictionary<string, SelfCheckEntry>(StringComparer.Ordinal);
    void Check(string name, Action action)
    {
      try { action(); checks[name] = new(true, null); }
      catch (Exception error) { checks[name] = new(false, error.GetType().Name); }
    }
    Check("packagedIdentity", () =>
    {
      if (!PackagedIdentity.Matches(expectedVersion)) throw new InvalidDataException();
    });
    Check("payloadInventory", () =>
    {
      foreach (var path in RequiredFiles)
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, path))) throw new FileNotFoundException();
    });
    Check("runtime", () => _ = new Window());
    var settingsPath = Path.Combine(isolatedDataRoot, "BookSplice", "settings.json");
    Check("settingsSchema", () =>
    {
      if (File.Exists(settingsPath) && new FileInfo(settingsPath).Length > 1024 * 1024)
        throw new InvalidDataException();
      var bytes = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
      var loaded = JsonSettingsStore.ParseSnapshot(bytes);
      if (loaded.Code is not (SettingsLoadCode.Valid or SettingsLoadCode.Missing))
        throw new InvalidDataException();
    });
    var toolRoot = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
    Check("ffmpeg", () => ProbeTool(Path.Combine(toolRoot, "ffmpeg.exe"),
      "ffmpegSha256", "ffmpeg", "-encoders", @"(?m)^\s*A[.A-Z]{5}\s+aac\s"));
    Check("ffprobe", () => ProbeTool(Path.Combine(toolRoot, "ffprobe.exe"),
      "ffprobeSha256", "ffprobe", "-formats", @"(?m)^\s*D\s+mov,mp4,m4a"));
    var window = new Window
    {
      Width = 16,
      Height = 16,
      ShowInTaskbar = false,
      Content = new System.Windows.Controls.TextBlock { Text = "BookSplice" },
      WindowStyle = WindowStyle.None,
      ResizeMode = ResizeMode.NoResize,
      Topmost = false
    };
    try
    {
      using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      readyDeadline.CancelAfter(TimeSpan.FromSeconds(60));
      var ready = WpfReadiness.WaitAsync(window, window.Dispatcher, readyDeadline.Token);
      window.Show();
      await ready;
      checks["wpfRendered"] = new(true, null);
    }
    catch (Exception error) { checks["wpfRendered"] = new(false, error.GetType().Name); }
    finally { window.Close(); }
    var report = new SelfCheckReport(1, "BookSplice", PackagedIdentity.Version ?? "source",
      "win-x64", UpdateProtocol.ProtocolVersion, checks);
    var bytesOut = JsonSerializer.SerializeToUtf8Bytes(report);
    if (bytesOut.Length > SelfCheckReport.MaximumBytes) return 1;
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(cancellationToken);
    var acknowledgement = new byte[1];
    await client.ReadExactlyAsync(acknowledgement, cancellationToken);
    if (acknowledgement[0] != 0xA5) return 1;
    var prefix = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(prefix, bytesOut.Length);
    await client.WriteAsync(prefix, cancellationToken);
    await client.WriteAsync(bytesOut, cancellationToken);
    await client.FlushAsync(cancellationToken);
    return checks.Values.All(x => x.Ok) ? 0 : 1;
  }

  internal static void ProbeTool(string path, string hashField, string name, string option,
    string capabilityPattern)
  {
    if (!File.Exists(path)) throw new FileNotFoundException();
    var sourcePath = Path.Combine(AppContext.BaseDirectory, "sources", "manifest.json");
    if (new FileInfo(sourcePath).Length > 1024 * 1024) throw new InvalidDataException();
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(sourcePath));
    var expectedHash = manifest.RootElement.GetProperty(hashField).GetString();
    var expectedVersion = manifest.RootElement.GetProperty("expectedVersionPrefix").GetString()
      ?? throw new InvalidDataException();
    if (!expectedVersion.StartsWith("ffmpeg version ", StringComparison.Ordinal))
      throw new InvalidDataException();
    var expectedPrefix = name == "ffprobe"
      ? "ffprobe" + expectedVersion["ffmpeg".Length..] : expectedVersion;
    using (var image = File.OpenRead(path))
      if (!string.Equals(Convert.ToHexString(SHA256.HashData(image)), expectedHash,
        StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
    var start = new ProcessStartInfo(path)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    start.ArgumentList.Add(option);
    using var process = Process.Start(start) ?? throw new IOException("Bundled tool did not start.");
    var output = ReadBoundedAsync(process.StandardOutput);
    var error = ReadBoundedAsync(process.StandardError);
    if (!process.WaitForExit(10_000))
    { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new TimeoutException(); }
    var stdout = output.GetAwaiter().GetResult();
    var stderr = error.GetAwaiter().GetResult();
    var response = stdout + stderr;
    if (process.ExitCode != 0 ||
        !(stdout.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
          stderr.StartsWith(expectedPrefix, StringComparison.Ordinal)) ||
        !Regex.IsMatch(response, capabilityPattern, RegexOptions.CultureInvariant))
      throw new InvalidDataException();
  }

  private static async Task<string> ReadBoundedAsync(StreamReader reader)
  {
    var result = new System.Text.StringBuilder();
    var buffer = new char[4096];
    while (true)
    {
      var count = await reader.ReadAsync(buffer.AsMemory());
      if (count == 0) return result.ToString();
      if (result.Length + count > 65536) throw new InvalidDataException("Tool output exceeds its limit.");
      result.Append(buffer, 0, count);
    }
  }
}
