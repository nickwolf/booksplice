# BookSplice Windows Updater Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver issue #77: authenticated, click-to-install Windows updates with empty-queue deferral, startup acknowledgement, recoverable launch paths, and ownership-checked cleanup.

**Architecture:** A `net10.0` updater library owns policy and Windows adapters without depending on WPF or conversion Core. A stable Windows launcher supervises staging, handover, and recovery; the WPF host participates through authenticated IPC and Dispatcher-owned readiness. GUI and CLI share activity leases, while settings remain outside application versions.

**Tech Stack:** .NET 10, C#, WPF, xUnit, System.Text.Json, System.Security.Cryptography, HttpClient, System.IO.Compression, Windows file handles/locking, named pipes, PowerShell 7, GitHub Actions. Use existing central package versions; no new third-party runtime dependency is planned.

**Spec:** [Approved updater design](../specs/2026-10-15-booksplice-updater-design.md). Read both artifacts before executing. This plan is approved for lower-cost subagent implementation with controller review. Tasks 1 and 2 are complete; tasks 3 through 14 remain.

## Global Constraints

- Work in this `codex/updater` worktree. Primary Tools and BookSplice remain on `main`; preserve unrelated primary edits, recovery stash, and previous worktrees.
- Windows x64 ZIP, flat complete application payload, including CLI/runtime/media tools/licenses/notices/sources. Preserve conversion behavior and existing settings schema/unknown properties.
- Daily automatic checks enabled by default; first overdue check after 60 seconds; success interval 24 hours; failure retry one hour; manual checks coalesce. Valid later rate-limit deadlines take precedence.
- Fixed GitHub repository `nickwolf/booksplice`; stable semantic-version precedence; 100 releases/page, at most 10 pages; incomplete pagination fails explicitly.
- HTTP policy: HTTPS with normal certificate validation; API fixed to `api.github.com`; initial asset URLs bind repository/tag/name; allow only GitHub asset redirects to `release-assets.githubusercontent.com` and `objects.githubusercontent.com`.
- Limits: API 10 seconds; idle download 60 seconds; staging 30 minutes; page 2 MiB; notes 256 KiB; manifest 1 MiB; signature 16 KiB; ZIP 1 GiB; extraction 20,000 entries, 1 GiB/file and 4 GiB total; self-check and readiness each 60 seconds.
- RSA-PSS/SHA-256, MGF1-SHA-256, salt 32 bytes, key 3072 bits; embedded public key; key ID lowercase SHA-256 of DER SPKI; signature covers ASCII `BookSplice update manifest v1`, LF byte `0x0A`, and exact UTF-8 manifest bytes.
- Installation requires an empty queue and no analysis/preview/jobs/cleanup/settings writes/dialogs/pending additions. Freeze entry points atomically and acquire exclusive cross-process activity lease. #60 remains v0.3.0.
- No candidate execution before signature/hash/layout verification. No candidate real-settings writes before completed handover. No automatic schema migration; incompatible schema/protocol requires manual upgrade.
- Stable launcher remains outside version cleanup. Commit launch mapping only after verified readiness. Candidate becomes writable only after old-process exit. Preserve previous working version and original unowned folder.
- Delete only exact recorded owned identities with validated ancestry; record exact trash destination before rename. Never follow reparse points, adopt collisions, glob-clean, or infer success from elapsed time.
- Generated output goes under ignored `artifacts/`, resolved from the repository/script path. New updater scripts live in `scripts/booksplice/`; keep existing script locations. Logs/fixtures/keys remain ignored. No private media.
- Every task includes a failing meaningful regression, a passing implementation check, explicit staging, staged Gitleaks, and an atomic local commit. Do not bypass hooks. No push, PR, merge, production signing-secret provisioning, tag, or release without separate authorization.

## Review Focus

These implied failure cases are assigned explicit tests below rather than left to review intuition.

1. GUI closes or machine restarts after pointer replacement but before the commit record: recovery must select the previous app and preserve settings (tasks 6, 9, 13).
2. A file or folder is replaced at a formerly owned path, including the exact trash path: replacement survives cleanup (tasks 4, 11, 13).
3. An asynchronous file-drop/cover/preview callback arrives after queue emptiness was checked: no new work starts during staging (tasks 5, 10).
4. Release metadata changes while the confirmation is open: stop and obtain fresh confirmation, never install unseen bytes (tasks 2, 3, 10).
5. User launches an original executable or colliding Start Menu shortcut after an update: only validated managed mappings redirect; unowned shortcut survives (tasks 7, 10, 13).

## File ownership and dependencies

Paths below are repository-relative. Each task owns only its listed files and its tests. If delegated later, workers must preserve concurrent edits and adapt to interfaces already landed. Tasks execute sequentially; do not parallelize shared startup, solution, project, or release-script changes.

| Area | Files and responsibility |
| --- | --- |
| Shared library | Create `src/BookSplice.Updates/BookSplice.Updates.csproj` (`net10.0`), namespaces `BookSplice.Updates.Policy`, `.Security`, `.Storage`, `.Windows`, `.Transport`, `.Handover`. No reference to Core or Gui. |
| Tests | Create `tests/BookSplice.Updates.Tests/BookSplice.Updates.Tests.csproj` (`net10.0-windows`), existing central xUnit dependencies; Windows integration and deterministic unit fixtures in separate folders. |
| Stable launcher | Create `src/BookSplice.Launcher/BookSplice.Launcher.csproj` (`net10.0-windows`, WinExe, self-contained package), references Updates. No WPF dependency. |
| WPF integration | Modify `App.xaml.cs`, `MainWindow.xaml/.cs`, `ViewModels/MainWindowViewModel.cs`; create `Updates/` host/readiness classes and `Views/UpdatesWindow.xaml/.cs`. |
| CLI lease | Modify `src/BookSplice.Cli/Program.cs`, `CliApplication.cs`, `CliComposition.cs`, CLI project reference; preserve `net10.0` target and exit/JSON contracts. Guard Windows-native adapter use at the boundary. |
| Packaging | Modify existing Build-Release/Test-Release, CI/release workflows and user-facing README/ARCHITECTURE/RELEASE docs; create updater scripts under `scripts/booksplice/`. |

