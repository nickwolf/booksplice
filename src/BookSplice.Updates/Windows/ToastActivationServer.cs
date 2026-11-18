using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BookSplice.Updates.Windows;

[ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INotificationActivationCallback
{
  void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
    [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs, IntPtr data, uint count);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
  [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance);
  [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
}

// Receives toast clicks from Windows: a registered class object gets them in-process, otherwise Windows launches the
// exe named by LocalServer32 with -Embedding and expects it to call Start.
[SupportedOSPlatform("windows")]
public static class ToastActivationServer
{
  private const uint ClsctxLocalServer = 4;
  private const uint RegclsMultipleUse = 1;
  private const int ClassENoAggregation = unchecked((int)0x80040110);

  public static IDisposable Start(Guid clsid, string expectedAppUserModelId, Action<string?> onActivated)
  {
    ArgumentException.ThrowIfNullOrEmpty(expectedAppUserModelId);
    ArgumentNullException.ThrowIfNull(onActivated);
    var factory = new Factory(new Callback(expectedAppUserModelId, onActivated));
    var hr = CoRegisterClassObject(ref clsid, factory, ClsctxLocalServer, RegclsMultipleUse, out var cookie);
    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    return new Registration(cookie, factory);
  }

  private sealed class Registration(uint cookie, Factory factory) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = CoRevokeClassObject(cookie);
      GC.KeepAlive(factory);
    }
  }

  private sealed class Callback(string expected, Action<string?> onActivated) : INotificationActivationCallback
  {
    public void Activate(string appUserModelId, string invokedArgs, IntPtr data, uint count)
    {
      if (!string.Equals(appUserModelId, expected, StringComparison.Ordinal)) return;
      // A click has nothing to report back, so a failure in the handler stays here.
      try { onActivated(invokedArgs); }
      catch (Exception) { }
    }
  }

  private sealed class Factory(Callback callback) : IClassFactory
  {
    public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance)
    {
      instance = IntPtr.Zero;
      if (outer != IntPtr.Zero) return ClassENoAggregation;
      var unknown = Marshal.GetIUnknownForObject(callback);
      try { return Marshal.QueryInterface(unknown, in riid, out instance); }
      finally { Marshal.Release(unknown); }
    }

    public int LockServer(bool lockServer) => 0;
  }

  [DllImport("ole32.dll")]
  private static extern int CoRegisterClassObject(ref Guid clsid,
    [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

  [DllImport("ole32.dll")]
  private static extern int CoRevokeClassObject(uint cookie);
}
