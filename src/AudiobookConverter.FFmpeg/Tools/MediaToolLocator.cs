using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AudiobookConverter.FFmpeg.Tests")]
[assembly: InternalsVisibleTo("AudiobookConverter.Cli.Tests")]

namespace AudiobookConverter.FFmpeg.Tools;

public sealed class MediaToolLocator
{
  private readonly string _directoryPath;
  private readonly string? _expectedVersionPrefix;
  private readonly Func<string, string> _versionReader;

  public MediaToolLocator(string directoryPath)
    : this(directoryPath, expectedVersionPrefix: null, ReadVersion)
  {
  }

  public MediaToolLocator(string directoryPath, string expectedVersionPrefix)
    : this(directoryPath, expectedVersionPrefix, ReadVersion)
  {
  }

  internal MediaToolLocator(
    string directoryPath,
    string? expectedVersionPrefix,
    Func<string, string> versionReader)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
    ArgumentNullException.ThrowIfNull(versionReader);

    _directoryPath = directoryPath;
    _expectedVersionPrefix = expectedVersionPrefix;
    _versionReader = versionReader;
  }

  public MediaToolSet Resolve()
  {
    var fullDirectoryPath = Path.GetFullPath(_directoryPath);
    var ffmpegPath = Path.Combine(fullDirectoryPath, "ffmpeg.exe");
    var ffprobePath = Path.Combine(fullDirectoryPath, "ffprobe.exe");
    var missingTools = new[] { ffmpegPath, ffprobePath }
      .Where(path => !File.Exists(path))
      .Select(Path.GetFileName)
      .ToArray();

    if (missingTools.Length > 0)
    {
      throw new MediaToolException(
        $"Media tool directory is missing required executables: {string.Join(", ", missingTools)}.");
    }

    var ffmpegVersion = _versionReader(ffmpegPath).Trim();
    var ffprobeVersion = _versionReader(ffprobePath).Trim();

    ValidateVersion("ffmpeg.exe", ffmpegVersion, _expectedVersionPrefix ?? "ffmpeg version ");
    var expectedFFprobePrefix = _expectedVersionPrefix is null
      ? "ffprobe version "
      : $"ffprobe{_expectedVersionPrefix["ffmpeg".Length..]}";
    ValidateVersion("ffprobe.exe", ffprobeVersion, expectedFFprobePrefix);

    return new MediaToolSet(ffmpegPath, ffprobePath, ffmpegVersion, ffprobeVersion);
  }

  private static void ValidateVersion(string toolName, string actualVersion, string expectedPrefix)
  {
    if (!actualVersion.StartsWith(expectedPrefix, StringComparison.Ordinal))
    {
      throw new MediaToolException(
        $"Detected version drift for {toolName}. Expected output beginning with '{expectedPrefix}', " +
        $"but received '{actualVersion}'. Reacquire the pinned media tools.");
    }
  }

  private static string ReadVersion(string executablePath)
  {
    var startInfo = new ProcessStartInfo
    {
      FileName = executablePath,
      RedirectStandardError = true,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add("-version");

    try
    {
      using var process = Process.Start(startInfo)
        ?? throw new MediaToolException($"Unable to start media tool '{executablePath}'.");
      var standardOutputTask = process.StandardOutput.ReadToEndAsync();
      var standardErrorTask = process.StandardError.ReadToEndAsync();

      if (!process.WaitForExit(milliseconds: 10_000))
      {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        throw new MediaToolException($"Media tool '{executablePath}' did not report its version within 10 seconds.");
      }

      var standardOutput = standardOutputTask.GetAwaiter().GetResult();
      var standardError = standardErrorTask.GetAwaiter().GetResult();

      if (process.ExitCode != 0)
      {
        throw new MediaToolException(
          $"Media tool '{executablePath}' failed to report its version (exit code {process.ExitCode}): " +
          $"{standardError.Trim()}");
      }

      var version = standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
      if (string.IsNullOrWhiteSpace(version))
      {
        throw new MediaToolException($"Media tool '{executablePath}' returned no version information.");
      }

      return version;
    }
    catch (MediaToolException)
    {
      throw;
    }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      throw new MediaToolException(
        $"Unable to execute media tool '{executablePath}': {exception.Message}",
        exception);
    }
  }
}

public sealed class MediaToolException : Exception
{
  public MediaToolException(string message)
    : base(message)
  {
  }

  public MediaToolException(string message, Exception innerException)
    : base(message, innerException)
  {
  }
}