Dependencies: 1 -> 2 -> 3; 4 depends on 1; 5 depends on 4; 6 depends on 4; 7 depends on 3/5/6; 8 depends on 3/5/7; 9 depends on 7/8; 10 depends on 2/5/8/9; 11 depends on 4/6/9; 12 packages the preceding components; 13 rehearses those packages; 14 documents and validates the integrated result. Follow numeric order to simplify review and avoid partially connected shared files.

## Execution and commit convention

First record `git status --short`, branch/HEAD, SDK, and the baseline generated-fixture tool paths. Restore before a no-restore build. Reuse only verified pinned tool artifacts; copy to a fresh ignored output if scripts require ownership. Do not rebuild pinned media tools unless absent or invalid. Run baseline restore/build/tests with the repository's documented `BOOKSPLICE_FFMPEG_DIR` and fixture-tool settings and record actual results; never use the earlier 423-test result as a new pass.

For each task's red/green cycle, `dotnet test <project> --configuration Release --filter FullyQualifiedName~<Class>` builds its changed tests. The first expected failure must be the missing contract or specified wrong behavior; environmental/tool failures are not a red test. After green, run `git diff --check`, stage only that task's explicit files, run `gitleaks git --staged --redact --no-banner`, and commit its stated message. Stop on actual failures rather than accepting partial output as success.

### Task 1: Version and protocol contracts

**Files:** Create Updates project, `Policy/SemanticVersion.cs`, `Policy/ReleaseModels.cs`, `Policy/UpdateLimits.cs`, `Handover/ProtocolModels.cs`; create Updates.Tests project and `Policy/VersionPolicyTests.cs`; modify `BookSplice.slnx` to include both projects.

**Interfaces:** Produce `SemanticVersion.ParseTag(string tag)` (throws FormatException for invalid tag), `CompareTo(SemanticVersion other)`, and `IsPrerelease`; `ReleaseAsset(long Id, string Name, Uri DownloadUri, long Length)`; `ReleaseCandidate(long Id, string Tag, SemanticVersion Version, Uri PageUri, string Notes, bool Draft, bool Prerelease, IReadOnlyList<ReleaseAsset> Assets)`; `UpdatePhase` with Preparing/Prepared/Starting/Ready/Committing/Committed/RollingBack/Failed; `ProcessIdentity(int Pid, long StartTimeUtcTicks, string ExecutablePath)` and `ProtocolVersion = 1`.

- [x] Add `VersionPolicyTests` with real SemVer rules and exact resource constants:

```csharp
[Fact]
public void NumericPrecedenceIgnoresBuildMetadata()
{
  Assert.True(SemanticVersion.ParseTag("v1.10.0").CompareTo(SemanticVersion.ParseTag("v1.9.0")) > 0);
  Assert.Equal(0, SemanticVersion.ParseTag("v1.2.3+a").CompareTo(SemanticVersion.ParseTag("v1.2.3+b")));
  Assert.True(SemanticVersion.ParseTag("v1.2.3-rc.1").IsPrerelease);
  Assert.Throws<FormatException>(() => SemanticVersion.ParseTag("v01.2.3"));
}
```

- [x] Run Updates.Tests filtered to `VersionPolicyTests`; observe contract failure. Add all exact spec limit assertions to the same class, including long arithmetic and numeric component overflow rejection without culture dependence.
- [x] Implement named types, limits and parser. Use ordinal ASCII grammar and numeric comparison; reject invalid/oversized components explicitly rather than overflow or lexicographic ordering.
- [x] Rerun `VersionPolicyTests`; require pass. Add project references only in later consuming tasks.
- [x] Apply commit convention, message `Define updater version and protocol contracts`.

### Task 2: GitHub release checks and scheduling

**Files:** Create `Transport/GitHubReleaseClient.cs`, `Transport/ValidatedHttpTransport.cs`, `Policy/ReleaseSelector.cs`, `Policy/CheckSchedule.cs`, `Policy/UpdateCheckService.cs`; tests `Transport/ReleaseClientTests.cs`, `Policy/ReleaseSelectionTests.cs`, `Policy/CheckScheduleTests.cs`.

**Interfaces:** Consume task 1. Produce `IReleaseClient.FetchAsync(CancellationToken) -> Task<IReadOnlyList<ReleaseCandidate>>`, `ReleaseSelector.Select(IReadOnlyList<ReleaseCandidate>) -> SelectionResult`, `CheckSchedule.NextDue(DateTimeOffset now, DateTimeOffset? lastSuccess, DateTimeOffset? retryAfter) -> DateTimeOffset`, and `UpdateCheckService.CheckAsync(bool manual, CancellationToken) -> Task<CheckResult>`. `SelectionResult` carries None/Available/Ambiguous, candidate, and error; `CheckResult` distinguishes success, unavailable asset, transport failure, and rate limit. Inject `HttpMessageHandler` and `TimeProvider`; do not add production endpoint overrides.

