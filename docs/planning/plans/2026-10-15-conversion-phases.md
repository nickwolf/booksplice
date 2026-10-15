# Conversion phases implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Show actual conversion phases and evidence-based progress, explain overdue activity, and preserve cancellation, retries, CLI compatibility, and terminal results.

**Architecture:** Extend the shared progress record with phase measurements and an ordered service envelope. Producers report scoped operations; a presentation-neutral observer evaluates freshness and estimates. GUI and CLI format the same observations while the service result remains the only terminal authority.

**Tech stack:** C#/.NET 10, WPF, xUnit, FFmpeg machine progress, PowerShell, Gitleaks. No new production package is required.

**Spec:** [Approved design](../specs/2026-10-15-conversion-phases-design.md). Read it before this plan. Paths below are relative to the dedicated BookSplice worktree.

## Global constraints

- Work in `C:\Tools\booksplice\.worktrees\real-conversion-phases` on `codex/real-conversion-phases`; preserve both primary main checkouts, their dirty files, and other worktrees.
- No synthetic whole-job percentage or fixed phase weights are introduced.
- A phase entry describes an operation being entered; it does not prove that the operation succeeded or that a child process started.
- Only a successful service result produces the final completed display.
- Use a rolling 30-second sample window after at least 3 seconds of observation. Hide ETA when the last numeric advance is more than 10 seconds old.
- The 30-second threshold is an advisory constant, not a timeout, and applies at the boundary inclusively.
- Missing duration evidence must not newly prevent an otherwise supported chapter-disabled conversion.
- Preserve schema version 1 event names, legacy execution fields, final exit/status/publication semantics, and #56 validation/result semantics.
- No private media, updater work, automatic watchdog termination, or new threshold configuration UI.
- Public publication, merge, tags, and releases require their existing approvals. Local commits are permitted.

## Review focus

- A process emits `progress=end` and then exits unsuccessfully: no successful segment completion or later speculative phase (Task 4).
- A slow dispatcher receives queued telemetry after work has moved on: old sequence/phase/attempt events cannot revive stale status or overwrite the result (Tasks 2 and 6).
- A source duration is missing, zero, overflowing, or mismatched to source order: conversion remains supported where previously supported, while numeric progress stays unavailable (Tasks 3 and 4).
- A timer races with terminal output or an interactive decision: no progress after CLI `final`, no warning while waiting for input (Task 7).
- A user cancels while synchronous metadata work, cleanup, or atomic publication is settling: dispatcher remains responsive and the eventual outcome reflects the actual published result (Task 6).

## File map and execution order

Run Tasks 1 through 8 in order. They share contracts and are best implemented serially. Each code task follows red, green, then commit. Commit only explicit owned paths; do not stage private handoffs or unrelated changes.

| Area | Files and responsibility |
| --- | --- |
| Progress contracts | Modify `src/BookSplice.Core/Execution/ExecutionContracts.cs`; create `ConversionPhaseScope.cs`, `ConversionProgressSession.cs`, and `ConversionProgressObserver.cs` in that directory |
| Orchestration | Modify `src/BookSplice.Core/Analysis/ConversionService.cs` for service phases and operation-scoped forwarding |
| Timing evidence | Modify `src/BookSplice.Core/Planning/ConversionPlan.cs` and `ConversionPlanner.cs`; create `SourceDurationEvidence.cs` beside them |
| Execution | Modify `src/BookSplice.FFmpeg/Execution/FFmpegConversionExecutor.cs`; extract its nested normalizer into `ConversionProgressNormalizer.cs` in the same directory |
| Validation | Modify `src/BookSplice.Core/Validation/ValidationContracts.cs` and `src/BookSplice.FFmpeg/Validation/FFmpegOutputValidator.cs` |
| GUI | Modify `src/BookSplice.Gui/ViewModels/ConversionProgressDisplay.cs`, `BookQueueItemViewModel.cs`, `MainWindowViewModel.cs`, and `src/BookSplice.Gui/MainWindow.xaml` |
| CLI | Modify `src/BookSplice.Cli/CliApplication.cs`; create `src/BookSplice.Cli/CliProgressDisplay.cs` |
| Documentation | Modify `README.md` for phase progress and additive JSON fields; update this plan's checkboxes as work completes |

