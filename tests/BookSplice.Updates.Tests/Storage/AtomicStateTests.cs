using BookSplice.Updates.Storage;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace BookSplice.Updates.Tests.Storage;

[Collection("WindowsOwnedStorage")]
public sealed class AtomicStateTests(ITestOutputHelper output)
{
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
    nint securityAttributes, uint disposition, uint flags, nint templateFile);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input,
    uint inputLength, nint output, uint outputLength, out uint returned, nint overlapped);

  private sealed record TestState(int SchemaVersion, string Value);
  private sealed record CounterState(int SchemaVersion, int Count);

  private static (string Root, OwnedDirectory Owned) NewRoot()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-state-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var store = new OwnedDirectoryStore(root, _ => { });
    return (root, store.CreateExclusive(root, "managed", Guid.NewGuid(), Guid.NewGuid()));
  }

  private static AtomicStateStore Store(OwnedDirectory owned, Action<StateWritePoint>? fault = null) =>
    new(owned, [new StateRecordDefinition<TestState>("state.json", value =>
      value.SchemaVersion == 1 && !string.IsNullOrEmpty(value.Value))], fault);

  [Fact]
  public void MissingAndReplacementAreNativeAtomic()
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "state.json");
      var store = Store(owned);
      Assert.Equal(StateReadKind.Missing, store.Read<TestState>(path).Kind);
      store.Write(path, new TestState(1, "old"));
      Assert.Equal("old", store.Read<TestState>(path).Value!.Value);
      store.Write(path, new TestState(1, "new"));
      Assert.Equal("new", store.Read<TestState>(path).Value!.Value);
    }
    finally { Directory.Delete(root, true); }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void CreationCallbackCanDurablyWriteMissingOrReplaceExistingState(bool existing)
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "state.json");
      var state = Store(owned);
      if (existing) state.Write(path, new TestState(1, "old"));
      var creator = new OwnedDirectoryStore(root, _ => state.Write(path, new TestState(1, "new")));
      var child = creator.CreateNamedExclusive(owned.Path, "version", "version",
        owned.InstallationId, Guid.NewGuid());
      Assert.True(child.Identity.IsValid);
      Assert.Equal("new", state.Read<TestState>(path).Value!.Value);
    }
    finally { Directory.Delete(root, true); }
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  [InlineData(4)]
  public void FaultLeavesWholeOldOrNewRecord(int point)
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "state.json");
      Store(owned).Write(path, new TestState(1, "old"));
      Assert.Throws<IOException>(() => Store(owned, current =>
      {
        if ((int)current == point) throw new IOException("injected");
      }).Write(path, new TestState(1, "new")));
      var result = Store(owned).Read<TestState>(path);
      Assert.Equal(StateReadKind.Valid, result.Kind);
      Assert.Equal(point == 4 ? "new" : "old", result.Value!.Value);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void PendingTempCannotBeWrittenOrDeletedBeforeRename()
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "state.json");
      var store = Store(owned, point =>
      {
        if (point != StateWritePoint.BeforeReplace) return;
        var temp = Assert.Single(Directory.EnumerateFiles(owned.Path, "state.json.*.tmp"));
        Assert.Throws<IOException>(() => File.Open(temp, FileMode.Open, FileAccess.Write,
          FileShare.ReadWrite | FileShare.Delete));
        Assert.Throws<IOException>(() => File.Delete(temp));
      });
      store.Write(path, new TestState(1, "exact"));
      Assert.Equal("exact", Store(owned).Read<TestState>(path).Value!.Value);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void RetainedStateRootRefusesInPlaceReparseBeforeReplace()
  {
    var (root, owned) = NewRoot();
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-state-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var control = Path.Combine(root, "reparse-control");
    Directory.CreateDirectory(control);
    try
    {
      Assert.True(TrySetMountPoint(control, outside, out var controlError),
        $"The reparse probe must work on an empty directory (Win32 {controlError}).");
      ClearMountPoint(control);
      var path = Path.Combine(owned.Path, "state.json");
      var set = false;
      Store(owned, point =>
      {
        if (point == StateWritePoint.BeforeReplace)
        {
          set = TrySetMountPoint(owned.Path, outside, out var error);
          output.WriteLine($"Nonempty root FSCTL_SET_REPARSE_POINT: success={set}, Win32={error}");
        }
      }).Write(path, new TestState(1, "exact"));
      Assert.False(set);
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
      Assert.Equal("exact", Store(owned).Read<TestState>(path).Value!.Value);
    }
    finally
    {
      Directory.Delete(outside);
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public void EmptyStateRootChangedAfterOpenCannotRedirectFirstLockOrRecord()
  {
    var (root, owned) = NewRoot();
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-state-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var control = Path.Combine(root, "reparse-control");
    Directory.CreateDirectory(control);
    var changed = false;
    try
    {
      Assert.True(TrySetMountPoint(control, outside, out var controlError),
        $"The reparse probe must work on an empty directory (Win32 {controlError}).");
      ClearMountPoint(control);
      var path = Path.Combine(owned.Path, "state.json");
      var state = new AtomicStateStore(owned,
        [new StateRecordDefinition<TestState>("state.json", value =>
          value.SchemaVersion == 1 && !string.IsNullOrEmpty(value.Value))], null,
        () => changed = TrySetMountPoint(owned.Path, outside, out _));
      var refusal = Record.Exception(() => state.Write(path, new TestState(1, "exact")));
      Assert.NotNull(refusal);
      output.WriteLine($"Empty root native refusal: {refusal.GetType().FullName}, HResult=0x{refusal.HResult:X8}, {refusal.Message}");
      Assert.True(changed, "The empty state root must accept the in-place reparse mutation.");
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    finally
    {
      if (changed) ClearMountPoint(owned.Path);
      Directory.Delete(outside);
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public void ReplacedRecordRootJunctionIsRefused()
  {
    var (root, owned) = NewRoot();
    var outside = Path.Combine(Path.GetTempPath(), "booksplice-state-outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside);
    var moved = owned.Path + "-created";
    try
    {
      var store = Store(owned);
      Directory.Move(owned.Path, moved);
      using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
      {
        ArgumentList = { "/d", "/c", "mklink", "/J", owned.Path, outside },
        UseShellExecute = false,
        CreateNoWindow = true
      })!)
      {
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
      }
      Assert.ThrowsAny<Exception>(() => store.Write(Path.Combine(owned.Path, "state.json"),
        new TestState(1, "refused")));
      Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    finally
    {
      if (Directory.Exists(owned.Path)) Directory.Delete(owned.Path);
      Directory.Delete(outside);
      Directory.Delete(root, true);
    }
  }

  [Fact]
  public async Task TransitionKeepsReadAndWriteUnderOneLock()
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "state.json");
      Store(owned).Write(path, new TestState(1, "0"));
      var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
      {
        Store(owned).Transition<TestState>(path, current =>
        {
          var next = int.Parse(current.Value!.Value,
            System.Globalization.CultureInfo.InvariantCulture) + 1;
          return new TestState(1, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
      }));
      await Task.WhenAll(tasks);
      Assert.Equal("8", Store(owned).Read<TestState>(path).Value!.Value);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public async Task SeparateProcessesSerializeTransitionsAndRejectObsoleteExpectedValue()
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "counter.json");
      var store = new AtomicStateStore(owned,
        [new StateRecordDefinition<CounterState>("counter.json", value =>
          value.SchemaVersion == 1 && value.Count >= 0)]);
      store.Write(path, new CounterState(1, 0));
      var current = new DirectoryInfo(AppContext.BaseDirectory);
      while (current is not null && !File.Exists(Path.Combine(current.FullName, "BookSplice.slnx")))
        current = current.Parent;
      Assert.NotNull(current);
      var host = Path.Combine(current.FullName, "tests", "BookSplice.LeaseHost", "bin",
        "Release", "net10.0-windows", "BookSplice.LeaseHost.dll");
      using var first = StartCounter(host, owned, 60);
      using var second = StartCounter(host, owned, 60);
      try
      {
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync())
          .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
      }
      finally
      {
        foreach (var process in new[] { first, second })
        {
          if (!process.HasExited) process.Kill(entireProcessTree: true);
          await process.WaitForExitAsync();
        }
      }
      Assert.Equal(120, store.Read<CounterState>(path).Value!.Count);
      Assert.Throws<InvalidOperationException>(() => store.Transition<CounterState>(path, prior =>
      {
        if (prior.Value!.Count != 0) throw new InvalidOperationException("obsolete");
        return new CounterState(1, 1);
      }));
      Assert.Equal(120, store.Read<CounterState>(path).Value!.Count);
    }
    finally { Directory.Delete(root, true); }
  }

  private static Process StartCounter(string host, OwnedDirectory owned, int increments)
  {
    var start = new ProcessStartInfo("dotnet")
    {
      UseShellExecute = false,
      RedirectStandardError = true
    };
    foreach (var value in new[] { host, owned.Path, "state-increment",
      owned.Identity.VolumeSerial.ToString(System.Globalization.CultureInfo.InvariantCulture),
      owned.Identity.FileIdHex, owned.InstallationId.ToString("D"),
      owned.OperationId.ToString("D"),
      increments.ToString(System.Globalization.CultureInfo.InvariantCulture) })
      start.ArgumentList.Add(value);
    return Process.Start(start)!;
  }

  [Fact]
  public void OwnershipDefinitionRejectsPathAndIdentitySubstitution()
  {
    var (root, owned) = NewRoot();
    try
    {
      var definition = OwnershipState.Definition("ownership.json", owned);
      var good = new OwnershipState(1, owned.InstallationId, owned.Identity,
        [OwnershipEntry.FromCreated(owned with { Kind = "managed-root" })]);
      Assert.True(definition.Validate(good));
      Assert.False(definition.Validate(good with { RecordRootIdentity = default }));
      Assert.False(definition.Validate(good with
      {
        Entries =
        [good.Entries[0] with { Path = Path.Combine(root, "outside") }]
      }));
      Assert.False(definition.Validate(good with
      {
        Entries =
        [good.Entries[0] with { UpdaterCreatedMarker = "forged" }]
      }));
      Assert.False(definition.Validate(good with
      {
        Entries =
        [good.Entries[0] with { Identity = default }]
      }));
      Assert.False(definition.Validate(good with
      {
        Entries =
        [good.Entries[0], good.Entries[0]]
      }));
    }
    finally { Directory.Delete(root, true); }
  }

  [Theory]
  [InlineData("foo.")]
  [InlineData("foo:ads")]
  [InlineData("CON")]
  [InlineData("nested\\NUL.txt")]
  public void OwnershipDefinitionRejectsUnsafeChildComponents(string relative)
  {
    var (root, owned) = NewRoot();
    try
    {
      var definition = OwnershipState.Definition("ownership.json", owned);
      var child = OwnershipEntry.FromCreated(owned with
      {
        Path = Path.Combine(owned.Path, relative),
        Kind = "version"
      });
      var state = new OwnershipState(1, owned.InstallationId, owned.Identity, [child]);
      Assert.False(definition.Validate(state));
    }
    finally { Directory.Delete(root, true); }
  }

  private static bool TrySetMountPoint(string directory, string target, out int error)
  {
    var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
    var print = Encoding.Unicode.GetBytes(target);
    var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
    substitute.CopyTo(buffer, 16);
    print.CopyTo(buffer, 18 + substitute.Length);
    using var handle = CreateFileW(directory, 0x100, 7, 0, 3, 0x02000000 | 0x00200000, 0);
    if (handle.IsInvalid)
    {
      error = Marshal.GetLastWin32Error();
      return false;
    }
    var result = DeviceIoControl(handle, 0x000900A4, buffer,
      (uint)buffer.Length, 0, 0, out _, 0);
    error = result ? 0 : Marshal.GetLastWin32Error();
    return result;
  }

  private static void ClearMountPoint(string directory)
  {
    var buffer = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
    using var handle = CreateFileW(directory, 0x100, 7, 0, 3, 0x02000000 | 0x00200000, 0);
    Assert.False(handle.IsInvalid);
    Assert.True(DeviceIoControl(handle, 0x000900AC, buffer, (uint)buffer.Length,
      0, 0, out _, 0));
  }

  [Theory]
  [InlineData("{\"schemaVersion\":2,\"value\":\"x\"}")]
  [InlineData("{\"schemaVersion\":1,\"value\":\"x\",\"value\":\"y\"}")]
  [InlineData("{\"schemaVersion\":1,\"value\":\"x\",\"extra\":1}")]
  [InlineData("{\"schemaVersion\":\"1\",\"value\":\"x\"}")]
  [InlineData("{\"schemaVersion\":1}")]
  [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":1,\"value\":\"x\"}")]
  public void InvalidStateIsExplicit(string json)
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "state.json");
      File.WriteAllText(path, json);
      Assert.Equal(StateReadKind.Invalid, Store(owned).Read<TestState>(path).Kind);
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void InvalidUtf8OversizeAndNumericStringAreExplicit()
  {
    var (root, owned) = NewRoot();
    try
    {
      var path = Path.Combine(owned.Path, "counter.json");
      var store = new AtomicStateStore(owned,
        [new StateRecordDefinition<CounterState>("counter.json", value =>
          value.SchemaVersion == 1 && value.Count >= 0, 128)]);
      File.WriteAllBytes(path, [0x7b, 0xff, 0x7d]);
      Assert.Equal(StateReadKind.Invalid, store.Read<CounterState>(path).Kind);
      File.WriteAllText(path, "{\"schemaVersion\":1,\"count\":\"2\"}");
      Assert.Equal(StateReadKind.Invalid, store.Read<CounterState>(path).Kind);
      File.WriteAllBytes(path, new byte[129]);
      Assert.Equal(StateReadKind.Invalid, store.Read<CounterState>(path).Kind);
    }
    finally { Directory.Delete(root, true); }
  }
}