- [x] Write selection tests that assert v1.10.0 wins over v1.9.0 despite order; drafts, prerelease tags/flags are excluded; tied highest precedence fails as ambiguous; highest missing assets stays visible without selecting an older release. Build tests through fake HTTP responses, not private GitHub calls.
- [x] Run red `ReleaseSelectionTests`; implement selector and page parser. Add `ReleaseClientTests.MetadataChangesPreserveDistinctAssetIdentity` asserting asset ID/name/length/URL changes are observable in the confirmation identity, and redirect/pagination tests: page 11 required -> explicit failure; cross-host Link/API redirect -> reject; duplicated assets -> reject; oversized bodies -> reject before parsing.
- [x] Run red transport/schedule cases; implement bounded manual redirects with auto-redirect disabled, fixed hosts, query/path checks, timeouts/cancellation, HTTP status classification and retry headers. Sanitize notes as bounded plain text. Add exact assertions: null or future last success -> now+60s; success -> +24h; failure -> +1h; later server reset wins; simultaneous manual/scheduled requests share one fetch.
- [x] Run Updates.Tests filters for `ReleaseClientTests`, `ReleaseSelectionTests`, `CheckScheduleTests`; require all pass.
- [x] Apply commit convention, message `Check stable GitHub releases with bounded scheduling`.

### Task 3: Signed manifest verification and package download

**Files:** Create `Security/UpdateManifest.cs`, `Security/ManifestVerifier.cs`, `Security/EmbeddedTrust.cs`, `Transport/PackageDownloader.cs`; tests `Security/ManifestVerifierTests.cs`, `Transport/PackageDownloaderTests.cs` and generated in-memory fixtures.

**Interfaces:** Produce `VerifiedManifest` with schemaVersion/product/repository/tag/version/target/archiveName/archiveLength/archiveSha256/packageProtocol/settingsSchemaMin/settingsSchemaMax; `ManifestVerifier.Verify(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> envelope, ReleaseCandidate release, int settingsSchema) -> VerifiedManifest`; `PackageDownloader.DownloadAsync(ReleaseCandidate release, VerifiedManifest manifest, Stream destination, IProgress<long>? progress, CancellationToken) -> Task`. Inject an internal trusted-key map for tests; production uses embedded immutable trust and fixed algorithm.

- [x] Write `ManifestVerifierTests` using `RSA.Create(3072)` in memory. Assert Verify accepts the exact signed domain/message and rejects one changed byte, LF-versus-literal-backslash-n, wrong key, key-ID mismatch, wrong repository/tag/target/protocol/schema, duplicate JSON properties, unknown fields, oversized payload, invalid UTF-8 and non-PSS signatures.
- [x] Run red; implement strict bounded envelope/manifest parsing, SPKI ID computation and `RSA.VerifyData` with SHA-256/PSS. A verification result alone permits download, never unverified DTOs. Keep private key material out of test logs/files.
- [x] Add downloader tests asserting exact length/hash, interrupted read/cancel/idle deadline, length overrun, and `ReleaseChangedAfterConfirmationFailsBeforePayloadExecution` with a changed asset identity. Require no extraction callback or executable start on any trust/identity failure.
- [x] Implement streaming download with spec limits, incremental SHA-256, progress and deletion responsibility delegated to the owned stage. Require refreshed release/asset identities and companion checksum to agree; no fallback to unsigned packages.
- [x] Run named security/download test classes; apply commit convention, message `Authenticate update manifests and downloaded packages`.

### Task 4: Windows identities, safe extraction and owned staging

**Files:** Create `Windows/NativeFileIdentity.cs`, `Windows/SafeDirectoryHandle.cs`, `Storage/OwnedDirectoryStore.cs`, `Storage/SafeZipExtractor.cs`, `Storage/PackageInventory.cs`, `Storage/PackageStager.cs`; tests `Storage/OwnedDirectoryTests.cs`, `Storage/SafeZipTests.cs`, `Storage/PackageStagerTests.cs`, Windows file-identity fixture helpers.

**Interfaces:** Produce `FileIdentity(ulong VolumeSerial, string FileIdHex)`, `OwnedDirectory(string Path, FileIdentity Identity, Guid InstallationId, Guid OperationId, string Kind)`, `OwnedDirectoryStore.CreateExclusive(string parent, string kind, Guid installationId, Guid operationId) -> OwnedDirectory`, `SafeZipExtractor.ExtractAsync(Stream archive, OwnedDirectory destination, CancellationToken) -> Task<PackageInventory>`, `PackageStager.StageAsync(ReleaseCandidate release, VerifiedManifest manifest, InstallationLayout layout, CancellationToken) -> Task<StagedPackage>`. `StagedPackage` binds owned staging/payload, inventory, manifest, and GUI path. Ownership APIs use validated canonical ancestry and handles, not string prefix checks alone.

- [x] Write red `OwnedDirectoryTests.CollisionIsNeverRecordedOrRemoved`: pre-create sentinel dir, force name collision, assert create fails and sentinel/ownership unchanged. Add `ReplacementAtRecordedPathIsRefused` and identity-survives-rename assertions using real Windows file IDs.
- [x] Implement exclusive native creation with collision-resistant names and record only successful creates. Use directory handles/reparse refusal and native identity queries. An unrecorded creation orphan is retained after crash.
- [ ] Add a parameterized malicious ZIP fixture table for traversal/absolute/UNC/drive/ADS/device/trailing-dot-space/case-equivalent duplicate/file-dir collision/links; assert no output escapes, no candidate launches, and limits apply to actual streamed output. Use injectable small limits to test 1 GiB/4 GiB boundaries without allocating gigabytes; separately assert production constants.
- [ ] Run red; implement entry preflight then bounded extraction into owned directories, full flat payload/inventory validation, same-volume no-replacement promotion support, and disk-full/cancel cleanup through recorded ownership only.
- [ ] Run the three storage classes on Windows; apply commit convention, message `Stage update packages with Windows ownership checks`.

### Task 5: Cross-process activity and GUI operation gates

