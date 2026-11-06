# Bisik Experiments implementation plan

Status: execution in progress; completed items are checked with validation receipts. Prepared 6 November 2026. This file is the persistent execution checklist. Repository root: `D:\Dev\Bisik`; all source paths below are relative to it. Files marked **new** do not exist yet.

## A. Project context and implementation guide

### Objective and agreed scope

Add a native Experiments window so a human reads a reference script once, Bisik recognizes it once, and every selected cleanup model/deployment × supported reasoning effort × system prompt operates on the identical raw transcript. Evaluate outputs against the reference using local wording/identifier checks and a fixed LLM meaning/readability judge. Show progress, a ranked table and a prominent winner; sliders and numeric inputs reweight saved scores immediately without network calls.

Confirmed decisions: one shared transcription; hybrid local plus LLM scoring; one existing cleanup provider connection with independent candidate/judge model selections; persist setups/results with audio retention optional. **Major meaning errors remain eligible for ranking.** The judge assigns low meaning scores and warning evidence; there is no semantic disqualification gate. Failed/incomplete generation and unavailable positively weighted scores are different conditions and cannot produce a comparable score.

Defaults to implement: sequential trials; one repetition, configurable 1–5; maximum 100 scheduled cleanup trials per batch; five-minute recording limit; 120-second recognition-after-Stop and per-trial limits; five-second initial quiet hint. Weights are Wording 5, Identifiers 25, Meaning 40, Readability 15, Speed 15. Speed reference B defaults to 5 seconds. Audio retention defaults off. These limits/defaults are adjustable within validated bounds, not model/endpoint constants. Experiments never automatically insert/copy text, update normal history/latest results, apply a winner or modify active settings. Setup freezes during a run; weights remain editable.

### Current architecture and non-obvious constraints

Bisik is C#/.NET 8 Windows WinForms, with NAudio capture, built-in HttpClient, System.Text.Json, DPAPI, xUnit and FluentAssertions. Core does not reference UI. `Program.BisikApplicationContext` is the composition root; it owns forms, dictation, HTTP, hotkeys and asynchronous shutdown. `TranscriptionService` owns provider/postprocessor orchestration; `AudioRecorder` only captures audio. Preserve these boundaries and the existing file fallback, final-only insertion and exactly-once result selection.

Experiment flow: snapshot setup → owned capture/raw-only session → drain/persist → one raw transcript → cleanup matrix → fixed judge → cached factor scores → local ranking. Do not call `Program.StartRecording()` for experiments: it captures an insertion target and eventually dispatches output. Do not simulate a provider returning saved raw text to reuse `TranscribeAsync`; add a cleanup-only service operation. The runner must not call providers or LLM HTTP directly.

Each candidate's comparable timing is **Estimated Stop→final = measured shared Stop→raw + its measured cleanup duration**, including that cleanup's retries/parsing, excluding matrix queueing, judging, recording duration and insertion. Show both underlying measurements. Batch elapsed and judge elapsed are separate. A whole batch may exceed two minutes; use a batch cancellation token and independently bounded stage/trial tokens.

Current effort emission silently omits `reasoning_effort` when `ReasoningCapability.Matches()` fails. Never label an omitted request “low”; persist requested/effective effort and verification state. Provider default is null/omission; `none` is an explicit value. Capability evidence is scoped to exact endpoint/API/deployment/model, not a friendly name. GPT-6 Astra is a candidate for low, not none; public model listings do not establish ABB deployment access.

Capture events are raised on the capture thread. The current real signal is `AudioLevelChanged`, not AGENTS' historical `WaveformDataAvailable` wording. Overlay waveform shimmer is decorative and cannot establish microphone activity. `DrainCaptureAsync` waits for final NAudio buffers; `CapturedRecording.PersistAsync` is exactly once. Live recognition with history off can finish before optional file persistence: the experiment owner must still await and own it.

Settings records contain mutable arrays/dictionaries: deep-snapshot new fields. `SettingsManager` currently protects only the main/cleanup provider connections. Do not serialize live ProviderSettings into plain experiment JSON. Reuse an isolated encrypted settings snapshot for the single connection; never reload/migrate the active source. Safe exports follow `HistoryConfigurationSnapshot`'s allowlist.

### Key files and symbols

| Existing path | Symbols / responsibility |
|---|---|
| `src/Program.cs` | `BisikApplicationContext.ShowSettings/ShowHistory`, `CreateTranscriptionService`, `BuildProviderMap`, hotkey handlers, `CompleteRecordingCoreAsync`, `DrainShutdownAsync` |
| `src/UI/TrayIcon.cs` | Constructor commands, `AddResultCommands`, status/balloons |
| `src/UI/SettingsForm.cs` | Native tab/help/layout pattern; `HandleTestConnectionClickAsync`, `BuildProviderTestSettings`, injected service factory |
| `src/UI/HistoryForm.cs` | Explicit bound DataGridView, copying, asynchronous close/refresh lifecycle |
| `src/Core/AudioRecorder.cs` | `AudioRecorder`, `CapturedRecording`, `DrainCaptureAsync`, `AudioLevelChanged`, `AudioRecorderLogic.CalculateLevel` |
| `src/Core/TranscriptionSession.cs` | `StartAsync`, `PushAudio`, `RequestStop`, `CompleteAsync`, `FinalizeLiveAsync`, cancellation/disposal |
| `src/Core/TranscriptionService.cs` | `CreateSession`, `TranscribeCoreAsync`, `TranscribeWithRetryAsync`; cleanup/history/metrics boundaries |
| `src/PostProcessor/LLMPostProcessor.cs` | `ProcessAsync`, `BuildRequestPayload`, `BuildAzureUri`, `ParseChatCompletionAsync`, `ReadCompleteTextAsync` |
| `src/PostProcessor/PostProcessorPresets.cs` | `ResolveSystemPrompt`; built-in presets do not use the custom prompt field |
| `src/Models/AppSettings.cs` | `ProviderSettings`, `PostProcessorSettings`, `ReasoningCapability.Fingerprint/Matches`, `AppSettings.Snapshot/CopyProvider` |
| `src/Core/SettingsManager.cs` | `Save`, `LoadEncryptedCopy`, normalization, DPAPI and atomic writes |
| `src/Models/HistoryConfigurationSnapshot.cs` | `Capture`, `SafeProvider`: secret-safe export precedent |
| `src/Core/SessionMetrics.cs`, `HttpMetricsHandler.cs`, `RetryPolicy.cs` | Bounded typed receipts, `TimingAggregation`, HTTP headers/EOF, server delay handling |
| `src/Core/FinalTextSelection.cs`, `LatestResultStore.cs` | Authoritative raw/edited arbitration; atomic pending-result claims |
| `src/Core/LiveAudioQueue.cs`, `SpeculativeCleanup.cs`, `RealtimePrewarmer.cs` | Ordered streaming, exact whole-text speculation, settings-sensitive unused session transfer |
| `src/Providers/AzureRealtimeProtocol.cs`, `AzureOpenAIProvider.cs` | `SessionUpdate`, `UsesRealtime`, deployment/API routing |
| `src/Core/RealtimeTranscriptionStream.cs`, `src/Providers/AzureRealtimeTranscriber.cs` | Shared realtime protocol for live and recorded-file replay |
| `src/Core/HistoryExport.cs` | JSON/CSV/table/Excel HTML export; schema currently specific to history |
| `tools/Bisik.Diagnostics/*Diagnostics.cs`, `src/Core/BenchmarkOptions.cs` | Existing non-inserting cleanup/file/live/probe/compare entry points |
| `tests/Bisik.Tests/` | Capture, service, provider, settings, metrics, output and STA WinForms precedents |

