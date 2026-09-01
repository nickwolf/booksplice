namespace AudiobookConverter.Core.Settings;

public sealed record OutputProbeResult(bool Succeeded, string Code, string? Error = null)
{
  public static OutputProbeResult Success() => new(true, "ok");
}

public interface IOutputDirectoryProbe
{
  Task<OutputProbeResult> ProbeAsync(string directory, CancellationToken cancellationToken = default);
}

public sealed class FileOutputDirectoryProbe : IOutputDirectoryProbe
{
  public async Task<OutputProbeResult> ProbeAsync(string directory, CancellationToken cancellationToken = default)
  {
    if (!Directory.Exists(directory) || !File.GetAttributes(directory).HasFlag(FileAttributes.Directory)) return new(false, "destination-unavailable");
    var path = Path.Combine(directory, $".audiobookconverter-probe-{Guid.NewGuid():N}.tmp");
    try
    {
      await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      {
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
      }
      cancellationToken.ThrowIfCancellationRequested();
      File.Delete(path);
      return new(true, "ok");
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      if (File.Exists(path)) try { File.Delete(path); } catch { }
      return new(false, "probe-failed", ex.Message);
    }
  }
}

public sealed record FirstLaunchResult(bool Completed, string Code, string? Error = null);

public sealed class FirstLaunchService
{
  private readonly ISettingsStore _store;
  private readonly IOutputDirectoryProbe _probe;
  public FirstLaunchService(ISettingsStore store, IOutputDirectoryProbe? probe = null) { _store = store; _probe = probe ?? new FileOutputDirectoryProbe(); }

  public async Task<FirstLaunchResult> CompleteAsync(AppSettings settings, CancellationToken cancellationToken = default)
  {
    var validation = SettingsValidator.Validate(settings);
    if (!validation.IsValid) return new(false, "validation-failed", string.Join("; ", validation.Errors));
    var probe = await _probe.ProbeAsync(settings.OutputDirectory, cancellationToken);
    if (!probe.Succeeded) return new(false, probe.Code, probe.Error);
    try { await _store.SaveAsync(settings, cancellationToken); return new(true, "completed"); }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { return new(false, "save-failed", ex.Message); }
  }
}