**Files:** Create `Windows/ActivityLeaseStore.cs`, `Windows/GuiInstanceLease.cs`, `src/BookSplice.Gui/Updates/GuiOperationGate.cs`, `src/BookSplice.Cli/CliExecutionHost.cs`; tests `Windows/ActivityLeaseTests.cs`, `tests/BookSplice.Gui.Tests/UpdateOperationGateTests.cs`; modify GUI project reference and CLI project/Program/composition/application plus `tests/BookSplice.Cli.Tests/UpdateLeaseTests.cs`.

**Interfaces:** Produce `IActivityLeaseStore.TryEnterJob() -> IDisposable?`, `TryEnterUpdate() -> IDisposable?`, and `GuiInstanceLease.TryAcquire(string canonicalDataRoot) -> GuiInstanceLease?`. Implement Windows handle-based shared/exclusive byte-range locks: byte 0 is the admission gate, byte 1 the active-job range; jobs briefly acquire gate then a shared job lock, updates retain exclusive gate plus exclusive job range. GUI lease is separate. `GuiOperationGate.Enter(string operation) -> IDisposable`, `TryFreeze(int queueCount) -> IDisposable?`, `IsFrozen`, `ActiveCount`; all GUI methods enforce Dispatcher access. No thread-affine named mutex crosses awaits.

- [ ] Write real helper-process tests asserting two shared jobs coexist, exclusive update fails while either lives, new jobs fail during reserved update, and process death releases the OS locks. Assert caller-owned leases are disposed only by that caller.
- [ ] Run red; implement bounded nonblocking acquisition, lock order from spec, exact data-root identity, disposal and cancellation-safe failure unwinding. Do not hold file-state locks while acquiring GUI lease.
- [ ] Write GUI gate tests: queueCount>0 or ActiveCount>0 returns null; freeze blocks `Enter`; delayed add/cover/preview callback cannot enter after freeze; unfrozen cancellation restores admission. Run red then implement gate.
- [ ] Add `CliExecutionHost.RunAsync(Func<CliApplication> createApplication, IActivityLeaseStore leases, IReadOnlyList<string> arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken, TextReader? input) -> Task<CliExitCode>` in `src/BookSplice.Cli/CliExecutionHost.cs`. Program routes through it before `CliComposition.Create`, which currently resolves and starts media tools. Tests inject a factory and assert it is never invoked while updating. Help remains before this path. Busy update returns existing configuration/usage exit code 2 and final JSON event shape without source processing; preserve parser and dry-run output semantics. Hold the shared lease through the whole application run including cleanup; non-Windows composition receives an explicit no-op lease implementation rather than invoking native APIs. Do not acquire another job lease inside CliApplication or serialize independent CLI jobs.
- [ ] Run ActivityLease, GUI gate and CLI lease tests plus existing CLI configuration/end-to-end tests. Apply commit convention, message `Coordinate updates with GUI and CLI activity leases`.

### Task 6: Durable journal and launch-pointer transactions

**Files:** Create `Storage/InstallationLayout.cs`, `Storage/AtomicStateStore.cs`, `Storage/UpdateJournal.cs`, `Storage/InstallationRegistry.cs`, `Storage/SettingsSnapshot.cs`; tests `Storage/AtomicStateTests.cs`, `Storage/RecoveryDecisionTests.cs`, `Storage/SettingsSnapshotTests.cs`.

**Interfaces:** Produce `InstallationLayout` with validated original/managed/data roots and exact paths from spec; `CurrentTarget(OwnedOrOriginalTarget Current, OwnedOrOriginalTarget? Previous)`; `UpdateTransaction` with ID, phase, old/new targets, settings hash/absence, protocol, nonce, owned paths, coordinator/old/new process identities; `AtomicStateStore.Read<T>(string path) -> StateRead<T>` and `Write<T>(string path, T value) -> void`; `UpdateJournal.Transition(Guid id, UpdatePhase expected, UpdatePhase next) -> void`; `RecoveryPolicy.Decide(JournalSnapshot snapshot, ProcessSnapshot processes) -> RecoveryDecision`. Define `StateRead<T>` as Missing/Valid/Invalid, never default an invalid destructive record to empty success.

Define `OwnedOrOriginalTarget` as executable path, directory identity, package inventory/hash, version and original/managed kind. `JournalSnapshot` contains installation/current/transaction/ownership StateRead values. `ProcessSnapshot` maps each recorded ProcessIdentity to Alive/Exited/Unknown from retained handles and exact identity validation. `RecoveryDecision` contains validated launch target or refusal, exact processes to stop, pointer action (Keep/RestoreOld), and candidate write permission. Unknown process state is never interpreted as safely exited.

Produce `SettingsSnapshot.CaptureAsync(string settingsPath, CancellationToken) -> Task<SettingsSnapshot>` with exact bytes/hash/schema or verified absence. Read without modifying the source and bound input to the existing settings-store contract. Candidate performs full settings validation through Core in task 8. Test unchanged bytes, missing file, concurrent replacement and unsupported schema; a snapshot that cannot be read consistently fails, rather than mixing bytes from different files.

- [ ] Add fault-injection tests for every temp write/flush/replace boundary and assert either whole prior or whole new record is readable. Assert invalid schemas, duplicate fields, malformed paths and future journal versions produce Invalid and no delete/execute action.
- [ ] Run red; implement bounded strict records, versioned schemas, write-through temp+flush+same-volume replacement and cross-process record serialization. Retain old/current pointers together in one atomic record. The registry binds exact executable identity to installation rather than selecting an arbitrary newest folder.
- [ ] Write `RecoveryDecisionTests.PointerChangedBeforeCommitRecordRollsBack`: phase Committing with new pointer and vanished coordinator yields old target, candidate-stop requirement, unchanged settings and no cleanup. Cover every phase; a stale timestamp never yields promotion.
- [ ] Implement transition validation and pure recovery decision table; add conflict test where an obsolete transaction cannot clear or commit another transaction.
- [ ] Run both classes; apply commit convention, message `Persist recoverable updater transactions and launch targets`.