New logical components: `Models/ExperimentDefinition.cs`, `Models/ExperimentResult.cs`; `Core/ExperimentMatrix.cs`, `ExperimentScoring.cs`, `ExperimentSession.cs`, `ExperimentRunner.cs`, `ExperimentStore.cs`; `PostProcessor/LLMExperimentJudge.cs`; `UI/ExperimentsForm.cs`, `ExperimentWeightsForm.cs`. Keep related small records together. A judge adapter reuses the service completion path; it does not duplicate HTTP transport. Avoid a new general workflow/form framework or new NuGet packages.

### Autonomous execution boundary (6 November 2026)

User direction: postpone work requiring human intervention. Execute local source, fake-based validation and documentation for items 1-20 and 22. Item 21 and all its subtasks remain unchecked and postponed until the user supplies E4. Completing local code does not complete its separate external acceptance.

Postponed acceptance: E1 deployment/access selection; E2 live provider/model probes and paid quality/latency comparisons; E3 agent/secrets/private-artifact setup and actual pipeline execution; E4 Speech service/contract selection and training/deployment. Also postpone human spoken-script/microphone-quality acceptance. UI checks that can be automated locally remain in scope; record unsupported or unrun environment checks truthfully. No user intervention is needed for mocked contract tests, source-only pipeline templates or documenting these boundaries.

When local implementation/testing for an item passes, check that item with its evidence and explicitly list any postponed external acceptance. Do not check item 21 or declare those external conditions verified. Continue past postponed work without waiting for the user.

### Conventions and execution guide

Read `AGENTS.md`, this introduction and the selected task. Work one task at a time unless independent tasks are explicitly coordinated. Select an unchecked task with completed dependencies; inspect its named symbols for drift before editing. Preserve completed checkboxes and stable IDs. Mark each subtask checked only after its implementation/validation passes; mark its parent only when all subtasks and acceptance criteria pass. Leave blocked work unchecked and append `Blocker: ...` with the missing prerequisite. Update incorrect assumptions locally rather than repeating broad research. Record unrun live/manual checks separately. Do not commit until requested.

Keep code concise and strongly typed; comment ownership/intent where not obvious. Async void is only for UI events; no UI-thread blocking, Thread.Sleep, browser UI, plaintext secrets, partial insertion or automatic submission. Use existing helpers before extracting another. If export/help/STA test helpers need sharing, extract only the small existing implementation. Update AGENTS/README for new files/settings. Preserve encoding/line endings.

Standard validation, from repository root:

```powershell
dotnet restore Bisik.sln
dotnet build Bisik.sln -p:BisikRegisterLatestBuild=false
dotnet test Bisik.sln -p:BisikRegisterLatestBuild=false
git -c core.whitespace=cr-at-eol diff --check
```

Use focused `--filter FullyQualifiedName~...` while implementing; run relevant/full regressions at integration. The registration opt-out prevents builds from changing the installed launcher's latest-build pointer. No installation/restart/service/cloud changes are part of implementation. Live API/microphone tests require the external conditions below; unit tests use fakes and never incur inference cost.

## B. Implementation checklist

### Phase 1: Contracts, service execution and reproducible scoring

- [x] 1. Define immutable experiment contracts and state
    - **Objective:** Give UI, runner, judge and persistence one versioned representation.
    - **Location:** **new** `src/Models/ExperimentDefinition.cs`, `ExperimentResult.cs`; `src/Models/AppSettings.cs` → snapshot patterns.
    - **Implementation:** Separate reference, raw take and candidate identities; store frozen connection/config fingerprints, prompts, reviewed terms, requested/effective effort, repeat index, timing, reported/effective judge scores, warnings and scoring/rubric versions. States: Draft, Recording, Stopping, Transcribing, Generating, Evaluating, Completed, Cancelled, Failed; generation/evaluation statuses remain separate. Weights/speed scale are derived-view settings, not request identity.
    - **Dependencies:** None.
    - **Validation:** Round-trip records; collections cannot mutate a running snapshot; invalid bounds/nonfinite values rejected; no live secrets in result DTOs.
    - [x] 1.1. Add grouped records/enums and defaults, including optional audio retention and baseline identity.
    - [x] 1.2. Add explicit deep snapshots and versioned hashes for input/config/evaluation identity.
    - [x] 1.3. Add `ExperimentModelTests` for immutability, defaults, identity and serialization.
    - Validation: 8 ExperimentModelTests passed; restore and diff check passed. No live calls.

- [x] 2. Add a non-inserting cleanup-only service stage and capability probes
    - **Objective:** Execute a saved raw string through production completion validation without rerunning recognition.
    - **Location:** `src/Core/TranscriptionService.cs` → `TranscribeCoreAsync`; `src/PostProcessor/LLMPostProcessor.cs` → `ProcessAsync/BuildRequestPayload/ParseChatCompletionAsync`.
    - **Implementation:** Add `ProcessTextAsync` (new) with typed output/timing/status/effective-parameter receipts; share the stage invocation with normal cleanup without changing final selection. No history/sound/UI/output side effects. Add a distinct explicit compatibility-probe operation for unknown efforts; send the requested probe parameter without fabricating verified capability, accept only complete successful responses, then record experiment-local evidence. Default requests omit effort. Optional one retry for transient 408/429/5xx/network errors stays in the service, obeys Retry-After and shares the trial deadline; do not broaden normal dictation retries implicitly. Never retry authentication/invalid payload/unsupported parameters into another configuration.
    - **Dependencies:** 1.
    - **Validation:** Captured payload proves effective effort/model/prompt; no provider recognition/history/sound calls; cancelled late completions rejected; retry latency included; incomplete/refused output fails.
    - [x] 2.1. Add the shared cleanup stage and typed receipts while preserving `IPostProcessor` compatibility.
    - [x] 2.2. Add separate capability-probe state and bounded service retry behavior.
    - [x] 2.3. Extend `TranscriptionServiceTests`/`PostProcessorTests` with payload, cancellation, retry and no-side-effect cases.
    - Validation: 64 service/completion tests passed, including 11 new text-stage cases; diff check passed. No live probes.