Before Task 1, run and record the baseline commands in Task 8 against committed product code. Restore precedes every `--no-restore` build in a fresh worktree. Baseline validation is execution setup, not a claim that tests have run during planning.

## Task 1: Define phase events and the observation model

**Files:** Modify `ExecutionContracts.cs`; create `ConversionProgressObserver.cs`; create `tests/BookSplice.Core.Tests/Execution/ConversionProgressObserverTests.cs`.

**Interfaces produced:**

- `enum ConversionPhase { Analysis, Planning, Preparation, Encoding, StreamCopy, Assembly, MetadataWriting, ValidationChecks, FullDecode, Publication, Cleanup, AuditWriting }`.
- Append optional arguments to `ConversionProgress`: `ConversionPhase? Phase = null`, `Guid? PhaseOccurrenceId = null`, `bool IsPhaseEntry = false`, `double? PhasePercent = null`, `double? PhaseSpeed = null`, `bool ExpectsMeasurableUpdates = false`, `int? CompletedSources = null`, `int? TotalSources = null`, `Guid? JobId = null`, `long? Sequence = null`. Preserve all six existing positional arguments and their meanings.
- `enum ProgressAdvisory { None, NoUpdate, NoAdvance }` and immutable `ConversionProgressSnapshot` with phase/occurrence, nullable percent/speed/remaining, completed/total counts, total elapsed, last-report age, last-advance age, advisory, and retained job/sequence.
- `ConversionProgressObserver`: `bool Report(ConversionProgress value, TimeSpan elapsed)` and `ConversionProgressSnapshot Snapshot(TimeSpan elapsed)`. Adapters supply monotonic elapsed time; snapshot reads never count as reports. Reject wrong-job, nonincreasing-sequence, and retired-occurrence events. Allow legacy null-envelope events for old test doubles, but never infer whole-job completion from them.

- [ ] Write fake-clock tests with these exact boundary assertions: `Snapshot(29.999s).Advisory == None`, silence at `30s` yields `NoUpdate`, repeated measured reports through `30s` yield `NoAdvance`, ETA is available with adequate advancing samples at age `10s` and unavailable at age `10.001s`. Assert phase change clears samples, advisory and percentage; NaN/infinity/negative measures never appear as numbers; recent samples recover without reusing the pre-stale rate. Assert numeric-unavailable phases do not invent a no-advance advisory.

```csharp
[Fact]
public void SnapshotTicksDoNotPostponeSilenceAdvisory()
{
  var observer = new ConversionProgressObserver();
  observer.Report(new(null, null, null, 0, Phase: ConversionPhase.MetadataWriting,
    PhaseOccurrenceId: Guid.NewGuid(), IsPhaseEntry: true), TimeSpan.Zero);
  for (var second = 1; second < 30; second++)
    Assert.Equal(ProgressAdvisory.None, observer.Snapshot(TimeSpan.FromSeconds(second)).Advisory);
  Assert.Equal(ProgressAdvisory.NoUpdate, observer.Snapshot(TimeSpan.FromSeconds(30)).Advisory);
  Assert.Null(observer.Snapshot(TimeSpan.FromSeconds(30)).Percent);
}
```
- [ ] Run `dotnet test tests/BookSplice.Core.Tests --configuration Release --filter FullyQualifiedName~ConversionProgressObserverTests`; expect missing-type compilation failure initially.
- [ ] Implement these types. Keep numeric advancement separate from telemetry receipt, with rolling samples and the exact spec thresholds. Clamp finite percentage into 0..100; a malformed measurement does not advance the clock. Reset ETA samples after an interval with stale numeric evidence. Store the last accepted phase measurement through a no-update interval. Hide phase speed when its evidence is stale.
- [ ] Re-run the command; require all selected tests passing. Add a regression asserting a snapshot call at every second cannot postpone the advisory.
- [ ] Stage the three owned files, run staged Gitleaks, and commit `feat: define phase progress observations`.

