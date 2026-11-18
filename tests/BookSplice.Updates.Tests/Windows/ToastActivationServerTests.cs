using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class ToastActivationServerTests
{
  private const uint ClsctxLocalServer = 4;

  [DllImport("ole32.dll")]
  private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

  private static INotificationActivationCallback Create(Guid clsid, out int hr)
  {
    var iid = typeof(INotificationActivationCallback).GUID;
    hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxLocalServer, ref iid, out var pointer);
    if (hr < 0) return null!;
    try { return (INotificationActivationCallback)Marshal.GetObjectForIUnknown(pointer); }
    finally { Marshal.Release(pointer); }
  }

  [Fact]
  public void ActivationReachesCallbackOnlyForExpectedAppUserModelId()
  {
    var clsid = Guid.NewGuid();
    var received = new List<string?>();
    using (ToastActivationServer.Start(clsid, "Expected.App", received.Add))
    {
      var callback = Create(clsid, out var hr);
      Assert.True(hr >= 0, $"CoCreateInstance failed: 0x{hr:X8}");
      callback.Activate("Other.App", "ignored", IntPtr.Zero, 0);
      callback.Activate("Expected.App", "result-key", IntPtr.Zero, 0);
      Assert.Equal(["result-key"], received);
    }
    Create(clsid, out var after);
    Assert.True(after < 0);
  }

  [Fact]
  public void CallbackExceptionsDoNotEscape()
  {
    var clsid = Guid.NewGuid();
    using var server = ToastActivationServer.Start(clsid, "Expected.App",
      _ => throw new InvalidOperationException("boom"));
    var callback = Create(clsid, out var hr);
    Assert.True(hr >= 0);
    callback.Activate("Expected.App", "x", IntPtr.Zero, 0);
  }
}