- [x] 3. Build and validate the candidate matrix
    - **Objective:** Turn selected models/efforts/prompts into an honest, bounded execution plan.
    - **Location:** **new** `src/Core/ExperimentMatrix.cs`; `AppSettings.cs` → `ReasoningCapability.Fingerprint/Matches`; `PostProcessorPresets.ResolveSystemPrompt`.
    - **Implementation:** Use the one current cleanup connection; distinguish Azure deployment and payload model where needed. Resolve each named prompt once. Deduplicate equivalent effective configurations, not merely display names. Model × compatible effort × prompt × repeat produces at most 100 cleanup trials. Show valid/unknown/excluded rows and reasons; unknown pairs need task 2's explicit verification before trial execution. Changing exact connection/API/model invalidates capability evidence. Do not silently omit effort, replace models or infer gateway availability from catalog entries. A raw baseline is a separate row, not a paid cleanup call.
    - **Dependencies:** 1, 2.
    - **Validation:** Exact counts, default versus none, aliases, duplicate prompts, capability invalidation, integer overflow/limits, blank entries and excluded-pair reasons.
    - [x] 3.1. Implement matrix expansion, fingerprinting, deduplication and local validation.
    - [x] 3.2. Connect explicit compatibility results to eligible configurations and call-count preview.
    - [x] 3.3. Add `ExperimentMatrixTests`; verify requested low can never become a labelled default trial.
    - Validation: 5 matrix tests passed for bounds, deduplication, effort evidence and connection invalidation; diff check passed.

- [x] 4. Implement local factors, weighting and ranking
    - **Objective:** Produce deterministic, explainable factors and instant weight changes.
    - **Location:** **new** `src/Core/ExperimentScoring.cs`; `SessionMetrics.cs` → `TimingAggregation`.
    - **Implementation:** Version Unicode/word normalization; require at least one reference token before Run. Wording score = `100*max(0,1-WER)`, WER=(substitutions+deletions+insertions)/reference tokens. Preserve numbers/negation; do not correct aliases before lexical comparison. Suggest terms from reviewed code-like tokens (backticks, paths, Pascal/camel case, snake_case, acronyms); never silently accept ambiguous suggestions. Identifier factor averages per-term `correct/(correct+configuredWrong)`; absent required terms score 0. Canonical matches use ordinal exact case; case variants and explicitly configured wrong aliases use case-insensitive detection excluding exact canonical matches. Match complete identifiers/paths and longest aliases first; flag ambiguous API/tool aliases instead of double-counting. Empty term lists make that factor unavailable. Speed=`100*B/(B+t)`, B>0, t=shared Stop→raw + candidate cleanup. For repeats: wording/identifier factors are arithmetic means across valid generation samples; meaning/readability are means across valid judged samples, applying severity caps per sample first; speed uses median cleanup across all valid generation samples. Preserve all sample warnings and show judged/successful/failed counts; partial evaluation is provisional and available aggregates may rank. Weights do not change sample cohorts. Normalize integer weights 0–100 by their sum. Missing positive-weight factors mean unranked; measured zero remains valid. Major semantic warnings never exclude a valid result. Round display to 0.1 point; equal displayed scores share rank with stable visual ordering.
    - **Dependencies:** 1.
    - **Validation:** WER>1 clamps similarity; HAPPI/Happy/mixed case tests; no alias double count; zero versus missing; all-zero weights; ties; repeat aggregation; speed unchanged when another row arrives.
    - [x] 4.1. Implement bounded local comparison and term evidence with explicit unavailable factors.
    - [x] 4.2. Implement fixed-cohort factor aggregation, smooth speed, normalized weights, ties and visible warning badges; warnings do not gate eligibility.
    - [x] 4.3. Add `ExperimentScoringTests`: B=5 and t=0/5/15 → 100/50/25; HAPPI Happy and HAPPI happi → 50 with the configured alias/case rules; empty-token reference rejection; repeat caps/means/coverage; speed-only winner with a major warning.
    - Validation: 12 scoring cases passed; comparison is bounded to 4096 tokens and 100 reviewed terms. Diff check passed.

### Phase 2: Evaluation, recording, scheduling and persistence

- [x] 5. Add the fixed meaning/readability judge
    - **Objective:** Assess semantic fidelity separately from surface wording and fluency.
    - **Location:** **new** `src/PostProcessor/LLMExperimentJudge.cs`; task 2 service stage; experiment evaluation records.
    - **Implementation:** One separate judge model/effort selection on the same connection, fixed across candidates. Judge sees reference + candidate final text + reviewed terms/rubric, never candidate model/prompt/latency/scores; raw baseline uses the same rubric. Treat all embedded instructions as data. Request bounded JSON with separate meaning/readability 0–100, enumerated category/severity and short reference/output evidence. Meaning anchors: extreme 0–10, major 11–30, substantial omissions 31–60, minor loss 61–85, preserved 86–100. Preserve reported score; major/extreme evidence caps effective meaning at 30/10 with visible reason. Readability remains independent. Validate schema/completion/ranges/finite numbers/evidence bounds. Cache by reference/output/terms/rubric/judge API+model+effective settings; weight edits never invalidate it. Judge-only retry does not regenerate text. No hidden-reasoning requests or fabricated missing scores.
    - **Dependencies:** 1, 2, 4.
    - **Validation:** Malformed JSON/refusal/truncation/oversized evidence fails; major 80→30 remains eligible; extreme 80→10; readability unchanged; injected instructions stay data; correct cache invalidation and judge-only retry.
    - [x] 5.1. Add fixed rubric, blinded payload construction and validated judge adapter using shared transport.
    - [x] 5.2. Add evidence caps, evaluation cache and retry/invalidation contracts.
    - [x] 5.3. Add `ExperimentJudgeTests` with fake completion responses and no live grading.
    - Validation: 10 judge tests passed for caps, schema bounds, blinding, cache and judge-only retry. Live grading postponed under E2.

