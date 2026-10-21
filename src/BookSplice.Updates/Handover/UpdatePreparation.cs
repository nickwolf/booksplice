using System.Runtime.Versioning;
using BookSplice.Updates.Policy;
using BookSplice.Updates.Storage;

namespace BookSplice.Updates.Handover;

public sealed record ConfirmedUpdate(ReleaseCandidate Release, ReleaseIdentity ReleaseIdentity,
  string ManifestSha256, OwnedOrOriginalTarget OldTarget, Guid UserConfirmationId);

public enum UpdateOutcomeKind { Prepared, Committed, Cancelled, Deferred, Refused, Failed }

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string? Reason = null);

public sealed class UpdateDeferredException(string message) : Exception(message);

[SupportedOSPlatform("windows")]
public sealed class UpdatePreparation
{
  internal static UpdateOutcome? CheckConfirmation(ConfirmedUpdate update)
  {
    ArgumentNullException.ThrowIfNull(update);
    if (update.UserConfirmationId == Guid.Empty || update.OldTarget is null ||
        update.ManifestSha256 is not { Length: 64 } ||
        !update.ManifestSha256.All(Uri.IsHexDigit))
      throw new ArgumentException("The update confirmation is incomplete.", nameof(update));
    var selected = ReleaseSelector.Select([update.Release]);
    if (!selected.Installable || selected.Identity is null ||
        !selected.Identity.Equals(update.ReleaseIdentity))
      return new UpdateOutcome(UpdateOutcomeKind.Deferred, "Release confirmation changed.");
    var oldVersion = SemanticVersion.ParseTag("v" + update.OldTarget.Version);
    if (update.Release.Version.CompareTo(oldVersion) <= 0)
      return new UpdateOutcome(UpdateOutcomeKind.Deferred, "The release is not newer than the installed version.");
    return null;
  }
}
