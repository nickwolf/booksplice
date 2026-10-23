using System.ComponentModel;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Windows;

public sealed class ImageFingerprintTests
{
  // A launcher pins the version folder with write access, so a read-only ancestor walk gets a
  // sharing violation (Win32 32). Only then does the direct read run; sharing rules are unchanged.
  [Fact]
  public void DirectReadRunsOnlyAfterASharingViolation()
  {
    if (!OperatingSystem.IsWindows()) return;
    var root = Path.Combine(Path.GetTempPath(), "BookSplice-tests", Guid.NewGuid().ToString("N"));
    var folder = Directory.CreateDirectory(Path.Combine(root, "pinned")).FullName;
    var image = Path.Combine(folder, "app.exe");
    File.WriteAllText(image, "image");
    try
    {
      var plain = ProcessSupervisor.ImageFingerprintOrDirect(image);
      Assert.Equal(plain, ProcessSupervisor.ImageFingerprint(image));
      using var pin = SafeDirectoryHandle.OpenPath(folder, permitChildren: true);
      var error = Assert.Throws<Win32Exception>(() => ProcessSupervisor.ImageFingerprint(image));
      Assert.Equal(32, error.NativeErrorCode);
      Assert.Equal(plain, ProcessSupervisor.ImageFingerprintOrDirect(image));
    }
    finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void OtherFailuresAreNotHidden()
  {
    if (!OperatingSystem.IsWindows()) return;
    Assert.ThrowsAny<Exception>(() => ProcessSupervisor.ImageFingerprintOrDirect(
      Path.Combine(Path.GetTempPath(), "BookSplice-tests", Guid.NewGuid().ToString("N"), "missing.exe")));
  }
}