- [x] 6. Own a single capture and shared raw transcription
    - **Objective:** Record once without touching normal dictation targets or output.
    - **Location:** **new** `src/Core/ExperimentSession.cs`; `AudioRecorder`, `CapturedRecording`, `TranscriptionSession`; `Program.StartRecording` ordering precedent.
    - **Implementation:** Dedicated recorder and raw-only service/session with history, cleanup, speculation and sounds disabled; use the current transcription settings/live path and complete-file fallback. Subscribe chunks before capture; listen immediately while network setup runs. Track actual audio levels with sustained threshold/activity duration, show a quiet hint after five seconds, never stop on a pause. Distinguish no buffers/device failure from quiet input. Done calls RequestStop, drains final buffers, persists/owns the WAV task and freezes one raw string. Measure shared Stop→raw monotonically. Limit capture to five minutes with a visible limit-stop notice. Empty raw stops paid cleanup/judging. Cancellation/disposal owns setup/sender/persistence/recorder and rejects late STT. Audio is temporary until the retention decision is honored.
    - **Dependencies:** 1, 2.
    - **Validation:** One capture/recognition; complete final buffer; setup failure fallback; silent/empty take; repeated Done; mic error; cancellation; no history/clipboard/output; real activity callbacks coalesced.
    - [x] 6.1. Implement session state, chunk/level subscriptions and raw-only recognition.
    - [x] 6.2. Implement Done/limit/abort/drain/persistence ownership and quiet hints.
    - [x] 6.3. Add `ExperimentSessionTests` using existing fake IWaveIn/stream patterns.
    - Validation: 58 capture/session/service regressions passed, including 7 experiment capture cases. Existing stream setup/file-fallback regressions passed. Human microphone acceptance postponed.

- [x] 7. Run cleanup trials and evaluation as separate phases
    - **Objective:** Execute the matrix predictably and preserve partial progress.
    - **Location:** **new** `src/Core/ExperimentRunner.cs`; tasks 2–6; `SessionMetrics`.
    - **Implementation:** Every trial receives the same raw string, frozen effective settings, its own linked deadline/metrics, and typed progress. Sequential scheduling; repeats run in recorded randomized rounds to reduce order bias. Capability probes/warmup receipts are separate from measured trials. Generate all candidate outputs first, then judge distinct outputs, avoiding evaluator traffic during generation. Count complete/failed/skipped/cached jobs explicitly; current request uses indeterminate progress. Batch cancel stops active/pending work, retains completed rows and allows selected cleanup/evaluation reruns using saved raw. A failure is isolated; unavailable models are failures, not poor quality. Settings/reference edits create a new setup/evaluation revision rather than relabeling results. Reweighting never schedules work.
    - **Dependencies:** 3, 4, 5, 6.
    - **Validation:** Identical inputs, isolated metrics/deadlines, failure continuation, repeat order seed, generation-before-judge, cancelled pending jobs, selected reruns and no new transcription.
    - [x] 7.1. Implement sequential scheduler and typed `IProgress<ExperimentProgress>` events.
    - [x] 7.2. Add separate judge phase, baseline evaluation, bounded reruns and partial-result events.
    - [x] 7.3. Add `ExperimentRunnerTests` proving no providers/judges are called by score changes.
    - Validation: scheduler tests passed for seeded rounds, identical input, generation-before-judge, distinct-output cache, selected evaluation rerun, isolated failure and partial cancellation.

- [x] 8. Persist/reopen experiments and export safe comparisons
    - **Objective:** Reuse a take and its results across app sessions without saving plaintext connections.
    - **Location:** **new** `src/Core/ExperimentStore.cs`; `SettingsManager.Save/LoadEncryptedCopy`; `HistoryConfigurationSnapshot.Capture`; `HistoryExport` formatting helpers.
    - **Implementation:** Versioned setup/run/result files beneath `%APPDATA%\Bisik\experiments\<id>`. Use immutable IDs and content hashes; atomic writes/checkpoints per result; an interrupted run reopens as interrupted, not complete. Save reference, raw, prompts, weights, B, rubric/scoring versions, outputs/receipts/warnings. Keep the one connection in an isolated DPAPI settings snapshot; public DTOs/export only approved fields. Retain audio only when selected; delete owned temporary recording after completion/cancel otherwise. Saved raw allows cleanup reruns without WAV. Export JSON/CSV/Unicode Excel-compatible rows; extract small existing formatting routines only if reused. Imported results can be inspected without credentials; reexecution clearly identifies unavailable connection/audio.
    - **Dependencies:** 1, 7.
    - **Validation:** Restart/checkpoint round trips, corrupted/interrupted files, atomic failure, owned-path cleanup, opt-out audio deletion, no active settings/history changes, endpoint/key/header exclusion, Unicode multiline exports.
    - [x] 8.1. Implement store/recovery/checkpointing with separate encrypted connection snapshots.
    - [x] 8.2. Implement optional audio retention and focused experiment exports/copy data.
    - [x] 8.3. Add `ExperimentStoreTests`/`ExperimentExportTests`, including secret exclusion and disk-write failure.
    - Validation: 20 store/export/history/judge tests passed; solution build passed with registration disabled. Connection reload leaves its encrypted source unchanged.

### Phase 3: Native window and app lifecycle

- [x] 9. Build setup, recording and progress UI
    - **Objective:** Present the single-reading workflow without blocking or burying the script.
    - **Location:** **new** `src/UI/ExperimentsForm.cs`; `SettingsForm` layout/help and `HistoryForm` grid patterns.
    - **Implementation:** Modeless singleton window; Setup and Results views using native TableLayoutPanel/SplitContainer/TabControl. Large multiline script remains visible during recording; separate named prompt editor. Editable model/deployment/effort selections, reviewed term checklist, separate judge settings, repeat/retention options and generated-trial preview. Commit pending grid edits before validation. Start/Done/Cancel, mic/elapsed/real-level hint; stage/row identity, bounded status log, completed/total bar and current-request marquee. Controls follow state; setup edits disabled during run, script scroll/copy stays available. Marshal/coalesce progress, discard stale run events, no decorative mic evidence. Reuse DPI/accessibility/help conventions without copying the whole SettingsForm.
    - **Dependencies:** 3, 6, 7, 8.
    - **Validation:** STA fake-runner tests cover input validation, state/buttons, repeated clicks, quiet notification, progress order, long script/prompt, closure and stale events; keyboard/DPI checks listed separately.
    - [x] 9.1. Build native setup/matrix/prompt/judge controls and accessible help.
    - [x] 9.2. Bind recording/progress states, bounded logs and save/open/rerun commands.
    - [x] 9.3. Add `ExperimentsFormTests`; share only the small existing STA host if needed.
    - Validation: 16 experiment/history STA tests passed; build passed. Long editors, input validation, exclusive repeated start and close/capture drain verified. Physical DPI/keyboard and human microphone acceptance remain unrun.