## Task 2: Scope producer events and instrument service operations

**Files:** Create `ConversionPhaseScope.cs`, `ConversionProgressSession.cs`; modify `ConversionService.cs`; extend `tests/BookSplice.Core.Tests/Analysis/ConversionServiceTests.cs`; create `tests/BookSplice.Core.Tests/Execution/ConversionProgressSessionTests.cs`.

**Interfaces produced:**

- `ConversionPhaseScope.Start(IProgress<ConversionProgress>? sink, ConversionPhase phase, bool expectsMeasurableUpdates = false)` returns an `IDisposable, IProgress<ConversionProgress>` scope. It generates an occurrence ID and immediately reports a phase-entry event. `Report` stamps its phase and occurrence; disposed scopes discard reports. Do not create a new occurrence for each measurement.
- `ConversionProgressSession(Guid jobId, IProgress<ConversionProgress>? sink)` implements `IDisposable`. `OpenOperation()` returns an `IDisposable, IProgress<ConversionProgress>` operation sink; opening a new operation closes prior forwarding. Serialize delivery and sequence increments under one lock. The session stamps job/sequence, tracks active and retired phase occurrences, and rejects retired callbacks before stamping. Disposal closes every sink before returning.
- Service calls keep the existing `IConversionService.ConvertAsync` signature. Pass the session's operation sink to the executor and, after Task 5, validator. Use `ConversionPhaseScope` for service-owned entry events. Keep `ConversionStage` and terminal result stages unchanged.

- [ ] Add `ServicePhasesFollowActualOperations`, `DryRunStopsAtPlanning`, `LateExecutorCallbacksAreDiscarded`, and `TerminalReturnClosesAllSinks` tests capturing events and operation boundaries in fakes: analysis precedes planning; execution `completed` precedes the validator invocation and never becomes terminal success; analysis failure has no execution, validation, or publication event; dry run stops at planning without audit; validation failure announces cleanup and audit but no publication. Retain an executor sink and report after its return: assert event count and sequence unchanged. Repeat for disposed phase scopes, same-phase re-entry, and terminal return. Check sequences are increasing under concurrent reports. Until Task 5 instruments the validator, verify its invocation boundary from the fake's call log rather than requiring a validator-owned event prematurely.
- [ ] Run Core tests filtered to `ConversionServiceTests|ConversionProgressSessionTests`; expect the new assertions to fail before implementation.
- [ ] Wire analysis/planning, publication, service cleanup, and audit entry at their actual call boundaries. Wrap executor and validator forwarding in operation lifetimes, and close them before later work. Share the existing service `jobId` with events and results. Dispose the whole session in every return/catch path. Do not add an execution placeholder phase that conflicts with executor-owned phases; the operation sink itself is silent until the producer enters an actual phase.
- [ ] Re-run both filtered groups and existing service terminal-status tests. Require preserved diagnostics, cleanup, audit-warning behavior, and no callbacks after return.
- [ ] Stage only the owned files, run staged Gitleaks, and commit `feat: report scoped conversion service phases`.

## Task 3: Freeze source duration evidence independently of chapters

**Files:** Modify `ConversionPlan.cs`, `ConversionPlanner.cs`; create `SourceDurationEvidence.cs`; extend `tests/BookSplice.Core.Tests/Planning/ConversionPlannerTests.cs`.

**Interfaces produced:** Append `IReadOnlyList<long?>? sourceDurationsMicroseconds = null` after the current final `ConversionPlan` constructor argument. Expose an immutable `IReadOnlyList<long?> SourceDurationsMicroseconds` matching `SourcePaths.Count`; omitted, invalid, or mismatched evidence normalizes to null entries, never guessed weights. `SourceDurationEvidence.ReadMicroseconds(SourceFile file)` returns a nullable positive `long`, using positive container duration then one positive audio-stream duration, checked decimal conversion, and the chapter planner's rounding policy. `SourceDurationEvidence.TotalMicroseconds(IReadOnlyList<long?> durations)` returns null for incomplete evidence or checked-sum overflow.