### Task 7: Stable launcher, process binding and managed shortcuts

**Files:** Create Launcher project/`Program.cs`, `LauncherApplication.cs`, Updates `Windows/ProcessSupervisor.cs`, `Windows/UpdatePipe.cs`, `Windows/ManagedShortcutStore.cs`, `Storage/LaunchResolver.cs`; tests `Handover/UpdatePipeTests.cs`, `Handover/LaunchResolverTests.cs`, `Windows/ShortcutOwnershipTests.cs`; modify solution to include launcher.

**Interfaces:** Produce `ProcessSupervisor.Start(string verifiedExe, IReadOnlyList<string> args, IReadOnlyDictionary<string,string> environment) -> SupervisedProcess`, `SupervisedProcess.Identity`, `WaitForExitAsync`, `StopAsync`; `UpdatePipe.ConnectAsync(ProcessIdentity peer, Guid transactionId, ReadOnlyMemory<byte> nonce, CancellationToken) -> Task<UpdateSession>`; messages are typed Ready/Commit/Abort/OldExited plus transaction/protocol/identity. `LaunchResolver.Resolve(InstallationLayout, JournalSnapshot) -> LaunchDecision`; `ManagedShortcutStore.EnsureRecordedShortcut(InstallationLayout) -> ShortcutResult` (Created/Unchanged/Collision/Failed).

- [ ] Add red helper-process tests for wrong nonce, PID reuse/start-time mismatch, wrong executable, late message, oversized message and pipe peer identity. Assert no Ready accepted solely on a file's existence. Enforce current-user ACL and validate named-pipe client process identity against retained process handles.
- [ ] Implement bounded versioned IPC, sanitized child environment, launch/stop/wait using exact retained handles, and launcher protocol dispatch (normal launch, coordinate, explicit recorded rollback, recover). Reject arbitrary externally supplied executable paths or test overrides. Use existing data-root override only to establish a validated installation; child receives explicit validated root.
- [ ] Add LaunchResolver tests: original path forwards only through matching committed registry; provisional pointer starts recovery; corrupt mapping refuses execution; unsupported launcher protocol requires manual upgrade. Add ShortcutOwnership tests: occupied unowned path/same name survives; owned shortcut with changed target is no longer repairable.
- [ ] Implement create-only shortcut then ownership record, atomic pointer resolution and minimal Windows recovery error display. Stable launcher bootstrap copies from the current trusted package, not the candidate, and records its file identity. Do not update pins or arbitrary user shortcuts.
- [ ] Run named classes on Windows; apply commit convention, message `Add stable updater launcher and authenticated process channel`.

### Task 8: WPF readiness and isolated packaged self-check

**Files:** Create GUI `Updates/PackagedIdentity.cs`, `Updates/UpdateSelfCheck.cs`, `Updates/WpfReadiness.cs`, `Updates/RestrictedStartup.cs`; modify `App.xaml.cs`, GUI project and assembly/build metadata; tests `tests/BookSplice.Gui.Tests/UpdateSelfCheckTests.cs`, `UpdateReadinessTests.cs`; create Updates `Handover/SelfCheckRunner.cs` and tests `Handover/SelfCheckRunnerTests.cs`.

**Interfaces:** Consume SettingsSnapshot from task 6. Produce `SelfCheckReport` schema1 with product/version/target/protocol and dictionary of named `SelfCheckEntry(bool Ok,string? Detail)`; `SelfCheckRunner.RunAsync(StagedPackage package, SettingsSnapshot snapshot, CancellationToken) -> Task<SelfCheckReport>`; `WpfReadiness.WaitAsync(Window window, Dispatcher dispatcher, CancellationToken) -> Task`; `RestrictedStartup` loads a settings snapshot and denies writes/jobs until authenticated commit and old-exit completion.

- [ ] Write red SelfCheckRunner tests for exit nonzero despite passing JSON, missing mandatory checks, wrong product/version/architecture, invalid/oversized JSON, timeout and inherited tool override. Assert real settings bytes unchanged and bundled tool path used.
- [ ] Implement hidden self-check startup before ordinary settings setup, machine-readable bounded result through a parent-owned output channel, isolated scratch data root/settings copy, actual bundled tool identity/capabilities, and exact packaged build identity. Source/dev build IDs must never become production-installable because assembly Version happens to parse.
- [ ] Write real STA/Dispatcher tests asserting readiness waits for ContentRendered, visible window and subsequent Dispatcher turn; hidden/closed/never-rendered windows fail or time out. Existing offscreen tests prove only layout, not this contract. Register candidate in restricted mode and acquire expected GUI lease; do not enter normal StartupService migration/save paths before commit.
- [ ] Implement readiness, named mandatory report fields, snapshot loading and no-write restricted host. Wire App argument dispatch for normal/source/self-check/candidate startup without environment bypasses. Ensure candidate loss of coordinator closes restricted window without persisting state.
- [ ] Run GUI and Updates named tests; apply commit convention, message `Verify packaged WPF readiness without changing user state`.

### Task 9: Supervised handover, failure recovery and rollback

**Files:** Create Updates `Handover/UpdateCoordinator.cs`, `Handover/RecoveryCoordinator.cs`, `Handover/RollbackCoordinator.cs`; consume SettingsSnapshot from task 6; Launcher routes these through `LauncherApplication.cs`; GUI create `Updates/WpfHandoverHost.cs`; tests `Handover/CoordinatorTests.cs`, `RecoveryCoordinatorTests.cs`, `RollbackTests.cs` and GUI `HandoverHostTests.cs`.