- [x] 10. Build live weighted results and detail inspection
    - **Objective:** Make the tradeoff and winner immediately understandable and adjustable.
    - **Location:** `ExperimentsForm.cs` (new); **new** `src/UI/ExperimentWeightsForm.cs`; task 4 scoring.
    - **Implementation:** Prominent Top for these weights, all factor values, warnings, samples/failures and measured cleanup/estimated total columns. Selecting a result shows reference/raw/edited text, missing terms and judge evidence/effective settings. Owned modeless weights popup: synchronized TrackBar+NumericUpDown 0–100, relative percentages, reset defaults and finite positive B input. Recompute locally on edits; maintain selected candidate ID during resort. All-zero/missing factors show explanation, no winner; zero-weight unavailable factors do not block other scores. Major-error rows remain eligible and clearly flagged even when speed-only weights make them win. Show ties/provisional results and explicit copy/export actions, never automatic output.
    - **Dependencies:** 4, 5, 8, 9.
    - **Validation:** Weight synchronization without recursion/API calls, all-zero/missing versus zero, warnings without exclusion, stable selection/ties, popup disposal, exports and same ranking after reopen.
    - [x] 10.1. Build grid/winner/detail views with honest timing and warning labels.
    - [x] 10.2. Build synchronized weights/B editor and immediate cached reranking.
    - [x] 10.3. Extend STA tests for zero weights, major-error winner, selection and no network work.
    - Validation: 16 UI/scoring cases passed, then 2 popup/result tests passed after adding owned weight checkpoints. Build passed. No live calls or automatic output.

- [x] 11. Integrate tray access, operation ownership and shutdown
    - **Objective:** Ensure experiments cannot capture/insert concurrently with normal app work.
    - **Location:** `src/Program.cs` → `BisikApplicationContext`, form commands, main/fast/latest hotkeys, VAD, `DrainShutdownAsync`; `src/UI/TrayIcon.cs`.
    - **Implementation:** Add Experiments tray action with singleton restore/activate behavior. Composition root injects experiment recorder/service/runner/store; use a suite-owned HttpClient+HttpMetricsHandler with infinite client timeout and explicit stage deadlines, no-op sounds, and no normal PhaseChanged output subscription. Introduce an operation gate for active experiment capture/batch versus normal dictation/VAD/settings connection tests/Apply. A merely idle Experiments window need not block dictation. Gate all main/fast/latest paths; history browsing is allowed. Close cancels and asynchronously drains before disposal; app exit owns the experiment task before any HTTP/recorder disposal. Unsubscribe callbacks/stop timers and reject stale run progress.
    - **Dependencies:** 7, 8, 9, 10.
    - **Validation:** One window, mutually exclusive capture/tests, all hotkey paths blocked while busy, settings unchanged, close/quit during capture/request/save, no background tasks or ObjectDisposed callbacks.
    - [x] 11.1. Wire tray/window creation and shared operation ownership guards.
    - [x] 11.2. Add owned experiment cancellation/draining to form close and app shutdown.
    - [x] 11.3. Add integration/STA tests with delayed fakes and assertions for no output dispatch.
    - Validation: 37 ownership/form/service tests passed and build passed. Runtime hotkey guards inspected. Installed-app tray/hotkey checks remain unrun; installation unchanged.

### Phase 4: Shared diagnostics, recovery and first-version verification

- [x] 12. Reuse the runner in fixture-driven diagnostics
    - **Objective:** Support reproducible AI-agent/CLI evaluation without another recording or another scheduler.
    - **Location:** `tools/Bisik.Diagnostics/Program.cs`, `LatencyDiagnostics.cs`, `ComparisonDiagnostics.cs`; `src/Core/BenchmarkOptions.cs`; tasks 7–8.
    - **Implementation:** Add explicit experiment-definition/raw-transcript fixture input and optional saved WAV replay. Invoke the same Core matrix/runner/scoring; preserve existing command behavior and synthetic corpus. Distinguish supplied human content from synthetic fixtures; retained text is an explicitly requested separate quality artifact, timing receipts are content-free. Use SettingsManager.LoadEncryptedCopy only for explicit CLI source, never migrate it. Extend comparisons with full input/config/scoring identity, quality components, failures, token/timing data and nullable unavailable stages. Keep existing live/file/delay/pause/speculation/prewarm comparisons; do not rerun ASR per cleanup row. Include held-out technical/ordinary English/Indonesian/mixed/negation/quantity fixtures without copying the full benchmark into prompts.
    - **Dependencies:** 7, 8.
    - **Validation:** CLI/UI identical definitions yield identical scheduling/ranking; one raw input; source unchanged; truthful labels; old commands remain valid; missing stages not zero.
    - [x] 12.1. Add fixture/definition entry points backed by the shared runner.
    - [x] 12.2. Extend safe quality/timing comparison artifacts and fixture identity.
    - [x] 12.3. Add parser/CLI tests with fakes and a small held-out test corpus.
    - Validation: 8 CLI/runner cases passed; 6 fixture/parser cases rerun after adding raw identity and direct-run scheduling comparison. Saved WAV recognizes once; live model comparisons postponed.

- [x] 13. Preserve normal dictation raw text after cleanup failure or timeout
    - **Objective:** Prevent losing a long successful recognition when cleanup fails; recovery stays explicit.
    - **Location:** `FinalTextSelection.SelectAsync`, `TranscriptionSession.SelectFinalAsync/CompleteCoreAsync`; `TranscriptionService.TranscribeCoreAsync`; `LatestResultStore`; `Program.CompleteRecordingCoreAsync/InsertLatestAsync`; `TrayIcon.AddResultCommands`, `ReviewPopupForm`.
    - **Implementation:** Expose an immutable authoritative raw recovery snapshot before cleanup begins; do not recover partial/late STT. Distinguish cleanup failure/deadline from user cancellation/shutdown. General failure can return retained raw, but timeout uses the cancellation path and must read the session snapshot before ResetSession; merely changing catch:230 is insufficient. Add a labelled raw-recovery pending kind while retaining original failure status. Automatic claims/output remain success-only. An explicit recovery command opens review/copy or deliberately claims raw for insertion into a newly validated target. Never automatically copy/paste, fake successful cleanup/history, or relax OutputHandler focus/clipboard checks. Reuse atomic claims/dismissal so old recovery completion cannot clear a newer result.
    - **Dependencies:** 2.
    - **Validation:** HTTP/parse failure and cleanup deadline independently recover final raw; user/shutdown cancellation and late STT do not auto-recover; no automatic insertion/clipboard; explicit recovery exactly once, new target validation, newer publication survives.
    - [x] 13.1. Add authoritative raw snapshot and explicit cancellation/failure provenance.
    - [x] 13.2. Add labelled pending recovery and explicit review/insertion path without success fabrication.
    - [x] 13.3. Extend service/session/output/claim tests for failure, timeout and races.
    - Validation: 59 recovery/service/output regressions passed; 3 focused recovery cases rerun after excluding completed fast selection. Automatic claims reject recovery; explicit commands preserve failed provenance and newer publications.