- [ ] Add `ChapterDisabledPlanRetainsOrderedDurations`, `PlanCopiesDurationEvidence`, and `InvalidDurationDoesNotRejectSupportedPlan` tests asserting that chapters disabled still freezes unequal source durations in source order; mutating constructor input cannot mutate a plan; absent/zero/unrepresentable duration and cumulative overflow yield unavailable progress evidence. Assert planning status is unchanged for otherwise-supported chapter-disabled input.
- [ ] Run `dotnet test tests/BookSplice.Core.Tests --configuration Release --filter FullyQualifiedName~ConversionPlannerTests`; expect missing properties/assertion failures.
- [ ] Implement immutable evidence and planner population without changing chapter entries, existing size checks, or duration validation policy. Existing hand-built plans remain constructible with omitted evidence; update only test fixtures that deliberately need measurable phase progress.
- [ ] Re-run planner and chapter tests; require all passing and source ordering preserved.
- [ ] Stage owned files, run staged Gitleaks, and commit `feat: retain source timing for phase progress`.

## Task 4: Report executor phases and aggregate parallel measurements

**Files:** Modify `FFmpegConversionExecutor.cs`; create `ConversionProgressNormalizer.cs`; extend `tests/BookSplice.FFmpeg.Tests/Execution/FFmpegConversionExecutorTests.cs`; create `tests/BookSplice.FFmpeg.Tests/Execution/ConversionPhaseProgressTests.cs`; adapt `tests/BookSplice.Gui.Tests/ConversionProgressDisplayExecutionTests.cs` in Task 6.

**Interfaces consumed:** Phase scopes and plan timing evidence. **Produced:** Internal normalizer with constructor `(ConversionPlan plan, IProgress<ConversionProgress>? sink, TimeProvider? timeProvider = null)`; `IDisposable BeginPhase(ConversionPhase phase)`, `IProgress<string> ForProcess(int stageIndex, int? sourceIndex)`, `void CompleteProcess(int stageIndex, int? sourceIndex)`, and `void CompleteExecution()`. Existing parser stays unchanged. Inject `TimeProvider` as an optional executor constructor argument for deterministic rate tests, preserving existing call sites.

- [ ] Add `ParallelSegmentsUseDurationWeightedBookProgress`, `UnknownDurationsRemainIndeterminate`, `FailedEndDoesNotCompleteSegment`, and `RetiredProcessCannotUpdateAssembly` controlled-runner tests, plus a phase-order theory over all audio strategies. Use durations 10s and 30s, advances 5s and 15s: assert encoding `PhasePercent == 50`, regardless of callback interleaving. Include 33 chapter-disabled sources, three concurrent workers, unavailable evidence, backwards/excessive timestamps, unequal reported segment speeds, late callbacks after assembly begins, and `progress=end` followed by nonzero exit. Assert no assembly/metadata phase after encoding failure; completed counts increment only on successful exit. Assert encoding phase reaching 100 does not send a whole-job completion event.
- [ ] Run FFmpeg tests filtered to `FFmpegConversionExecutorTests|ConversionPhaseProgressTests`; expect new phase/measurement failures.
- [ ] Extract normalization. Maintain processed time per source and aggregate speed over monotonic elapsed time, independent of legacy per-process fields. Close phase and per-process callback lifetimes at transition/exit. Emit preparation, actual encoding/stream-copy/assembly, metadata-writing, and executor-owned failure-cleanup entries. Call `CompleteProcess` only after checking exit code; parsed `end` is telemetry, not success. Keep the legacy capped execution display calculation separate so CLI `displayPercent` semantics do not become phase-local. Keep legacy `completed` as execution-only, issued after metadata writing.
- [ ] Run both test groups and existing parser tests. Require unchanged command construction, failure diagnostics and workspace cleanup safety. Do not alter parallel scheduling or sibling-cancellation behavior as unrelated work.
- [ ] Stage owned files, run staged Gitleaks, and commit `feat: measure actual audio conversion phases`.

## Task 5: Instrument validation without changing validation evidence