**Interfaces:** `UpdateCoordinator.RunAsync(ConfirmedUpdate update, InstallationLayout layout, IOldApplicationHost oldHost, CancellationToken) -> Task<UpdateOutcome>`; `ConfirmedUpdate` binds release confirmation identity, manifest identity and user click. `IOldApplicationHost` provides FreezeAsync/HideAndReleaseGuiAsync/RestoreAndAcquireGuiAsync/ExitAsync; `RecoveryCoordinator.RecoverAsync(InstallationLayout layout, CancellationToken) -> Task<RecoveryOutcome>`; `RollbackCoordinator.RunAsync(InstallationLayout layout, IOldApplicationHost oldHost, CancellationToken) -> Task<UpdateOutcome>`. Outcomes explicitly distinguish Installed/Cancelled/Deferred/RolledBack/Failed. `SettingsSnapshot` records verified absence or exact bytes/hash/schema without saving them over real settings.

- [ ] Write red phase-driven tests for authenticate->stage->self-check->promote->journal->hide->launch->ready->commit->old exit->candidate writable order. Assert pointer unchanged before Ready, candidate no-write through old-exit observation, settings hash checked again at commit, and activity lease retained throughout. Launch error and no-ack timeout restore old visible host.
- [ ] Implement launcher-owned coordinator using earlier components. The GUI reserves its local gate, bootstrap launcher obtains exclusive activity lease, and refusal unwinds both. Acquisition failure returns Deferred promptly. Once handover starts, cancellation cannot abandon live writers. Hide/release/restore GUI lease only on Dispatcher, with explicit ShutdownMode that keeps hidden old app alive.
- [ ] Add fault tests for coordinator/old/candidate death at each journal boundary, especially after pointer replacement and before committed record; changed settings aborts without overwrite; wrong/late ack rejected; candidate death after Ready before commit rolls back; child kill failure keeps old frozen rather than creates two writers; old-process exit timeout becomes supervised recovery, not success.
- [ ] Implement recovery from journal before normal launcher resolution, exact process termination, validated previous/original fallback and explicit rollback under the same gate/protocol/settings checks. If old GUI died, launch its validated target after stopping candidate; never infer visibility or ownership from time/PID alone.
- [ ] Run coordinator/recovery/rollback/host classes and prior IPC/journal tests; apply commit convention, message `Hand over updates with startup acknowledgement and rollback`.

### Task 10: Updates UI, deferral and direct-launch integration

**Files:** Create GUI `Updates/UpdatesViewModel.cs`, `Updates/UpdatePreferencesStore.cs`, `Updates/UpdateComposition.cs`, `Views/UpdatesWindow.xaml/.cs`; modify `MainWindow.xaml/.cs`, `ViewModels/MainWindowViewModel.cs`, `App.xaml.cs`; tests `tests/BookSplice.Gui.Tests/UpdatesViewModelTests.cs`, `UpdatesWindowTests.cs`, `UpdateEntryPointTests.cs`, modify `AccessibilityContractTests.cs` only for added controls.

**Interfaces:** `UpdatesViewModel.CheckAsync(CancellationToken)`, `InstallAsync(CancellationToken)`, `RollbackAsync(CancellationToken)`, `CancelDownload()`, `AutomaticChecksEnabled`, `InstalledVersion`, `AvailableVersion`, `ReleaseNotes`, `Phase`, `Message`, `CanInstall`; persist `UpdatePreferences(bool AutomaticChecksEnabled, DateTimeOffset? LastSuccess, string? NotifiedTag)` atomically under data-root updates directory. `UpdateComposition` wires production components and retains lifetimes; no service locator or UI mutation on workers.

- [ ] Add red view-model tests for defaults, daily/manual checks, one notice/version, failure distinct from UpToDate, note text/validated links, phase progress/cancel, and unowned shortcut collision message. Assert installation never clears rows or invokes conversion cancellation; completed/result rows also defer it.
- [ ] Wire operation registration around MainWindow add/drop, cover reads/settings dialogs and ViewModel analysis/preview/conversion including finally cleanup. Add delayed-callback tests to UpdateEntryPointTests proving freeze denies new work after final empty-queue check. Preserve the existing conversion-progress tests and tracked baseline logic untouched.
- [ ] Implement Updates window, manual action, one nonmodal discovery notice and install confirmation. Bind exact spec deferral copy, accessible keyboard/focus/live-region labels, installed/available versions and bounded plain notes. Render exclusively on Dispatcher. A metadata change during confirmation returns to Available with fresh notes/confirmation; it cannot reuse the previous click.
- [ ] Wire normal launch through validated registry/launcher resolution and GUI instance lease. Explicit direct launch of original still works when no mapping; matching committed mapping forwards once without loops. Restrict close/settings/add/drop throughout staging/handover and restore them after failure/cancel. Keep update preference separate from AppSettings.
- [ ] Run named GUI classes plus QueueTests, FirstLaunchTests, result/progress and accessibility tests; apply commit convention, message `Expose safe update checks and empty-queue installation`.

### Task 11: Retention and identity-checked trash recovery

**Files:** Create Updates `Storage/UpdateCleanup.cs`, `Policy/RetentionPolicy.cs`, extend `Storage/OwnedDirectoryStore.cs` with exact trash journal; tests `Storage/UpdateCleanupTests.cs`, `Policy/RetentionPolicyTests.cs`; hook cleanup into Launcher after completed transactions/normal startup via activity admission.

**Interfaces:** `RetentionPolicy.Select(IReadOnlyList<OwnedDirectory> owned, ProtectedTargets protectedTargets) -> IReadOnlyList<OwnedDirectory>`; `UpdateCleanup.RunAsync(InstallationLayout layout, CancellationToken) -> Task<CleanupReport>`. `ProtectedTargets` names running/current/previous/original/stable-launcher/pending paths. `CleanupReport` lists Removed/Deferred/Refused without forgetting partial deletions. Use exact source and trash identity plus operation IDs in records.