- [x] 14. Validate and document the complete first version
    - **Objective:** Make the core feature reviewable and verifiably compatible.
    - **Location:** `AGENTS.md`, `README.md`, `VERIFICATION.md`, `sample.settings.json` if applicable; `tests/Bisik.Tests`.
    - **Implementation:** Document new files, workflow, same-take limitations, scoring formula/versions, judge cost/request counts, warnings without exclusion, retention/export and diagnostic commands. State that one sample is provisional and small-sample p95 is not a reliable tail estimate. Add integration tests proving one capture/recognition, identical raw across matrix, no auto-apply/output, major-error eligibility, partial saves/reopen, invalid efforts, shutdown and zero/missing weights. Manual checklist: real silence/speech, mic disconnection, 100/150/200% DPI, constrained display, keyboard-only operation and configured provider comparisons. Keep manual/live items unrun until actually verified; do not require unavailable cloud access to claim unit tests passed.
    - **Dependencies:** 1–13.
    - **Validation:** Focused/full suite and build pass with registration disabled; diff check passes; documentation matches effective behavior; all unrun checks identified separately.
    - [x] 14.1. Update project structure, behavior/configuration/export/scoring documentation and placeholder-only samples.
    - [x] 14.2. Add/run cross-component regressions and full build/test/diff checks.
    - [x] 14.3. Record manual/live acceptance status explicitly without altering installation or cloud resources.
    - Validation: full solution build/test passed (336 tests, no skips), registration disabled; staged diff check passed. Manual/live acceptance is explicitly unrun/postponed in VERIFICATION.md; no installed/cloud changes.

### Phase 5: Earlier code-related quality and latency recommendations

These are additive followups from the original research, not requirements to vary transcription in the first-version matrix. Keep optional modes off and preserve the completed-file/full-text rollback. Each has its own validation and external dependencies; no performance gain is presumed.

- [ ] 15. Add reusable project vocabulary to cleanup settings
    - **Objective:** Improve technical identifiers and allow controlled prompt/glossary comparisons.
    - **Location:** `PostProcessorSettings`, `AppSettings.Snapshot`, `SettingsManager.NormalizePostProcessor`; `PostProcessorPresets.ResolveSystemPrompt`; `SettingsForm` Post-Processor tab; `HistoryConfigurationSnapshot`; `SpeculativeCleanup.FinishAsync`.
    - **Implementation:** Optional small named vocabulary profile, default empty. Compose canonical spellings/context-dependent variants after resolving the chosen preset; custom prompt remains verbatim when no profile is selected. Include exact HAPPI/VtrinLib/cMiaServer/API-versus-tool examples as an optional editable template, not universal replacements. Never inject full reference scripts. Preserve stable glossary ordering, deep snapshots and effective prompt/history/cache identity; vocabulary edits invalidate cleanup caches, not verified reasoning support. Evaluation checklist and runtime vocabulary are distinct settings. No regex global replacement of normal happy/unrelated libraries.
    - **Dependencies:** 1, 3, 4, 9.
    - **Validation:** Old settings/default prompts unchanged; every preset handles optional profile; ordinary happy preserved; exact identifiers/paths and ambiguity tests; snapshot/cache invalidation and safe history exports.
    - [ ] 15.1. Add profile data/defaults/normalization and post-preset prompt composition.
    - [ ] 15.2. Add native profile controls/template and identity/history propagation.
    - [ ] 15.3. Extend settings/prompt/history/speculation tests and documentation.

- [ ] 16. Add capability-gated recognition context for compatible models
    - **Objective:** Repair domain terms before cleanup without breaking current Whisper.
    - **Location:** `ProviderSettings`, `AppSettings.Snapshot/CopyProvider`; `SettingsManager.NormalizeProvider`; `AzureRealtimeProtocol.SessionUpdate`; `RealtimeTranscriptionStream.StartAsync`; `AzureOpenAIProvider.UsesRealtime`; `RealtimePrewarmer.Key`; `SettingsForm`; safe snapshots; fixture diagnostics.
    - **Implementation:** Optional typed prompt/keywords/expected-language fields and exact per-field capability evidence. Defaults omit new fields. Separate realtime transport selection from supported prompt/delay/language properties; a generic AzureRealtime flag must not imply Whisper delay support. Current gpt-realtime-whisper GA must not receive unsupported prompt. New gpt-live-transcribe routing can use explicit realtime configuration plus verified field support; aliases must not be guessed from names. The recorded-file transcriber already delegates through the same stream builder: do not duplicate protocol. Include resolved context in prewarmer/cache/session identity and deep-copy arrays. Add context to multipart providers only where a documented/probed contract supports it. Diagnostics can compare en versus auto, or verified en/id hints, on identical saved audio; first-version experiments still use one selected ASR configuration.
    - **Dependencies:** 12, 15; live compatibility requires E1/E2.
    - **Validation:** Captured WSS/multipart payloads omit unsupported fields; forceRealtime does not force delay; current Whisper/file fallback unchanged; context changes retire warm slots; no full-reference injection; newer model tests use fakes.
    - [ ] 16.1. Add context/field-capability settings, validation and explicit transport/field selection.
    - [ ] 16.2. Thread supported hints through shared protocol, identity, UI and diagnostics.
    - [ ] 16.3. Extend realtime/provider/settings/prewarmer/history tests and document unverified live contracts.

