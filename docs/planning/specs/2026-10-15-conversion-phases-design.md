# Conversion phases and overdue progress

Issue: [#57](https://github.com/nickwolf/booksplice/issues/57). Milestone: v0.2.0, clear status and results.

Status: design direction approved; written spec awaiting review. No implementation is included in this document.

## Purpose and scope

The queue must tell users which operation is running, what measurable progress is available, and whether updates or measurable advances have become overdue. Encoding completion must not imply that validation or publication finished. Users must remain able to request cancellation without an automatic timeout terminating a long operation.

Extend the existing shared progress channel so the GUI and CLI receive the same facts. Keep the result and validation semantics delivered by #56. Do not change validation requirements, publication policy, concurrency settings, updater behavior, or release packaging. No private media is required.

## Current behavior

`ConversionService` sends its progress sink only to the executor. Analysis, planning, validation, publication, cleanup, and audit writes produce no progress events. `FFmpegConversionExecutor` reports `State == "completed"` before the service starts validation. The GUI translates that event into a generic finalizing message.

`ConversionPlan` does not carry source durations independently of chapter entries. Without chapters, segmented progress uses an equal-source fallback that cannot measure how much of an active source has actually completed. Per-process stage indices and speeds also reach the book display while multiple segments can run concurrently.

The GUI already rejects callbacks after the attempt stopwatch stops or its cancellation source changes. Preserve this protection and add ordering protection within an attempt.

## Phase ownership and ordering

Emit a phase entry immediately before invoking its operation. A phase entry describes an operation being entered; it does not prove that the operation succeeded or that a child process started. An operation can fail at startup. Do not announce a later phase speculatively.

| Phase | Owner and entry boundary | Numeric evidence |
| --- | --- | --- |
| Analysis | Service, before conversion-time source reanalysis | Unavailable initially |
| Planning | Service, before constructing the conversion plan | Unavailable |
| Preparation | Executor, before workspace and command-input preparation | Unavailable |
| Encoding | Executor, before direct, filter-concat, or segment encoding | Processed audio duration when known |
| Stream copy | Executor, before copying compatible AAC into the output | Processed audio duration when known |
| Assembly | Executor, after successful segment encoding, before final assembly | Processed audio duration when known |
| Metadata writing | Executor, before the MP4 metadata writer | Unavailable |
| Validation checks | Validator, before file, probe, packet, chapter, artwork, and metadata checks | Unavailable; check names may provide activity updates |
| Full audio validation | Validator, immediately before optional full decode | Decoded audio duration against valid output duration |
| Publication | Service, before the atomic publisher copies and commits validated output | Unavailable in this change |
| Cleanup | Service, before controlled artifact cleanup | Unavailable |
| Audit writing | Service, before the final audit write | Unavailable |

GUI labels use action wording such as "Reanalyzing sources", "Encoding audio", "Assembling audio", "Checking output", and "Publishing output". CLI analysis can use "Analyzing sources" because its first analysis is not necessarily a repeat. Waiting for a GUI queue slot remains a separate queue state, without an overdue-operation warning.

Direct and filter-concat transcodes do not announce a separate assembly phase because their final FFmpeg command already performs encoding and muxing together. Stream copy does not announce encoding. Segmented conversion announces assembly only after all segment encoders succeed. Full audio validation appears only when full validation is selected and execution reaches that call. Failures and cancellation can transition directly to cleanup and audit. Dry runs stop after planning and do not announce execution or finalization work.

Executor-owned failure cleanup must also identify its actual cleanup operation. Avoid duplicate phase entries for the same cleanup call. Cleanup and audit failures retain their existing warning behavior.

## Shared progress contract

Extend `ConversionProgress` with typed phase identity and nullable phase measurements. Keep the existing constructor arguments available to reduce source churn in consumers and test doubles. Add these explicit concepts:

- `Phase`: the operation in the table above.
- `PhasePercent`: a finite value from 0 through 100, or null when the current operation has no reliable denominator.
- Phase speed: audio seconds processed per wall-clock second, or null. Its scope is the current book phase, never the latest individual segment.
- Expected-update capability: whether measurable progress reports are expected during this operation. Phase entry and check-boundary updates alone do not promise periodic telemetry.
- Service envelope: job ID, monotonically increasing sequence, and phase occurrence ID. A phase occurrence distinguishes repeated cleanup calls or any later re-entry into the same phase.

The service creates the job identity before analysis. It serializes forwarded events and stamps their sequence at one boundary. Executor and validator callbacks are bound to their operation scope; callbacks arriving after that scope closes are discarded before stamping. Sequence numbers alone cannot reject an old phase callback that arrives late, so scope closure is required as well.

The service closes its progress session before returning the terminal result. GUI delivery additionally checks the current attempt identity and ignores duplicate or older sequence numbers. All display mutation and timer handling occurs on the dispatcher. The result is applied once and cannot be overwritten by queued progress from that attempt or an earlier one.

There is no progress event that means the entire conversion succeeded. The terminal `ConversionServiceResult` remains authoritative. The existing executor `State == "completed"` retains its execution-only meaning and must not drive the new GUI completion state.

## Measurement and estimates

Freeze nullable per-source audio durations in the plan from analysis evidence, independently of chapter creation. Reject nonpositive, unrepresentable, or nonfinite evidence as unavailable for progress calculations; missing duration evidence must not newly prevent an otherwise supported chapter-disabled conversion. Preserve the existing validation rules for plans that require duration evidence.

For encoding parallel segments, sum each source's clamped, nondecreasing processed duration and divide by the sum of source durations. Include completed segments at their full duration only after successful process exit. A process `progress=end` line alone does not prove success. Serialize aggregation under a lock, and emit one nondecreasing book-level percentage per phase occurrence. If complete duration evidence is missing, report unavailable numeric progress and known completed-source counts without substituting equal-duration estimates.

Compute book-level encoding speed from changes in aggregate processed audio over monotonic wall time. Do not sum reported segment speeds or reuse whichever speed arrived last. Assembly and stream-copy measurements have their own samples. Reset all measurements, speed, freshness, and ETA samples at phase entry. A phase-local percentage can reset when the label changes; no synthetic whole-job percentage or fixed phase weights are introduced.

Enable machine progress during full decode. Use valid probed output duration as the denominator, clamp processed time, and retain full-decode exit-code and diagnostic checks unchanged. Never infer a passing `decode.full` check from progress reaching 100%. If no valid duration or advancing samples exist, validation ETA remains unavailable. Validation checks do not borrow encoding estimates.

Estimate remaining time only for the current measurable phase using recent advancing samples. Use a rolling 30-second sample window after at least 3 seconds of observation. Hide ETA when the last numeric advance is more than 10 seconds old, speed is zero, evidence is invalid, or the result would be nonfinite or nonpositive. Label it "Phase remaining" so users do not interpret it as total job time. Recovery uses fresh advancing evidence; samples from before a stale interval must not produce an immediate misleading estimate.

## Unavailable progress and overdue activity

Use monotonic elapsed time for thresholds and two separate timestamps: last accepted operation report and last measurable advance. A repeated value updates the first timestamp but not the second. UI timer ticks never count as operation reports. Phase entry resets both clocks and any prior advisory.

| Condition | Display behavior |
| --- | --- |
| Operation has no numeric evidence | Show the phase, elapsed time, and "Numeric progress unavailable"; use an indeterminate bar |
| No operation report for at least 30 seconds | Add "No update received for X; operation has not returned" |
| Reports continue, measurable updates are expected, but no advance for at least 30 seconds | Add "Updates received, but progress has not advanced for X" |
| Both silence and lack of advance apply | Prefer the no-update message; retain the last known measurement |
| Fresh report after silence | Clear the silence advisory; a no-advance advisory can remain if warranted |
| Fresh measurable advance | Clear the no-advance advisory and rebuild ETA confidence |

The 30-second threshold is an advisory constant, not a timeout, and applies at the boundary inclusively. A phase without numeric progress can still become overdue for operation reports; say only what is observed. Do not describe the process as hung, healthy, unresponsive, or making progress merely because it has not returned. Where no process handle is exposed, do not claim that a child process is still running.

Preserve the last measured percentage during an overdue measurable phase and hide its ETA. Show elapsed time and the age of the relevant last update. No advisory cancels work, kills a process, fails validation, or changes the terminal outcome. Avoid a new configuration UI for these thresholds.

## GUI responsiveness and cancellation

Invoke the conversion service away from the dispatcher, using the existing dispatcher-captured progress adapter for presentation. This prevents synchronous portions of reanalysis, preparation, metadata writing, or publication from blocking timer ticks and cancellation input. Do not move observable collection or view-model mutation to a worker thread.

Keep Cancel enabled for active and waiting attempts. A request shows "Cancellation requested" while keeping the actual current phase and elapsed cleanup state visible. Continue passing the cancellation token to operations that support it. Synchronous metadata writing, mandatory cleanup, audit completion, and the final atomic rename may need to finish before cancellation can settle. Do not promise an interruptible atomic commit or mark an already published successful result as cancelled solely because the user clicked late.

Preserve #56's result card, warning distinction, requested-versus-performed validation evidence, diagnostics redaction, published-path rules, and retry eligibility. Only a successful service result produces the final completed display.

## CLI compatibility

Keep schema version 1's `started`, `progress`, and `final` event names and existing fields. Existing execution progress fields (`displayPercent`, `speed`, `stageIndex`, `sourceIndex`, and `state`) retain their execution-only meanings. Populate the new explicit phase fields on every new-contract progress event. New service-only phase events have null legacy measurements rather than reinterpreting them as whole-job progress. Preserve the final event's status, exit code, job ID, and publication fields.

Add phase identity, phase percent, phase speed, measurement scope, job/sequence/occurrence identity, and expected-update capability as additive JSON fields. A consumer must tolerate extra fields and progress events whose numeric fields are null; document and test this existing-style nullable behavior. CLI human-readable output names the phase and its available measurements. Do not print executor completion as completed validation or publication.

Use the same freshness/advisory calculation for CLI and GUI, with a cancellable timer owned by the CLI run. Emit a JSON activity advisory on a `progress` event with an explicit advisory field and observation age, while leaving operation-report sequence and timestamps unchanged. Serialize output with other events. Stop and join the timer before emitting `final`; no events may follow it. Waiting for an interactive ordering decision is not an active operation and must not generate overdue advisories. A second service attempt gets a fresh identity and observation state.

## Files and integration boundaries

Expected production changes include the core execution and validation contracts, conversion service, conversion plan/planner, FFmpeg executor and normalizer, validator full-decode command, GUI progress formatter and queue properties, `MainWindowViewModel`, `MainWindow.xaml`, and CLI presentation. Add focused shared observation logic in Core only if it remains presentation-neutral; adapter wording belongs in the adapters.

Potential shared-file overlap with #77 includes `MainWindow.xaml`, `MainWindowViewModel.cs`, window tests, and any README progress documentation. Keep changes local to conversion presentation and integrate against live main before final validation. Do not modify updater work or another worktree. Both primary checkouts stay on main; this work remains in its dedicated linked worktree.

## Acceptance and validation

- Service tests verify actual phase entry order, no speculative later phases on failure, dry-run omission, execution-only completion, cleanup/audit paths, closed scopes, and no callbacks after terminal return.
- Executor tests use production normalization for direct transcode, stream copy, and segmented conversion. Include 33 chapter-disabled sources, unequal durations, concurrent interleaving, missing durations, failed exits after `progress=end`, and late segment callbacks during assembly.
- Validator tests exercise real command construction and progress parsing for full decode, measurable and missing output durations, stale/repeated reports, and unchanged error/warning/full-decode check semantics.
- Fake-clock observer tests cover unavailable numbers, 10-second freshness, the inclusive 30-second advisory boundary, continuous repeated values, silence, recovery, phase resets, and stale callbacks. No test waits 30 real seconds.
- GUI tests cover phase labels and indeterminate bars, phase-local percentage resets, dispatcher-safe updates, cancellation responsiveness while synchronous work is blocked in a controlled fake, cancellation during queue wait and cleanup, and callbacks delivered after retry or terminal result. Retain #56 result and explicit-completion regression coverage, adapting assertions to named phases where required.
- CLI tests verify additive JSON fields, preserved legacy execution fields and terminal exit/status semantics, coherent book-level fields, advisory timing without fake operation heartbeats, interactive-decision suspension, and no events after `final`.
- Run the generated-media suite with both verified media-tool directories, Release build, format verification, acceptance-harness safeguards, and the required secret scan. Record actual outcomes; do not reuse previous test counts as current evidence.
- Genuine manual GUI acceptance checks cancellation and phase text at normal and high DPI. If not performed, report it as unverified separately from automated tests.

## Review boundary

Approve this written spec before implementation planning. The plan must identify concrete contract additions and migration of every production caller, fake, and adapter. Implementation begins only after plan review and execution-method selection. Public PR text, publication, merge, tags, and releases each retain their existing approval boundaries.