- [ ] Write red tests for retention of every protected path and for `ReplacementAtExactRecordedTrashPathSurvives`, missing/stale identity, junction ancestry, collision destination, in-use rename failure and partial deletion. Assert original unowned folder never eligible, even when named like a version.
- [ ] Implement pure retention policy and cleanup under exclusive update/activity admission. Record source identity and exclusive unique trash intent before rename; native rename must not replace a collision. Revalidate identity after rename; handle traversal must refuse replacement/reparse races. Never enumerate directory prefixes to discover delete candidates.
- [ ] Implement resumable deletion of only the journaled trash identity; retain incomplete records. Aborted staging cleanup uses the same mechanism after workers/children exit. A corrupt ownership or journal state refuses every destructive action. Startup cleanup must not race a new staging transaction.
- [ ] Run named tests with real Windows directory identities and held file handles; repeat coordinator cancellation test with cleanup active. Apply commit convention, message `Retain rollback copies and clean exact updater-owned paths`.

### Task 12: Signing tools and complete release packages

**Files:** Modify `scripts/Build-Release.ps1`, `scripts/Test-Release.ps1`, `.github/workflows/release.yml`; create `scripts/booksplice/New-UpdateManifest.ps1`, `Test-UpdateManifest.ps1`, `Test-UpdateTooling.ps1`; tests `tests/BookSplice.Updates.Tests/Packaging/BuildFlavorTests.cs`; add explicit generated build-flavor/public-trust inputs to Updates, GUI and Launcher project files.

**Interfaces:** `New-UpdateManifest.ps1 -ArchivePath <zip> -Version <semver> -SigningKeyPath <private-pem> -PublicKeyPath <public-pem> -OutputDirectory <fresh-owned-dir>` produces exact JSON/envelope assets and verifies signature before returning paths/hashes. `Test-UpdateManifest.ps1 -ArchivePath <zip> -ManifestPath <json> -SignaturePath <json> -PublicKeyPath <pem>` returns nonzero on any mismatch. Private PEM inputs are opened without logging contents and temporary keys are excluded from artifacts.

- [ ] Write failing tooling assertions in Test-UpdateTooling for cross-language RSA-PSS byte compatibility, wrong key/ZIP/version, missing production key, and existing output refusal. Add BuildFlavorTests proving Source/CI cannot install production updates, Rehearsal cannot be mistaken for Release, and Release ignores all test endpoint/key/ack/fault variables.
- [ ] Implement build flavors Source/CI/Rehearsal/Release as immutable generated metadata. Build-Release publishes launcher into flat ZIP with the same version/runtime policy and preserves all existing required payload/source/license files; normal CI version `0.1.0-ci` remains a non-production package. Extend version validation deliberately for stable SemVer build metadata and keep source/CI tags distinct. Explicit Release builds require embedded reviewed production public key; unsigned CI smoke packages must be clearly marked and not installable.
- [ ] Implement manifest scripts with exact field names from task 3, no canonicalization round trip, production public-key match, and no implicit development key. Release job uses protected environment signing secret and validates final signed assets; PR jobs use disposable keys only. Never expose private keys in command arguments, logs, reports or uploads. Existing checksum and provenance remain. Missing production signing configuration fails the release path before publication, not general contributor CI.
- [ ] Run Test-UpdateTooling, BuildFlavorTests and complete-package smoke using the non-production CI flavor. Explicitly test that production build/signing refuses absent trust/key material without generating a production key. Apply commit convention, message `Package launcher and signed update manifests`.

### Task 13: Packaged Windows update and recovery rehearsal

**Files:** Create `scripts/booksplice/Test-UpdateRehearsal.ps1`, `tools/BookSplice.UpdateHarness/BookSplice.UpdateHarness.csproj`, `Program.cs`, `FixtureServer.cs`, `ScenarioRunner.cs`, `ProcessEvidence.cs`; modify `BookSplice.slnx`, `.github/workflows/ci.yml` and Test-UpdateTooling with harness safeguard cases.

**Interfaces:** `Test-UpdateRehearsal.ps1 -MediaToolDirectory <pinned-dir> -OutputDirectory <fresh-artifacts-dir>` owns only its generated layout and returns aggregate JSON with schemaVersion/status/scenarios/packages/processCleanup and individual evidence paths. `ScenarioRunner.RunAsync(string scenario, RehearsalLayout layout, CancellationToken) -> Task<ScenarioResult>` uses real packaged processes; `ScenarioResult` includes passed assertions, package digests/flavors, settings digest and exact retained-target identities. Test harness process/transport faults are compiled only into Rehearsal packages.

- [ ] Add a failing harness safeguard scenario: unsupported/noninteractive desktop, unowned output root or unexpected process must produce a nonzero result before destructive work, never a skipped scenario reported as passed. Start with a successful-upgrade scenario that fails on missing real readiness rather than a mocked callback.
- [ ] Build two marked rehearsal versions with independent version IDs, test endpoint/trust compiled only into Rehearsal. Start isolated harness transport with fixed bounded payloads. Automate real stable-launcher/WPF success, interrupted download, wrong version, candidate no-render/no-ack, coordinator death before and after pointer replacement, successful rollback, unowned folder replacement and exact-trash collision. Assert one responsive usable GUI, unchanged settings bytes, expected current/previous pointer, surviving unowned sentinels, and no stranded owned processes. Run one generated-audio full-validation conversion using candidate's bundled media tools after success.
- [ ] Add final-production-package verification independent of marked rehearsal: verify exact ZIP/signature with embedded production key, self-check/render on clean Windows, complete payload/tool inventory and generated CLI conversion. Record that fault-injected endpoint rehearsals use Rehearsal builds; do not label them as production end-to-end delivery evidence. Exercise real GitHub production selection/install with actual immutable published signed releases only after publication is separately authorized; no test-only runtime bypass may be added to a Release build.
- [ ] Wire Windows rehearsal to existing pinned CI tools, with explicit interactive-desktop preflight. If hosted CI cannot satisfy real visibility/readiness, keep deterministic integration tests in CI and require the full packaged gate on an actual Windows desktop; record it as pending until run, never weaken readiness to make hosted CI pass. Upload only anonymous bounded reports and package identities; no private PEMs, raw nonce/IPC dumps or user paths.
- [ ] Run Test-UpdateTooling safeguards and all Test-UpdateRehearsal scenarios on a suitable Windows desktop. Require aggregate passed and no stranded helper/GUI/FFmpeg processes. Apply commit convention, message `Rehearse packaged Windows update failures and rollback`.