- [ ] 17. Add completion usage telemetry and optional validated streaming
    - **Objective:** Distinguish queueing/first text/generation/reasoning and expose truthful progress.
    - **Location:** `LLMPostProcessor.ProcessAsync/ParseChatCompletionAsync`; `HttpMetricsHandler`; `SessionMetrics`; task 2 receipts and experiments/diagnostics.
    - **Implementation:** Add nullable typed prompt/output/reasoning/cached/prediction-token counts when returned. Header arrival remains separate from first generated content. Optional capability-gated SSE parser accumulates privately, supports bounded deltas/final usage, and accepts only complete validated text with stop/refusal/truncation checks equivalent to nonstreaming. No partial clipboard/insertion; streaming is measurement/progress, not guaranteed faster final delivery. Preserve string-returning API via a shared richer internal completion result; don't reparse/call a second request. Track retries/statuses without URLs, bodies, transcript or credentials in timing logs.
    - **Dependencies:** 2, 7, 12; live streaming contract requires E2.
    - **Validation:** Split/UTF-8/SSE frames, missing usage, EOF before finish, refusal/filter/truncation, cancellation, bounded accumulation, first-text versus headers and no content leaks.
    - [ ] 17.1. Add shared usage/first-content metadata to complete-response parsing and typed receipts.
    - [ ] 17.2. Add optional SSE parsing/private accumulation and stage progress through the service.
    - [ ] 17.3. Extend parser/metrics/runner tests and comparisons; document nullable/unverified fields.

- [ ] 18. Add an opt-in predicted-output cleanup comparison
    - **Objective:** Measure reduced regeneration for mostly unchanged transcripts where supported.
    - **Location:** `PostProcessorSettings`, `LLMPostProcessor.BuildRequestPayload/BuildAzureUri`; experiment variant identity/UI; task 17 receipts.
    - **Implementation:** Optional verified prediction capability bound to exact model/API; send raw transcript, never reference, as prediction content. Keep off by default; reject unknown/unsupported routes. Current exported 2024-06-01 route is not presumed compatible and GPT-6 support is not assumed. Expose as an explicit advanced comparison variant so prediction on/off has distinct identities. Record accepted/rejected token counts and complete latency, retaining the same final-output validation. Do not auto-enable or shorten output caps to manufacture speed.
    - **Dependencies:** 3, 12, 17; runnable compatible deployment/API requires E1/E2.
    - **Validation:** Supported payload uses raw; disabled legacy request unchanged; invalid capability/API blocked; prediction variants deduplicate correctly; unsupported/ignored evidence not labelled proven; quality/latency remain separately reported.
    - [ ] 18.1. Add scoped option/capability and payload/identity propagation.
    - [ ] 18.2. Add explicit diagnostic/advanced experiment variants and usage comparison.
    - [ ] 18.3. Add request/identity/complete-output tests and compatibility documentation.

- [ ] 19. Implement a bounded stable-segment cleanup diagnostic
    - **Objective:** Test overlapping cleanup with recording beyond existing exact whole-text speculation.
    - **Location:** **new** `src/Core/SegmentedCleanup.cs`; `LiveAudioQueue`, `RealtimeTranscriptionStream` completed-prefix events, `TranscriptionSession`, `SpeculativeCleanup`; fixture diagnostics.
    - **Implementation:** Diagnostic-only opt-in coordinator receives acknowledged ordered stable completed prefixes, identifies each immutable raw segment once, uses bounded recent raw context and request budget, and cleans the final tail after Stop. Do not blindly append cumulative snapshots. Verify final raw-prefix identity/coverage; corrections, configuration changes, unsupported prompts or failed/incomplete segments force full-text fallback inside the same remaining deadline. Preserve output order, later self-correction/translation/glossary caveats, owned cancellation and final-only selection. Existing whole-text speculation stays unchanged/off by default. The first-version after-Stop matrix is unchanged; evaluate this mode on paced saved-audio replay with the same reference/judge. No production promotion based only on mocks.
    - **Dependencies:** 2, 12, 15, 17; empirical quality/latency requires E1/E2.
    - **Validation:** No repeated/omitted prefix, reordered completions, late correction, stale settings, segment error, cancellation and bounded budgets; complete fallback only once within deadline; whole-text baseline remains available.
    - [ ] 19.1. Add stable-prefix segment coordinator and explicit fallback/cancellation rules.
    - [ ] 19.2. Add diagnostic live-replay mode and comparable overlap/tail/request receipts.
    - [ ] 19.3. Add race/coverage/fallback tests and document why live promotion remains deferred.

- [ ] 20. Extend existing instructed-audio diagnostics for matched fixtures
    - **Objective:** Evaluate the earlier one-stage alternative without replacing production routing.
    - **Location:** `tools/Bisik.Diagnostics/InstructedAudioDiagnostics.cs` → `BuildPayload/RunAsync`; task 12 fixtures/evaluation; existing audio request tests.
    - **Implementation:** Reuse the existing instruction-following audio Chat Completions adapter, not the dedicated transcription session. Make supported model/deployment selectable with explicit callable-contract evidence, and label human versus synthetic fixtures truthfully. Current code fixes gpt-audio and limits one minute/8 MiB; all six original takes exceed a minute. Keep bounds explicit and only extend them to verified model/API limits; otherwise report the unsupported fixture without silently truncating it. Evaluate against the same reference/rubric and show full upload/generation timing versus the live two-stage baseline. Prior 404 is unsupported access, not a quality score. No production provider switch.
    - **Dependencies:** 5, 12, 17; actual audio model/API access requires E1/E2.
    - **Validation:** Supported model option/payload, reference never used as answer instructions, oversized/long fixture rejection, no misleading synthetic labels, safe 404, complete output, no normal routing/installation changes.
    - [ ] 20.1. Add model/fixture selection and documented contract-aware bounds.
    - [ ] 20.2. Reuse fixed evaluator and safe comparison artifacts for one-stage outputs.
    - [ ] 20.3. Extend diagnostic request/bounds/label tests and retain explicit unsupported receipts.

- [ ] 21. Prepare the conditional Azure Speech provider comparison (POSTPONED by user)
    - Blocker: E4 requires user-supplied existing service/access and exact API contract. All adapter work is postponed; continue to item 22.
    - **Objective:** Account for phrase-list/Custom Speech alternatives as a separate recognition service.
    - **Location:** **new** `src/Providers/AzureSpeechProvider.cs`; `ITranscriptionProvider`; `ProviderType/ProviderSettings`; `Program.BuildProviderMap`; `SettingsForm`; fixture diagnostics.
    - **Implementation:** This task is blocked until E4 identifies an existing authorized Speech endpoint and exact supported transcription/hint contract. Then implement a thin file-based provider using built-in HTTP or a justified SDK, existing service retry/deadline boundaries and DPAPI-protected connection settings. Add phrase hints only to the chosen documented supporting API; an existing Custom Speech deployment is configuration, not training/provisioning code. Handle supported audio/duration/locale limits and normalize the returned final text. Register provider/UI/docs and compare identical saved WAVs in diagnostics, not additional ASR permutations in first-version Experiments. Do not create resources or claim OpenAI Whisper supports Speech phrase lists.
    - **Dependencies:** 12, 16; **external prerequisite E4**.
    - **Validation:** Verified-contract mocked request/parse/limits/auth/cancellation tests; old provider settings unchanged; secret protection and fallback errors; live Speech quality explicitly unrun until authorized access exists.
    - [ ] 21.1. Establish the selected existing service contract and record E4 before coding an adapter.
    - [ ] 21.2. Implement thin provider, protected settings, registration/UI and diagnostic selection.
    - [ ] 21.3. Add provider/settings/fixture tests and document external training/deployment dependency.

