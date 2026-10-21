using System.Diagnostics;
using System.Security.Cryptography;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Security;

namespace BookSplice.Updates.Tests.Packaging;

public sealed class PowerShellSigningTests
{
  [Fact]
  public async Task ManifestSignedByTheScriptVerifiesInTheClientVerifier()
  {
    var root = FindRepositoryRoot();
    var work = Path.Combine(root, "artifacts", "signing-test", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
      using var key = RSA.Create(3072);
      var spki = key.ExportSubjectPublicKeyInfo();
      File.WriteAllText(Path.Combine(work, "key.pem"), key.ExportPkcs8PrivateKeyPem());
      File.WriteAllText(Path.Combine(work, "pub.pem"), key.ExportSubjectPublicKeyInfoPem());
      var archive = Path.Combine(work, "BookSplice-1.2.3-win-x64.zip");
      File.WriteAllBytes(archive, "abc"u8.ToArray());
      var output = Path.Combine(work, "out");
      var script = Path.Combine(root, "scripts", "booksplice", "New-UpdateManifest.ps1");

      var start = new ProcessStartInfo("pwsh") { RedirectStandardError = true, RedirectStandardOutput = true };
      foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-ArchivePath", archive,
        "-Version", "1.2.3", "-SigningKeyPath", Path.Combine(work, "key.pem"),
        "-PublicKeyPath", Path.Combine(work, "pub.pem"), "-OutputDirectory", output })
      {
        start.ArgumentList.Add(argument);
      }

      using var process = Process.Start(start)!;
      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEndAsync();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
      await process.WaitForExitAsync(timeout.Token);
      Assert.True(process.ExitCode == 0, await stderr + await stdout);

      var manifest = File.ReadAllBytes(Path.Combine(output, "BookSplice-1.2.3-win-x64.update.json"));
      var envelope = File.ReadAllBytes(Path.Combine(output, "BookSplice-1.2.3-win-x64.update.sig.json"));
      var keyId = Convert.ToHexStringLower(SHA256.HashData(spki));
      var release = new ReleaseCandidate(1, "v1.2.3", SemanticVersion.ParseTag("v1.2.3"),
        new Uri("https://github.com/nickwolf/booksplice/releases/tag/v1.2.3"), "notes", false, false,
        [new ReleaseAsset(2, "BookSplice-1.2.3-win-x64.zip", new Uri("https://github.com/nickwolf/booksplice/releases/download/v1.2.3/BookSplice-1.2.3-win-x64.zip"), 3)]);
      var verified = new ManifestVerifier(new Dictionary<string, byte[]> { [keyId] = spki }).Verify(manifest, envelope, release, 1);
      Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", verified.ArchiveSha256);
    }
    finally
    {
      Directory.Delete(work, recursive: true);
    }
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BookSplice.slnx")))
    {
      directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
  }
}