### Task 14: User documentation and final integrated validation

**Files:** Modify `README.md`, `ARCHITECTURE.md`, `docs/README-RELEASE.md`, `docs/RELEASE.md`, `docs/ACCEPTANCE.md`; update this plan's actual task checkboxes only after execution.

**Interfaces:** Consume package/signature identities and actual scenario reports from tasks 12/13. Produce user instructions, release/acceptance checklists, and an evidence inventory distinguishing local automated, external CI, manual desktop, production-key and publication status. No new runtime interfaces.

- [ ] Compare README and release instructions against actual output from tasks 12/13; a documented filename/flag must exist and the tested package digest must identify that exact artifact. Mark unperformed gates explicitly.
- [ ] Document first-updater manual bootstrap, signed metadata, empty-queue behavior, managed launcher/shortcut collisions and rollback; ARCHITECTURE component boundaries; RELEASE key/protocol/manual-upgrade/signature gates. Keep manual DPI/keyboard/screen-reader and clean-machine gates explicit in ACCEPTANCE. Show complete public drafts before posting.
- [ ] Run Test-UpdateTooling; dotnet restore/build Release; named tests then the full repository tests with verified tool paths; format touched files and verify solution formatting; Test-AcceptanceHarness; Build-Release/Test-Release; Test-UpdateRehearsal. Record exact final ZIP digests, build flavors, commands and actual results. Do not claim external CI or genuine manual checks ran from a local pass.
- [ ] Apply commit convention, message `Document updater operation and release validation gates`.

Use the repository root as working directory for the final commands below. Resolve `$mediaToolDirectory` to the verified pinned production tool output and set the two documented FFmpeg environment paths before running. Use fresh ignored release/rehearsal directories; do not overwrite earlier evidence. For targeted red/green cycles, use the exact test-project path and class from that task with `--filter FullyQualifiedName~<Class>`.

```powershell
dotnet restore BookSplice.slnx
dotnet build BookSplice.slnx --configuration Release --no-restore
dotnet test BookSplice.slnx --configuration Release --no-build
dotnet format BookSplice.slnx --verify-no-changes --no-restore
pwsh -NoProfile -File scripts/Test-AcceptanceHarness.ps1
pwsh -NoProfile -File scripts/booksplice/Test-UpdateTooling.ps1
pwsh -NoProfile -File scripts/Build-Release.ps1 -Version 0.2.0-ci -MediaToolDirectory $mediaToolDirectory -OutputDirectory artifacts/update-validation/package
pwsh -NoProfile -File scripts/Test-Release.ps1 -ArchivePath artifacts/update-validation/package/BookSplice-0.2.0-ci-win-x64.zip -ChecksumPath artifacts/update-validation/package/BookSplice-0.2.0-ci-win-x64.zip.sha256
pwsh -NoProfile -File scripts/booksplice/Test-UpdateRehearsal.ps1 -MediaToolDirectory $mediaToolDirectory -OutputDirectory artifacts/update-validation/rehearsal
```

Each command must exit zero with the expected test/package/scenario results. Capture its exit immediately; do not pipe away failure. The CI-flavor package above is not the later production-signed shipping artifact. Repeat only gates affected by new changes or new findings once the complete suite passes.

## Final review and completion

- [ ] Check every acceptance row in the spec against actual tests and packaged evidence. Fix missing requirements before requesting publication. Refresh live usage and private handoff around long package gates.
- [ ] Run whole-branch independent review using the execution method selected by the user. Focus on final filesystem mutation boundaries, exact signing artifact, child identity/lock transfer, settings writes and cleanup races. Address concrete findings and rerun affected verification.
- [ ] Confirm primary checkouts remain on main, unrelated edits/stash/worktrees intact, no bare gitlinks, explicit staged files only, and ignored handoff untracked. Leave concise advisory handoff while work is unfinished.
- [ ] Present complete PR draft and evidence/remaining manual gates. Wait for explicit publication approval; attach any subsequently created PR with the app artifact tool. No release authorization is implied.

## Plan self-review and execution choice

Coverage mapping: spec purpose/UI/checks -> tasks 1/2/10; authentication -> 3/12; layout/ownership -> 4/6/7/11; archive/self-check -> 4/8; queue/jobs/settings -> 5/8/9/10; handover/recovery -> 6/7/9; diagnostics -> 9/10/13; packaged validation and delivery gates -> 12/13/14/final review. All five Review Focus cases have named owning tests. Shared contract definitions precede consumers; public/private/data-root boundaries are explicit.

Recommended execution is subagent-driven, sequentially with a fresh review after each task: this plan crosses signature verification, Windows destructive file operations, process handover, and WPF state. Those boundaries merit review before subsequent tasks depend on them. Native execution is available if preferred. The user selected lower-cost subagents implementing with the controller acting as reviewer.