- [ ] 22. Add an opt-in Azure DevOps evaluation job template
    - **Objective:** Make fixture evaluation repeatable without automatic paid CI runs.
    - **Location:** **new** `tools/azure-pipelines-experiments.yml`; task 12 CLI; README/VERIFICATION.
    - **Implementation:** Manual-trigger Windows job invoking build/tests and the shared experiment CLI against explicitly supplied private fixtures/encrypted settings or secret-backed test configuration. Desktop DPAPI settings are not portable to a different CI host/account: require a compatible preprovisioned source, or construct secret-backed settings only in agent memory and use `SettingsManager.Save` to create agent-owned encrypted scratch settings. Never write plaintext secret JSON. No hardcoded keys/endpoints/models, no personal fixture commits, no active desktop settings/launcher mutation. Parameterize paths/labels/repeats; validate paths and publish only content-free summaries by default. Judge/output artifacts require an explicitly configured private destination. Preserve failures/unavailable access as failures, not zero-time samples. This file is source only; do not register/run a pipeline or install/configure agents.
    - **Dependencies:** 12; live execution requires E1/E3.
    - **Validation:** Template structure/arguments and mock CLI invocation validated locally; no automatic trigger or secret/content publication; document required agent/secret/artifact setup and mark real pipeline execution unrun.
    - [ ] 22.1. Add parameterized manual-only job template using existing CLI commands.
    - [ ] 22.2. Add safe publication/failure behavior and external setup documentation.
    - [ ] 22.3. Validate syntax/command generation with fixtures/mocks and record unrun pipeline status.

## C. Dependencies and execution order

First-version order: 1 → 2 → 3; 4 can follow 1 independently; then 5 and 6 can proceed independently once their prerequisites pass; 7 → 8 → 9 → 10 → 11. Task 12 follows 7/8 and can run alongside UI work. Task 13 follows 2, but coordinate its shared service/Program edits with 11. Task 14 closes the first version after 1–13. Preserve every task's checkbox state across sessions.

Followups: 15 after its prerequisites; 16 follows 12/15; 17 follows 2/7/12; 18 follows 17; 19 follows 12/15/17; 20 follows 5/12/17. Tasks 16/17/18 share request/settings files: do not edit those concurrently without coordination. Task 21 is explicitly blocked by E4; task 22's source can be written after 12 without provisioning anything. Phase 5 does not block first-version task 14.

Independent reading/test work may be delegated; implementations sharing files should remain sequential. Each new optional field/mode needs its own defaults, snapshot/export identity, regression tests and documentation. Never reset/renumber completed IDs or mark an external/live condition complete from mocked tests.

## D. External dependencies, excluded and deferred recommendations

| ID / recommendation | Boundary and reason |
|---|---|
| E1: Callable models and credentials | Human/admin supplies authorized existing deployments and access. Earlier smaller-model/audio probes returned 404. Tasks implement capability/error handling, not resource creation, key acquisition or default-model selection. |
| E2: Live provider/model compatibility and empirical benchmarks | Exact Azure routes, hints, streaming/prediction/audio support and performance need authorized calls. Mock tests establish code behavior only. Lowest documented effort/smaller size is an experiment, not a guaranteed speedup. |
| E3: Azure DevOps agent/secrets/private artifacts | Agent setup, pipeline registration, secret stores, private output policy and paid execution are external. Task 22 provides source/commands only; no automatic cloud run. |
| E4: Azure Speech choice/resource/Custom Speech training | Existing endpoint/credential/API selection and Custom Speech training/deployment require external setup. Task 21 accounts for adapter work but stays unchecked while its exact contract/access prerequisite is unresolved. |
| Region/PTU/dedicated capacity/APIM changes | Portal configuration, deployment moves, capacity purchases and gateway policies are excluded. Tasks 12/17 provide client evidence; correlate externally with APIM Duration/BackendDuration and Azure service metrics before spending/changing infrastructure. |
| Prompt caching | Stable prompt/profile ordering and cached-token receipts are included in 15/17. Explicit cache breakpoints/TTL policy tuning is deferred: the current prompt is short and the contract/cost is model-specific. Do not pad prompts or create a new caching service. |
| Existing prewarming/delay/pause/speculation | Already implemented; preserve and compare through 12/16. No new editor-prewarming subsystem or automatic enablement. Existing two-call whole-text speculation cannot promise to hide this cleanup bottleneck; 19 is the separate opt-in experiment. |
| Production promotion of prediction/segments/one-stage/Speech | Deferred pending matched quality/latency evidence and explicit choice. Code/diagnostic work is represented in 18–21; production rollback remains available. |
| Alternative provider connections, automatic winner application, browser UI | Excluded: user chose one connection, did not request automatic settings/output changes, and native WinForms is required. |
| Cost as an extra ranking factor | Excluded from initial scoring: not an agreed factor and exact billing/price metadata is unavailable. Request/token counts are transparent; no invented currency estimates. |

Original findings informing this plan: cleanup averaged about 14.77s versus approximately one second shared transcription tail on six live recordings; the reference contains HAPPI, VtrinLib, cMiaServer, FetchClassData/FetchGraphDataEx/CommitClassData, fetch_class_data, GetInstancesWithSecurityChecking and RTDB. Fluent output still rendered those incorrectly. Original WAVs/analysis are local fixtures, not committed sample data. Replaying identical raw/WAV inputs controls tired delivery; judge/reference scores still measure fidelity to this script/take, not universal model superiority.

Useful contract references for conditional tasks: [Azure model catalog](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure), [transcription field restrictions](https://learn.microsoft.com/en-us/javascript/api/@azure/ai-projects/voiceagentinputtranscription), [OpenAI model/effort guidance](https://developers.openai.com/api/docs/guides/latest-model), [Azure predicted outputs](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/predicted-outputs), [latency](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/latency), [Speech phrase lists](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/improve-accuracy-phrase-list). Refresh exact contracts when implementing conditional API work; public documentation is not deployment entitlement.

Before declaring this plan executed, verify every required parent/subtask is checked with its specified evidence, external blockers/unrun tests remain explicit, scope/defaults match behavior, and existing progress is preserved. Creating this checklist does not authorize executing it.
