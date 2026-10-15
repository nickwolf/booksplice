# BookSplice Windows updater

Date: 2026-10-15. Issue: [#77](https://github.com/nickwolf/booksplice/issues/77). Target: v0.2.0. Status: specification and implementation plan approved. Implementation is underway; version/protocol contracts and release checks are complete. Installation and handover remain unfinished.

## Purpose and scope

Users can discover a newer stable BookSplice release and install it from the Windows application without manually replacing the application folder. A failed or interrupted update must leave a working version reachable. Installation requires a click, and restart must never discard reviewed queue edits.

The implementation follows Tokitty's check, stage, self-check, side-by-side install, startup acknowledgement, and rollback sequence. It adapts that sequence to BookSplice's flat Windows x64 ZIP, WPF startup, bundled media tools, and settings contracts. Publisher signatures and exact directory ownership checks strengthen the reference flow.

Daily automatic checks are enabled by default. Manual checks remain available when automatic checks are disabled. All published stable releases are compared by semantic version precedence. Drafts, prereleases, and malformed versions are excluded. Source builds can check and open the release page but cannot install.

Installation requires an empty queue and no active application work. Queue serialization and general saved queues remain in #60 for v0.3.0. This work does not add an installer, elevation, automatic installation, automatic restart, or support for other operating systems or package architectures.

## Existing contracts

The relevant current implementation is in `src/BookSplice.Gui/App.xaml.cs`, `src/BookSplice.Gui/MainWindow.xaml.cs`, `src/BookSplice.Gui/ViewModels/MainWindowViewModel.cs`, `src/BookSplice.Gui/Bootstrap/AppHost.cs`, `src/BookSplice.Core/Settings/JsonSettingsStore.cs`, `scripts/Build-Release.ps1`, `scripts/Test-Release.ps1`, and `.github/workflows/release.yml`.

The release ZIP has files at its root. It contains the GUI, CLI, self-contained runtime, pinned FFmpeg and FFprobe, version information, README, licenses, notices, and corresponding media-tool sources. An update keeps that complete payload. It must not expect Tokitty's enclosing application directory.

Settings live outside the package in the BookSplice Local AppData directory. Corrupt settings and unsupported future schemas already block startup; the updater preserves that behavior. Reviewed metadata, chapters, artwork, ordering choices, and per-book settings currently live in memory. Each queue row has a Remove action. Conversion remains busy until the awaited conversion and cleanup complete.

The application currently has neither a stable cross-version launcher nor a cross-version GUI instance lock. Both are part of this feature. The existing release workflow produces build-provenance attestations; this specification does not treat their mere presence as verification by the desktop client.

## Components and boundaries

| Component | Responsibility |
| --- | --- |
| Update policy library | Semantic versions, release selection, manifest schema, state transitions, retention policy, and testable contracts. No WPF dependency. |
| Windows update adapter | HTTP, cryptographic verification, archive handling, Windows file identities, process handles, locks, durable files, and shortcut operations. |
| WPF update coordinator and view model | Dispatcher-owned UI state, manual and scheduled checks, queue deferral, progress, cancellation, and handover participation. |
| Stable launcher and recovery coordinator | Resolve the committed application, supervise a handover, reconcile an interrupted transaction, and expose rollback without requiring the candidate to start. |
| Release tooling | Generate and sign the manifest, package the launcher, and rehearse exact packaged upgrades and rollback. |

The implementation plan will choose project and file boundaries within these responsibilities. Existing pure conversion policies in `BookSplice.Core` do not acquire HTTP, WPF, or process-launching duties. Existing result and progress behavior is outside this change.

## Check schedule and user interface

Add a Check for updates action and an Updates section with the running version, last successful check, daily-check preference, available version, and release notes. Display notes as bounded plain text; a release-page link opens only the validated repository release URL. Do not render executable HTML or arbitrary embedded links.

The first automatic check runs 60 seconds after startup when no successful check exists or its timestamp is at least 24 hours old. Otherwise it is due 24 hours after the last success. A future timestamp is treated as invalid and schedules a check after 60 seconds. Checks use UTC timestamps and an injectable clock. A manual request joins any in-flight check. There is only one network check in flight.

A successful check schedules the next check 24 hours later. Network failures retry after one hour. Rate-limit responses honor a valid Retry-After or reset time when that is later. Automatic failures update the Updates section without a modal dialog; manual failures show a clear retryable result. Cached availability is advisory and is refreshed before installation. Disabling automatic checks cancels future scheduled requests without disabling manual checks.

Automatic discovery presents one nonmodal notice per version. The confirmation shows the installed and available versions, notes, installation location, and the managed launch shortcut behavior. Its actions are Install and restart, Later, and Open release page. A failed availability or trust check never appears as Up to date.

Install and restart is unavailable while any queue row remains, including completed or failed rows. Explain: "Remove queued books before installing. Reviewed edits remain in this window until you remove them." Do not remove rows, discard edits, cancel jobs, or schedule a surprise restart on the user's behalf. The user empties the queue and clicks Install and restart again. Download cancellation is available before handover begins. Once handover starts, report that restart is in progress and prevent competing close or launch actions until it succeeds or recovery finishes.

Controls have keyboard access, automation names, visible focus, and polite progress announcements. Unit tests do not stand in for real DPI, keyboard, and screen-reader checks.

## Release selection and transport

List releases from the fixed `nickwolf/booksplice` GitHub API endpoint with 100 entries per page. Follow same-endpoint pagination, with a maximum of 10 pages. If pagination cannot complete within that bound, fail the check explicitly rather than claim to have found the maximum from an incomplete list. Do not use `releases/latest` or publication order to select a version.

Parse standard semantic versions from `v`-prefixed tags. Exclude prerelease versions even if the release's prerelease flag is false. Compare major, minor, and patch numerically; build metadata does not alter precedence. If distinct highest-precedence releases are ambiguous, display the ambiguity and refuse installation. The highest release without usable signed assets is reported as available but not installable; do not silently select an older release instead. Normal update installation never downgrades.

Require the exact expected asset names derived from the selected version. Duplicate asset names are an error. Bind their GitHub asset and release identities during the confirmation and refresh them before staging; if the release or asset changes, require a fresh confirmation. No user GitHub credential is needed for public checks.

Production accepts HTTPS only, uses normal Windows certificate validation, rejects URL credentials, and validates each redirect before following it. API requests remain on `api.github.com`; release asset URLs initially name the exact repository, tag, and asset. Asset redirects may go only to explicitly allowed GitHub asset-delivery hosts, initially `release-assets.githubusercontent.com` and `objects.githubusercontent.com`. Never forward authorization headers across hosts. Unsupported redirect destinations fail visibly.

Policy limits are 10 seconds per API request, 60 seconds without download progress, and 30 minutes for the complete staging operation. Bound each release-list page to 2 MiB, notes to 256 KiB, manifest to 1 MiB, signature envelope to 16 KiB, and downloaded ZIP to 1 GiB. Bounds are implementation policy, not measurements. Changes to them require tests. Cancelled or interrupted downloads are discarded rather than resumed in v0.2.0.

Production packages have no environment variable that can replace the release endpoint, trust key, expected version, self-check result, or acknowledgement. Rehearsal transport and fault injection belong to an explicitly marked test build with a separate test data root and disposable trust key.

## Package integrity and publisher authentication

Use a detached RSA-PSS signature with SHA-256, MGF1-SHA-256, a 32-byte salt, and a 3072-bit release key. The trusted public key is embedded in the updater-capable package. The private key is provisioned separately in a protected GitHub release environment and is never committed or included in artifacts. CI must not substitute a development key when the production key is absent.

Publish `BookSplice-<version>-win-x64.update.json` and `BookSplice-<version>-win-x64.update.sig.json` beside the ZIP and existing checksum. The signature envelope contains a schema version, key ID, fixed algorithm identifier, and base64 signature. A key ID is the lowercase SHA-256 hex digest of the public key's DER SubjectPublicKeyInfo. The signed message is the ASCII text `BookSplice update manifest v1`, one LF byte (`0x0A`), then the exact UTF-8 manifest bytes. Do not parse and reserialize before signature verification. Accept only the specified algorithm, strict UTF-8, one JSON object, known schema, and unique property names. Reject unknown key IDs and unknown schema fields in version 1.

The authenticated manifest contains these fields:

| Field | Required binding |
| --- | --- |
| Schema and product | Schema 1 and BookSplice product identity |
| Repository | Exactly `nickwolf/booksplice` |
| Tag and version | Selected tag and corresponding stable semantic version |
| Target | Exactly `win-x64` |
| Archive name, length, SHA-256 | Exact expected ZIP identity and digest |
| Package protocol | Launcher/update protocol 1 |
| Settings compatibility | Minimum and maximum supported existing settings schema |

Verify the manifest signature before extraction or candidate execution. Stream the ZIP with exact length and SHA-256 checks against the signed manifest. The companion checksum and GitHub asset size must agree, but neither substitutes for the signature. Check the package's embedded version and process architecture again after extraction and during self-check.

This authenticates authorization by the release signing key. It does not protect against compromise of that key or the initially installed updater, nor against arbitrary code already executing as the user. Preserve GitHub build provenance as additional release evidence. A checksum and ZIP fetched from the same source are not sufficient publisher authentication.

The first updater-capable release requires one manual installation. Production key provisioning and a signature-verified exact-package rehearsal are shipping gates. Key rotation must be delivered through an artifact trusted by the existing key or a separately verified manual installation; runtime downloads cannot replace trust roots. Automated key rotation is outside protocol 1.

The release workflow signs only the exact ZIP that passed package gates, verifies the resulting signature using the embedded production public key, and attaches all matching assets to the unpublished draft. A signing step must not overwrite an existing same-named release asset. Do not publish a ZIP without its matching signed metadata.

## Installation layout and ownership

Create a random managed root beside the initially running extracted application directory. Its parent must be a local writable filesystem supporting atomic same-volume renames and Windows file identities. Refuse unsupported or read-only locations with a manual-download option; do not elevate or silently change the destination.

```text
<parent>/
  <original extracted application>/
  .booksplice-managed-<installation-id>/
    BookSplice.Launcher.exe
    install.json
    current.json
    transaction.json
    ownership.json
    versions/<version>-<transaction-id>/<complete flat package>
    staging/<transaction-id>/
    trash/<transaction-id>/
```

The original extracted application remains unowned and is never deleted. It is the first rollback target. New releases live beside it beneath the managed root. A destination must be created exclusively; an existing directory is neither adopted nor overwritten, even when its contents look correct. Record ownership only after successful creation. A crash before recording may leave an orphan; recovery leaves that orphan untouched.

`install.json` binds the installation ID, original executable identity, managed root, and data root. `current.json` contains the committed application identity and previous working target. It initially points at the original version. Transaction and ownership records use versioned schemas, bounded parsing, write-through temporary files, and same-volume atomic replacement. A parse or durability error stops the update and disables destructive cleanup. Never erase a pending transaction just because it is old.

Updater preferences and scheduling metadata live under `<BookSplice data root>/updates/` separately from conversion settings. A locked installation registry maps exact original and managed executable paths to the managed root. Multiple manually extracted copies are not silently combined into one installation. The GUI instance lock is scoped to the canonical BookSplice data root, so two copies cannot concurrently edit that root through the GUI.

Copy the stable launcher from the currently trusted installed package into the new managed root and record its identity. It is independent of candidate-version directories and is not removed by version cleanup. Protocol 1 does not replace a running launcher; packages requiring a newer launcher protocol are unavailable for automatic installation and direct users to a manual upgrade.

On the first successful update, create a per-user Start Menu shortcut targeting the stable launcher if the target shortcut path is absent. Future updates may repair only that recorded shortcut while its identity and target still match. An unowned collision is left intact and the result shows the managed launcher location. Never rewrite arbitrary desktop shortcuts or pinned items. Updater-capable direct GUI launches consult the installation registry and forward only to its committed version after validating that mapping. An original pre-updater executable cannot be redirected retroactively and remains usable.

## Archive validation and self-check

Validate every ZIP entry before writing any payload. Reject absolute paths, traversal, drive and UNC paths, alternate data streams, device names, NUL characters, trailing spaces or dots, Windows-equivalent duplicate names, file/directory collisions, links, and reparse entries. Reject path components that escape the staging root after Windows normalization. Do not follow pre-existing reparse points in destination ancestry.

Extract at most 20,000 entries, at most 1 GiB per file, and at most 4 GiB in total. Enforce actual streamed output limits, not only declared ZIP sizes. Check available disk space before staging and handle disk-full errors throughout. A signed archive still has to pass these checks.

Validate the complete package layout and required executables, runtime, licenses, notices, sources, and version manifest. Record a payload inventory from the verified extraction. Reject unexpected special files and verify that promoted payload identity and inventory have not changed before launch. Do not copy just the GUI or reuse tools from the old release.

Add a hidden packaged self-check mode with a versioned, machine-readable result, bounded output, nonzero failure exit status, and a 60-second deadline. The parent requires both a zero exit code and every named mandatory check to pass. Mandatory checks include exact product/version/protocol, x64 process architecture, complete payload inventory, GUI/runtime initialization, a rendered disposable WPF window, bundled FFmpeg and FFprobe identity/capabilities, and settings schema compatibility. A hidden or never-rendered window is not a pass.

Use an isolated scratch data root and a copy of the current settings for the self-check. Never run first-launch setup or write real settings during it. Remove inherited media-tool overrides and data-root overrides from candidate environments, then supply only the transaction's validated data root and explicit check mode. Use bundled media tools. A failed or hung self-check stops before handover.

## Queue, jobs, and settings safety

A Dispatcher-owned operation gate tracks queue rows, analysis, preview, conversion, cancellation cleanup, pending asynchronous additions and cover reads, settings writes, modal dialogs, and update activity. Counts are registered before asynchronous work starts and released only after final cleanup. An empty collection alone does not prove quiescence.

At the install click, acquire exclusive update intent, then recheck empty queue and zero active work atomically. Freeze add/drop, edit, settings, preview, and conversion entry points for the staging and handover interval. Deferred callbacks must recheck the gate before starting work. Failure and cancellation release the gate and restore the UI. Do not stage while active jobs or cleanup are running.

Packaged GUI and CLI jobs participate in a cross-process activity lease under the same data root. Multiple jobs can hold shared leases; updating requires an exclusive lease. New operations cannot begin while update intent is reserved. Locks are acquired in one documented order: update coordinator lock, exclusive activity lease, then short state-file locks. No process waits for the GUI instance lock while holding a state-file lock. Previously released binaries cannot participate in the new lease; their unowned application directories remain untouched.

Before handover, flush completed settings writes and record the settings file's bytes/hash, or its verified absence. Candidate compatibility must include the current settings schema. For protocol 1, automatic upgrades and rollback must read that schema without migration. An incompatible package is refused for automatic installation. Candidate readiness reads a snapshot and leaves real settings untouched. Normal settings editing is enabled only after transaction completion and old-process exit. Revalidate the settings hash before commit; unexpected changes abort handover rather than being overwritten.

Update preferences are persisted atomically in their own file and do not change conversion defaults. Queue emptiness means no reviewed edits need restoration after restart. Retain completed results in the old UI until the user deliberately removes their rows.

## Handover and crash recovery

The stable launcher runs as a separate transaction coordinator and supervises both GUI processes. It holds the update and exclusive activity leases through transfer. Use a current-user-only named pipe and an unpredictable 256-bit transaction nonce for IPC. Bind messages to transaction ID, expected executable identity/version, and recorded process ID plus start time. Keep process handles; do not identify a process by PID alone. An acknowledgement file existing at a guessed path is insufficient.

| Phase | Durable state and behavior |
| --- | --- |
| Preparing | Current pointer still names the old app. Create and record staging, authenticate, extract, and self-check. Cancellation is allowed. |
| Prepared | Promote to a new exclusive version directory and record its identity, old target, settings hash, protocol, nonce, and process identities before launching it. |
| Starting | Old GUI freezes and hides, releases the GUI instance lock under the reserved handover, and stays alive. Candidate obtains that lock in restricted startup mode. |
| Ready | Candidate reports that the real main window has rendered, Dispatcher responds, settings are readable, bundled tools initialized, and the expected lock is held. It remains read-only. |
| Committing | Coordinator revalidates the child is alive, settings unchanged, and target identities valid, then journals commit intent and atomically changes current/previous pointers. |
| Committed | Coordinator permits old GUI exit. Only after old-process exit is observed does it release the candidate for ordinary work and release the exclusive activity lease. |
| Rolling back | Stop and observe exit of the exact candidate, restore old pointer if changed, let the old GUI reacquire its instance lock and restore its window, then record the failed transaction. |

Allow 60 seconds for candidate readiness. Require the expected main window to be visible and a completed WPF render plus a subsequent Dispatcher round trip. A process launch, `Show()` call, offscreen self-check, or timer tick alone is not readiness. Late acknowledgements from aborted transactions are rejected. If the candidate cannot be stopped, keep work frozen and report recovery failure; do not run both versions as active writers.

Recovery reads the journal before resolving a launch target. A phase before committed success restores the old mapping and stops any positively identified candidate before launching/resuming the old app. If the coordinator died after pointer replacement but before a durable committed record, treat the candidate as provisional and roll back. A candidate that loses its coordinator before completion exits restricted mode without writing settings. A committed record with the old process still present finishes the supervised exit before enabling work. Recovery never infers success from elapsed time, candidate presence, or version ordering.

If current metadata is corrupt, the launcher may use only an independently validated recorded previous target or original target. If neither can be validated, show recovery instructions and leave all directories untouched. No arbitrary path from malformed metadata may be executed.

Keep the prior working version after success. Updates exposes Roll back to previous version with the same empty-queue gate, identity checks, protocol/settings compatibility, startup acknowledgement, and commit/recovery sequence. The explicit rollback operation selects the recorded prior working payload; downloaded updates remain subject to anti-downgrade checks. Automatic handover and rollback require protocol 1 at both ends. Older binaries without IPC support are manual recovery targets only. The initial updater-capable release already implements protocol 1. Retain its original extracted folder as a manual recovery target even after later updates.

## Cleanup policy

Ownership records contain exact canonical path, kind, installation and operation IDs, volume/file identity, and an updater-created marker. Validate the managed root and all relevant ancestry, including reparse-point refusal, before mutations. Use handle-based Windows identity checks and prevent directory replacement during each destructive operation. Markers or directory names alone do not establish ownership.

Keep the stable launcher, running/current application, recorded previous working application, original extracted application, and every live transaction path. Other versions are eligible only after a successful committed update and no active jobs or update transaction. An aborted transaction may discard its own recorded staging directory after all its workers and children exit, using the same ownership and identity checks. Refuse cleanup when ownership, state, ancestry, or file identity cannot be verified.

Before deletion, record the exact unique trash destination and source identity durably, rename without replacement, then verify the destination has the same recorded identity. Traverse without following reparse points. Retry only that recorded trash identity after interruption. An unrelated folder at the exact same derived trash name is still unowned and must survive. Do not glob for update/version/trash prefixes. Retain records on partial deletion, file-in-use failure, or failed rename so later cleanup can retry safely. Never partially dismantle a retained working version.

## Errors and diagnostics

Represent checking, available, deferred, downloading, verifying, preparing, restarting, complete, cancelled, rolled back, and failed as distinct states. Report the stage and practical next action. Never label a rollback as successful installation. The old app remains usable after pre-handover failures. Recovery failures identify the retained old executable/launcher locally without claiming recovery succeeded.

Persist bounded transaction diagnostics under the ignored/local application data root. Public copied diagnostics use the existing path-redaction policy and omit private queue/media metadata, tokens, nonce material, and signing secrets. Updater output must not imply that conversion validation or full audio decode ran.

## Verification and acceptance

| Area | Required evidence |
| --- | --- |
| Release checks | Numeric precedence, build metadata ties, draft/prerelease rejection, pagination completion/failure, duplicates, missing signed assets, future clocks, daily/manual coalescing, offline and rate-limit retry. |
| Trust | Valid signature, changed manifest, wrong key, missing signature, algorithm/schema confusion, duplicate fields, wrong repository/version/target/name/length/hash, and asset replacement after confirmation. |
| Archive/staging | Corruption, truncation, cancellation, disk full, unsafe Windows paths, duplicate/colliding names, link/reparse ancestry, expansion bounds, existing destination, crash before ownership recording, and payload change before launch. |
| Quiescence | Reviewed edits keep installation deferred; analysis, preview, queued additions, settings writes, conversion cancellation, and cleanup remain protected; new work cannot slip between the final check and gate acquisition; CLI activity blocks handover. |
| Self-check | Wrong embedded version/architecture, missing tools/runtime, tool override rejection, nonzero exit with valid JSON, malformed or partial reports, timeout, and no rendered window. |
| Handover | Correct process/nonce binding, wrong and stale acknowledgement, child exit before/after readiness, invisible/hung child, old lock transfer, late callbacks, and coordinator/old/candidate death at every durable boundary. |
| Settings | Exact bytes preserved on success and rollback, corrupt/future schema refusal, incompatible candidate refusal, changed settings during readiness, and no candidate write before completion. |
| Cleanup | Unowned original/version/trash folders survive; replacement at formerly owned paths survives; exact trash identity checked; running/previous/pending versions retained; partial deletion retried; locked files and corrupt state fail safely. |
| Launch paths | Managed shortcut updates only after success, collision preserves unowned shortcut, direct-launch forwarding uses committed mapping, launcher recovery works when candidate will not run, and explicit rollback remains reachable. |

Unit tests use injected clocks, transports, filesystem/process adapters, and fault points. Integration tests exercise real Windows file identities, atomic replacements, locks, named pipes, process handles, and reparse behavior. Fault tests assert surviving files, committed launch mapping, settings bytes, visible usable application, and exclusive-writer behavior, not only expected method calls.

Add a packaged Windows rehearsal that builds two distinct versions with generated data, an isolated settings root, a disposable signing key, and a local test transport in the marked test build. Test successful upgrade, interrupted download, interrupted handover with coordinator death, failed-start rollback, wrong-version rejection, and unowned-path survival. Exercise the actual stable launcher and WPF render acknowledgement. Require one usable GUI after success or recovery and no stranded test processes. Run a generated-audio conversion with the candidate's bundled tools after success; no private media is required.

Existing build, unit tests, formatting, generated-fixture/package smoke, acceptance-harness safeguards, and staged Gitleaks remain required for the implementation. Test the exact final ZIP and signed manifest together. A different archive invalidates that package's prior evidence. Perform the existing Windows clean-machine release gate against the final candidate. Record unperformed manual GUI/DPI/keyboard/screen-reader checks explicitly.

## Delivery gates

The next artifact is an implementation plan after approval of this written specification. Implementation may be divided into trust/check policy, Windows staging/ownership, launcher/recovery, WPF integration, and packaged rehearsal, but all acceptance paths above are required for #77.

Local implementation and disposable-key rehearsals do not authorize production signing-key provisioning, a public PR, merge, tag, or release. Show complete public drafts before posting. Before shipping, provision the production key, embed its reviewed public key, exercise the exact signed production package, and record genuine manual checks separately from automated evidence.

## References

- [Tokitty updater design](https://github.com/nickwolf/tokitty/blob/main/docs/superpowers/specs/2026-10-02-auto-update-design.md), plus its current `updater.py`, `update_check.py`, `update_install.py`, `update_controller.py`, `update_swap.py`, updater tests, and `freeze/verify_update.py`.
- [.NET RSA signature verification](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.verifydata?view=net-10.0).
- [GitHub artifact attestations](https://docs.github.com/en/actions/concepts/security/artifact-attestations).
- [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
