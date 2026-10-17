using BookSplice.Updates.Windows;
using System.Runtime.Versioning;

namespace BookSplice.Updates.Storage;

public sealed class OwnedDirectoryException(string message, Exception? innerException = null)
  : IOException(message, innerException);

public sealed record OwnedDirectory(string Path, FileIdentity Identity,
  Guid InstallationId, Guid OperationId, string Kind);

[SupportedOSPlatform("windows")]
public sealed class OwnedDirectoryStore
{
  private readonly string _allowedRoot;
  private readonly FileIdentity _rootIdentity;
  private readonly Action<OwnedDirectory> _record;
  private readonly Func<string> _nameGenerator;

  public OwnedDirectoryStore(string allowedCanonicalLocalRoot, Action<OwnedDirectory> recorder)
    : this(allowedCanonicalLocalRoot, recorder, () => Guid.NewGuid().ToString("N")) { }

  internal OwnedDirectoryStore(string allowedCanonicalLocalRoot, Action<OwnedDirectory> recorder,
    Func<string> nameGenerator)
  {
    ArgumentNullException.ThrowIfNull(allowedCanonicalLocalRoot);
    ArgumentNullException.ThrowIfNull(recorder);
    ArgumentNullException.ThrowIfNull(nameGenerator);
    var normalized = System.IO.Path.TrimEndingDirectorySeparator(
      System.IO.Path.GetFullPath(allowedCanonicalLocalRoot));
    if (!string.Equals(normalized, System.IO.Path.TrimEndingDirectorySeparator(allowedCanonicalLocalRoot),
      StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException("The allowed root must be canonical.", nameof(allowedCanonicalLocalRoot));
    }

    using var root = SafeDirectoryHandle.OpenPath(normalized, true);
    _allowedRoot = normalized;
    _rootIdentity = root.Identity;
    _record = recorder;
    _nameGenerator = nameGenerator;
  }

  public OwnedDirectory CreateExclusive(string parent, string kind,
    Guid installationId, Guid operationId)
  {
    ValidateIds(installationId, operationId);
    ValidateKind(kind);
    var parentPath = ValidatePath(parent);
    SafeDirectoryHandle? parentHandle = null;
    SafeDirectoryHandle? child = null;
    try
    {
      parentHandle = SafeDirectoryHandle.OpenPath(parentPath, true, _allowedRoot, _rootIdentity);
      var suffix = _nameGenerator();
      if (suffix.Length != 32 || !suffix.All(Uri.IsHexDigit))
      {
        throw new OwnedDirectoryException("The generated directory name is invalid.");
      }

      child = SafeDirectoryHandle.CreateChild(parentHandle, kind + "-" + suffix);
      parentHandle = null;
      var owned = new OwnedDirectory(child.Path, child.Identity, installationId, operationId, kind);
      _record(owned);
      return owned;
    }
    catch (Exception error) when (error is not OwnedDirectoryException)
    {
      throw new OwnedDirectoryException("The owned directory could not be created or recorded.", error);
    }
    finally
    {
      child?.Dispose();
      parentHandle?.Dispose();
    }
  }

  public OwnedDirectory CreateNamedExclusive(OwnedDirectory parent, string leafName, string kind,
    Guid installationId, Guid operationId)
  {
    ArgumentNullException.ThrowIfNull(parent);
    ValidateIds(installationId, operationId);
    ValidateKind(kind);
    if (parent.InstallationId != installationId || !SafeZipExtractor.IsSafeComponent(leafName))
    {
      throw new OwnedDirectoryException("The named child does not match its owned parent.");
    }

    using var parentHandle = OpenValidated(parent);
    SafeDirectoryHandle? child = null;
    try
    {
      child = SafeDirectoryHandle.CreateBorrowedChild(parentHandle, leafName);
      var owned = new OwnedDirectory(child.Path, child.Identity, installationId, operationId, kind);
      _record(owned);
      return owned;
    }
    catch (Exception error) when (error is not OwnedDirectoryException)
    {
      throw new OwnedDirectoryException("The named child could not be created or recorded.", error);
    }
    finally { child?.Dispose(); }
  }

  public OwnedDirectory CreateNamedExclusive(string parent, string leafName, string kind,
    Guid installationId, Guid operationId)
  {
    ValidateIds(installationId, operationId);
    ValidateKind(kind);
    if (!SafeZipExtractor.IsSafeComponent(leafName))
      throw new OwnedDirectoryException("The named child is invalid.");
    using var parentHandle = SafeDirectoryHandle.OpenPath(ValidatePath(parent), true,
      _allowedRoot, _rootIdentity);
    using var child = SafeDirectoryHandle.CreateBorrowedChild(parentHandle, leafName);
    var owned = new OwnedDirectory(child.Path, child.Identity, installationId, operationId, kind);
    _record(owned);
    return owned;
  }

  public SafeDirectoryHandle OpenValidated(OwnedDirectory recorded)
    => OpenValidatedCore(recorded, true, false);

  internal SafeDirectoryHandle OpenValidatedReadOnly(OwnedDirectory recorded)
    => OpenValidatedCore(recorded, false, false);

  internal SafeDirectoryHandle OpenValidatedForRename(OwnedDirectory recorded, OwnedDirectory parent)
  {
    ArgumentNullException.ThrowIfNull(parent);
    if (recorded.InstallationId != parent.InstallationId ||
        !string.Equals(System.IO.Path.GetDirectoryName(recorded.Path), parent.Path,
          StringComparison.OrdinalIgnoreCase))
      throw new OwnedDirectoryException("The promotion source parent is not the recorded stage.");
    return OpenValidatedCore(recorded, false, true, false, parent);
  }

  internal SafeDirectoryHandle OpenValidatedForDestination(OwnedDirectory recorded)
    => OpenValidatedCore(recorded, false, false, true);

  private SafeDirectoryHandle OpenValidatedCore(OwnedDirectory recorded, bool permitChildren,
    bool permitRename, bool destinationForRename = false, OwnedDirectory? expectedParent = null)
  {
    ArgumentNullException.ThrowIfNull(recorded);
    ValidateIds(recorded.InstallationId, recorded.OperationId);
    ValidateKind(recorded.Kind);
    if (!recorded.Identity.IsValid)
    {
      throw new OwnedDirectoryException("The recorded directory identity is invalid.");
    }

    var path = ValidatePath(recorded.Path);
    if (string.Equals(path, _allowedRoot, StringComparison.OrdinalIgnoreCase))
    {
      throw new OwnedDirectoryException("The recorded path is not an owned directory.");
    }

    SafeDirectoryHandle? current = null;
    try
    {
      current = SafeDirectoryHandle.OpenPath(path, permitChildren, _allowedRoot, _rootIdentity,
        permitRename, destinationForRename, expectedParent?.Path, expectedParent?.Identity);
      if (current.Identity != recorded.Identity)
      {
        throw new OwnedDirectoryException("The directory at the recorded path has changed.");
      }

      var validated = current;
      current = null;
      return validated;
    }
    catch (Exception error) when (error is not OwnedDirectoryException)
    {
      throw new OwnedDirectoryException("The recorded directory could not be reopened.", error);
    }
    finally
    {
      current?.Dispose();
    }
  }

  private string ValidatePath(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    if (!string.Equals(full, _allowedRoot, StringComparison.OrdinalIgnoreCase) &&
        !full.StartsWith(_allowedRoot + System.IO.Path.DirectorySeparatorChar,
          StringComparison.OrdinalIgnoreCase))
    {
      throw new OwnedDirectoryException("The path is outside the allowed root.");
    }

    return full;
  }

  private static void ValidateIds(Guid installationId, Guid operationId)
  {
    if (installationId == Guid.Empty || operationId == Guid.Empty)
    {
      throw new OwnedDirectoryException("Installation and operation IDs are required.");
    }
  }

  private static void ValidateKind(string kind)
  {
    if (string.IsNullOrEmpty(kind) || kind.Length > 40 ||
        !char.IsAsciiLetterOrDigit(kind[0]) ||
        !kind.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
    {
      throw new OwnedDirectoryException("The ownership kind must be one safe component.");
    }
  }

}