**Files:** Modify `ValidationContracts.cs`, `FFmpegOutputValidator.cs`, `ConversionService.cs`; update validator fake in `tests/BookSplice.Core.Tests/Analysis/ConversionServiceTests.cs`; create `tests/BookSplice.FFmpeg.Tests/Validation/ValidationProgressTests.cs`; retain existing validator tests and real-media integration tests.

**Interface change:** `IOutputValidator.ValidateAsync(ConversionPlan plan, string temporaryOutputPath, CancellationToken cancellationToken, IProgress<ConversionProgress>? progress = null)`. Migrate the production implementation and every implementing fake. Existing three-argument callers remain valid. Add optional `TimeProvider? timeProvider = null` to `FFmpegOutputValidator`'s constructor only for measured-rate testability.

- [ ] Add `ChecksEnterBeforeProbe`, `LightweightSkipsFullDecodePhase`, `FullDecodeUsesOutputDurationWithoutChapters`, and `FullDecodePercentDoesNotProveValidationPassed` tests asserting validation-checks entry precedes file/probe access; lightweight validation emits no full-decode phase; early parse failure emits no full-decode phase; full decode uses valid output duration with chapters disabled; missing duration gives null percent/ETA; decode reporting 100% but exiting unsuccessfully still fails `decode.full`. Retain standard-error diagnostics and warning assertions from full-decode fault tests.
- [ ] Run FFmpeg tests filtered to `ValidationProgressTests|FFmpegOutputValidator`; expect missing signature and phase assertions to fail.
- [ ] Add `-progress pipe:1 -nostats` to the full-decode command while preserving its log level, audio mapping, output sink, and error checks. Forward parsed processed time and a phase-local measured rate using valid output duration, through disposable phase/per-process scopes. Report check-boundary activity without claiming periodic numeric telemetry. Wire the service's validation operation sink. Full decode being measurable does not make all validation checks measurable.
- [ ] Run the filtered tests, `ResultValidationIntegrationTests`, and `CoverPayloadValidatorIntegrationTests` with verified media tools. Require unchanged check codes, required flags, warnings, errors, and validation failure exit mapping.
- [ ] Stage owned files, run staged Gitleaks, and commit `feat: report validation phase measurements`.

## Task 6: Present phases and preserve responsive GUI attempts

**Files:** Modify the four GUI files from the file map; extend `tests/BookSplice.Gui.Tests/ConversionProgressDisplayTests.cs`, `ConversionProgressDisplayExecutionTests.cs`, `QueueTests.cs`, and `WindowTests.cs`; create `tests/BookSplice.Gui.Tests/ConversionPhaseQueueTests.cs`.

**Interfaces produced:** `ConversionProgressDisplay` wraps the shared observer, retaining `Report(ConversionProgress progress, TimeSpan elapsed)`, `Describe(TimeSpan elapsed)`, and `ElapsedText(TimeSpan elapsed)`; expose `ConversionProgressSnapshot Snapshot(TimeSpan elapsed)` and `string PhaseLabel`. Queue item adds notifying `bool IsProgressIndeterminate` and `bool IsCancellationRequested`; `Cancel()` records the request before cancelling the existing token. `QueueStatus` combines active phase with cancellation-request wording, while result titles retain precedence.

