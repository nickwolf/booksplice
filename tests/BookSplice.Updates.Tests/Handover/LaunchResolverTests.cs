using BookSplice.Updates.Storage;
using BookSplice.Updates.Windows;
using BookSplice.Updates.Tests.Storage;

namespace BookSplice.Updates.Tests.Handover;

[Collection("WindowsOwnedStorage")]
public sealed class LaunchResolverTests
{
  [Fact]
  public async Task MissingCommittedPointerNeverSelectsNewestFolder()
  {
    var root = Path.Combine(Path.GetTempPath(), "booksplice-resolve-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var installationId = Guid.NewGuid();
      var original = Path.Combine(root, "BookSplice");
      var data = Path.Combine(root, "data");
      Directory.CreateDirectory(data);
      OriginalPackageFixture.Write(original);
      var helperPath = BookSplice.Updates.Tests.Windows.ProcessSupervisorTests.HelperPath();
      var helperDirectory = Path.GetDirectoryName(helperPath)!;
      File.Copy(helperPath, Path.Combine(original, "BookSplice.Gui.exe"), true);
      foreach (var support in new[]
      {
        "BookSplice.LeaseHost.dll", "BookSplice.LeaseHost.deps.json",
        "BookSplice.LeaseHost.runtimeconfig.json", "BookSplice.Updates.dll"
      })
        File.Copy(Path.Combine(helperDirectory, support), Path.Combine(original, support));
      var store = new OwnedDirectoryStore(root, _ => { });
      var managed = store.CreateNamedExclusive(root, ".booksplice-managed-" + installationId,
        "managed-root", installationId, Guid.NewGuid());
      var versions = store.CreateNamedExclusive(managed, "versions", "versions", installationId, Guid.NewGuid());
      var staging = store.CreateNamedExclusive(managed, "staging", "staging", installationId, Guid.NewGuid());
      var trash = store.CreateNamedExclusive(managed, "trash", "trash", installationId, Guid.NewGuid());
      var records = store.CreateNamedExclusive(managed, "records", "records", installationId, Guid.NewGuid());
      var layout = new InstallationLayout(original, data, managed, records, versions, staging, trash, store);
      Directory.CreateDirectory(Path.Combine(versions.Path, "v999"));
      var installation = InstallationRecord.FromLayout(layout, "1.0.0");
      var missing = new JournalSnapshot(new StateRead<InstallationRecord>(StateReadKind.Valid, installation),
        new StateRead<CurrentTarget>(StateReadKind.Missing),
        new StateRead<UpdateTransaction>(StateReadKind.Missing),
        new StateRead<OwnershipState>(StateReadKind.Missing));
      Assert.Equal(LaunchDecisionKind.Refused, LaunchResolver.Resolve(layout, missing).Kind);
      var current = new CurrentTarget(1, installationId,
        new OwnedOrOriginalTarget(TargetKind.Original, installation.OriginalExecutablePath,
          installation.OriginalIdentity, installation.OriginalExecutableIdentity,
          installation.OriginalPackage, installation.OriginalVersion,
          installation.OriginalProtocolVersion, null), null);
      var ownership = new OwnershipState(1, installationId, records.Identity,
        new[] { managed, records, versions, staging, trash }.Select(OwnershipEntry.FromCreated).ToArray());
      var committed = missing with
      {
        Current = new StateRead<CurrentTarget>(StateReadKind.Valid, current),
        Ownership = new StateRead<OwnershipState>(StateReadKind.Valid, ownership)
      };
      var ready = LaunchResolver.Resolve(layout, committed);
      Assert.Equal(LaunchDecisionKind.Ready, ready.Kind);
      Assert.Equal(installation.OriginalExecutablePath, ready.Target!.ExecutablePath);
      using (var child = LaunchResolver.StartVerified(layout, committed, ready, [],
        new Dictionary<string, string>()))
      {
        Assert.Equal(installation.OriginalExecutablePath, child.Identity.ExecutablePath,
          ignoreCase: true);
        Assert.False(child.HasExited);
        await child.StopAsync();
        Assert.True(child.HasExited);
      }
      Assert.True(LaunchResolver.SameTarget(ready.Target, ready.Target with
      {
        Package = PersistedPackage.FromSnapshot(ready.Target.Package!.Restore())
      }));
      var provisional = committed with
      {
        Transaction = new StateRead<UpdateTransaction>(StateReadKind.Valid,
          new UpdateTransaction(1, installationId, Guid.NewGuid(),
            BookSplice.Updates.Handover.UpdatePhase.Preparing, current.Current, null,
            null, true, BookSplice.Updates.Handover.UpdateProtocol.ProtocolVersion,
            new string('a', 64), [], null, null, current))
      };
      Assert.Equal(LaunchDecisionKind.RecoveryRequired,
        LaunchResolver.Resolve(layout, provisional).Kind);
      var failed = provisional with
      {
        Transaction = provisional.Transaction with
        {
          Value = provisional.Transaction.Value! with
          { Phase = BookSplice.Updates.Handover.UpdatePhase.Failed }
        }
      };
      Assert.Equal(LaunchDecisionKind.Ready, LaunchResolver.Resolve(layout, failed).Kind);
      Assert.Equal(LaunchDecisionKind.Refused, LaunchResolver.Resolve(layout, failed with
      {
        Transaction = failed.Transaction with
        {
          Value = failed.Transaction.Value! with
          {
            Phase = BookSplice.Updates.Handover.UpdatePhase.Committed,
            NewTarget = current.Current
          }
        }
      }).Kind);
      Assert.Equal(LaunchDecisionKind.Refused, LaunchResolver.Resolve(layout,
        committed with { Current = new StateRead<CurrentTarget>(StateReadKind.Invalid) }).Kind);
      Assert.Equal(LaunchDecisionKind.Refused, LaunchResolver.Resolve(layout,
        committed with
        {
          Current = new StateRead<CurrentTarget>(StateReadKind.Valid,
            current with { Current = current.Current with { ProtocolVersion = 2 } })
        }).Kind);
      Assert.Equal(LaunchDecisionKind.Refused, LaunchResolver.Resolve(layout,
        committed with
        {
          Installation = new StateRead<InstallationRecord>(StateReadKind.Valid,
            installation with { Records = installation.Records with { Identity = managed.Identity } })
        }).Kind);
      Directory.Move(records.Path, records.Path + "-old");
      Directory.CreateDirectory(records.Path);
      Assert.Equal(LaunchDecisionKind.Refused, LaunchResolver.Resolve(layout, committed).Kind);
      Directory.Delete(records.Path);
      Directory.Move(records.Path + "-old", records.Path);
      File.AppendAllText(Path.Combine(original, "README.md"), "changed");
      Assert.Equal(LaunchDecisionKind.Refused, LaunchResolver.Resolve(layout, committed).Kind);
    }
    finally
    {
      for (var attempt = 0; ; attempt++)
      {
        try { Directory.Delete(root, true); break; }
        catch (UnauthorizedAccessException) when (attempt < 20)
        { await Task.Delay(50); }
      }
    }
  }
}