- [ ] Add `SynchronousWorkDoesNotBlockCancel`, `OldAttemptCannotOverwriteRetry`, `OldSequenceCannotOverwritePhase`, and `LateCancelPreservesPublishedSuccess` dispatcher-pump tests with controlled fakes and completion gates. While a synchronous converter blocks, assert a dispatcher tick and Cancel callback run and the token is cancelled; release the gate before ending the test. Deliver an old attempt callback after retry and an old sequence after a newer phase: assert phase, percent and eventual result are unchanged. Test waiting-slot cancellation and cleanup after cancellation. Capture request/options on the dispatcher before moving work to a worker, so fake service execution never reads or mutates UI state.
- [ ] Add display and XAML assertions for named phases, phase-local resets, indeterminate binding, 10/30-second boundaries, and exact spec advisory wording. Replace the old generic-finalizing assertions with actual phase assertions while preserving the regression that 99% never implies completion. Adapt the old inline-context progress test to real queued delivery; it must not depend on worker code executing on the UI thread.
- [ ] Run GUI tests filtered to `ConversionProgressDisplay|ConversionPhaseQueue|QueueTests|WindowTests`; expect the new presentation/dispatcher assertions to fail.
- [ ] Capture immutable request/settings and create `Progress<T>` on the dispatcher, then run only `conversion.ConvertAsync` in `Task.Run`. Apply snapshots and results on the dispatcher. Keep timer and cancellation-source identity checks, plus observer job/sequence rejection. Reset cancellation-request and observer state at every attempt. Start indeterminate, switch to current phase percent when measurable, keep result authority over executor `completed`, and stop all updates before applying the terminal result. Bind `ProgressBar.IsIndeterminate`; retain `Value` binding `Mode=OneWay`. Preserve #56 result builder and diagnostic redaction.
- [ ] Run the whole GUI test project, including `ConversionResultDisplayTests`. Require all passing and no background view-model mutations. Manual DPI/cancellation testing is a separate Task 8 checkpoint.
- [ ] Stage owned files, run staged Gitleaks, and commit `feat: show conversion phases and overdue activity`.

## Task 7: Migrate CLI progress and manage advisory lifetime

**Files:** Modify `CliApplication.cs`; create `CliProgressDisplay.cs`; create `tests/BookSplice.Cli.Tests/CliProgressDisplayTests.cs`; extend `tests/BookSplice.Cli.Tests/CliApplicationTests.cs`; preserve `AdvancedOptionsTests.cs` and `CliComposition.cs` service signatures.

**Interfaces produced:** `CliProgressDisplay(TextWriter output, bool json, TimeProvider? timeProvider = null)` implements `IProgress<ConversionProgress>, IAsyncDisposable`. `StartAttempt()` resets the observer and starts a one-second timer; `Task StopAttemptAsync()` closes event acceptance, cancels and joins its timer. Use a shared private lock for observer state and output serialization; never wait for timer completion while holding the lock. Inject the same optional `TimeProvider` into `CliApplication` for tests. Construct/start one attempt presenter around each service invocation; stop it before interactive input or terminal event output.

- [ ] Add JSON tests that compare all five legacy execution fields and schema version 1, assert new phase fields (`phase`, `phasePercent`, `phaseSpeed`, `measurementScope` with value `phase`, `jobId`, `sequence`, `phaseOccurrenceId`, `expectsMeasurableUpdates`), and verify service-only legacy measurements stay null. Advisory `progress` events add `advisory`, `lastReportAgeSeconds`, `lastAdvanceAgeSeconds`, and `observationElapsedSeconds`; they repeat the last operation sequence and never refresh the observer. Assert unknown additive fields can be ignored by a legacy-reader fixture.
- [ ] Add `SilentAttemptEmitsAdvisoryWithoutHeartbeat`, `DecisionPromptSuspendsObservation`, `StopJoinsTimerBeforeFinal`, and `SelectedOrderStartsFreshAttempt` fake-time tests for silence and repeated values, no advisory during the interactive ordering prompt, a fresh identity for the selected-order attempt, a tick racing with stop, and cancellation. Assert no writes after `final`, no timer leak after exceptions, dry run or early failure, and human output names phases with "Phase remaining" only when measurable.
- [ ] Run `dotnet test tests/BookSplice.Cli.Tests --configuration Release`; expect new fields, lifetime and wording assertions to fail.
- [ ] Implement scoped CLI observation and additive serialization. Keep final result and exit mapping untouched. Human output emits phase changes, meaningful numeric updates, advisory entry and recovery; overdue ages may update at most once per second. JSON timers emit only while an advisory is active, plus one recovery event, without fabricating operation reports. Stop the presenter in `finally` around every service call. State/advisory enum values serialize as explicit stable strings, not numeric enum ordinals.
- [ ] Re-run the whole CLI project, including diagnostics privacy and decision-prompt tests. Require preserved final fields and exit codes.
- [ ] Stage owned files, run staged Gitleaks, and commit `feat: expose phase progress in CLI events`.

## Task 8: Integrate and verify the complete conversion path

**Files:** Modify `README.md` with current phase/ETA behavior and additive JSON contract. Modify only production/tests above if integration exposes a defect. Keep `ConversionResultDisplay.cs` unchanged unless a demonstrated integration bug requires a narrow fix.

- [ ] Before Task 1 execution, restore and record the committed-code baseline using the commands below. Verify both production and fixture tool directories contain working tools, and verify the production build against its existing manifest/inventory before claiming generated-media coverage. If baseline fails, establish whether the failure is environmental or in committed code; record evidence before changes.
- [ ] After Task 7, inspect live `origin/main`, primary checkouts, and other worktree metadata read-only. Review overlap with #77 in `MainWindow.xaml`, `MainWindowViewModel.cs`, window tests, and README. If main advanced, integrate with a non-squash merge only when needed; do not modify another chat's branch or dirty primary files. Re-run affected and full checks after integration changes.
- [ ] Run from this worktree, with the verified environment:

```powershell
$env:BOOKSPLICE_FFMPEG_DIR = 'C:\Tools\booksplice\artifacts\private-build-f4a2dd5f7abe4960a3ed420f1147f6c7\media\output'
$env:BOOKSPLICE_FIXTURE_FFMPEG_DIR = 'C:\Tools\booksplice\local\legacy-ffmpeg-tools\autobuild-2026-08-29-13-12'
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
.\scripts\Test-AcceptanceHarness.ps1
git diff --check
```

Expected: successful restore; build with zero warnings/errors; all tests passing with actual count recorded; format verification exit 0; safeguard command exit 0 with actual count recorded; diff check exit 0. Stop on command failure rather than allowing a later command to conceal its exit code. Repeat formatting only for owned files if existing unrelated formatting failures appear. Never repair unrelated code to make the full-tree check green.

- [ ] Before the final full-suite commands above, add `ChapterDisabledSegmentedFullDecodeReportsActualPhases` and `ValidationFailureNeverReportsPublication` generated-media service regressions using the real executor and validator: segmented chapter-disabled conversion with full decode must report encoding, assembly, metadata, validation checks, full decode, publication and final success in order. Assert validation failure prevents publication. Place these in `tests/BookSplice.FFmpeg.Tests/Execution/FFmpegRealMediaIntegrationTests.cs`; assert adapter result semantics in their existing GUI/CLI projects to avoid adding GUI references to the FFmpeg test project. Keep inputs disposable and generated.
- [ ] Perform genuine manual normal/high-DPI GUI acceptance when available: observe named phases, long unavailable phase advisory, repeated-value advisory via a controlled fixture, active Cancel, retry, and final validation/warning semantics. Record unperformed portions as unverified; automated dispatcher and window tests are not manual evidence. Do not use private media.
- [ ] Review the final branch against the spec, including the five Review Focus cases. Run a fresh independent whole-branch review using the user's selected execution workflow. Resolve concrete findings and repeat only affected checks plus the final full suite if implementation changed.
- [ ] Show the complete public README/PR writing draft before publication. Commit local documentation with staged Gitleaks as `docs: explain conversion phase progress`. Preserve the ignored handoff while review or publication is pending. No push, PR, merge, tag, or release is authorized by this plan.

## Plan self-review

Coverage: phase ownership and omission (Tasks 2, 4, 5); shared record and scope ordering (Tasks 1, 2); independent timing (Task 3); aggregation and ETA (Tasks 1, 4, 5); unavailable/stale/recovery semantics (Tasks 1, 6, 7); responsive cancellation/retry and #56 preservation (Task 6); CLI compatibility and final-event ordering (Task 7); generated-media validation, manual limits, shared-file integration, and publication boundaries (Task 8).

All five Review Focus cases have explicit owning tests. Interface names are shared consistently across tasks. No new dependency or subsystem is introduced beyond the approved progress contract. Execution still requires plan review and execution-method selection; serial native execution is recommended because each task consumes interfaces from its predecessor.
