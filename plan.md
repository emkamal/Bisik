# Bisik latency improvement plan

## Goal and scope

Reduce the time from **toggle-off to final text inserted**, without losing the first/final words, changing the intended meaning, or inserting into the wrong field. Keep microphone startup immediate. Preserve contextual correction and English/Indonesian mixed-language translation; make bypassing cleanup an explicit fast mode.

This is an implementation checklist, not completed work. It covers code, tests, diagnostics, and comparisons using already accessible services. Creating Azure resources/deployments, changing cloud capacity/routing policies, obtaining credentials, buying hardware, and installing the finished build are outside scope. Experimental work must remain isolated until supported by measurements.

The 2026-11-06 audit found:

| Existing behavior | Consequence |
|---|---|
| Stop capture, save WAV, create Azure session, upload, finalize transcript, run cleanup, then output | Almost all remote work starts after the user stops speaking |
| Saved configuration uses `gpt-realtime-whisper`, then `gpt-6-astra` with custom bilingual cleanup | Preserve these existing selections; compare alternatives without silently replacing them |
| Three recent sessions: transcription 4.52/6.16/11.82 s; cleanup 3.15/7.67/6.19 s | Combined 7.66/13.83/18.01 s; small sample, excluding local/output overhead |
| `AudioChunkAvailable` and realtime streaming implementation exist but are not connected during recording | Reuse this foundation instead of implementing another transport |
| Success audio uses `PlaySync()` before output | Adds 180 ms of audio plus startup overhead |

Target flow: **capture + stream + recognition while speaking; optionally prepare cleanup during pauses; on Stop, drain final audio, finalize, finish/reuse valid cleanup, then perform one guarded insertion.** No numerical latency promise until measured.

## Architecture and starting points

Native WinForms, C#/.NET 8, NAudio, shared `HttpClient`, Win32 hotkeys, UI Automation target validation. Read `AGENTS.md` and `README.md` first; preserve existing uncommitted changes. Paths below are repository-relative to `D:\Dev\Bisik`; method names are more durable than line numbers.

| Area | Files and relevant entry points |
|---|---|
| Application/session lifecycle | `src/Program.cs`: `StartRecording`, `CompleteRecordingAndTranscriptionAsync`, `ResetSession`, `ExitThreadCore`, `OnTranscriptionPhaseChanged`; `BuildProviderMap` is the actual provider factory |
| Audio capture | `src/Core/AudioRecorder.cs`: `AudioChunkAvailable`, `StartRecording`, `StopRecordingAsync`, `FinalizeRecordingAsync`; `AudioChunkEventArgs.cs` carries actual sample rate |
| Pipeline ownership | `src/Core/TranscriptionService.cs`: `TranscribeCoreAsync`, `TranscribeWithRetryAsync`, `TranscriptionResult`; owns provider + postprocessor orchestration, not recorder/UI |
| Azure transport | `src/Core/IRealtimeTranscriber.cs`, `RealtimeTranscriptionStream.cs`; `src/Providers/AzureRealtimeProtocol.cs`: secret creation, routing, session update; `AzureRealtimeTranscriber.cs`: current file adapter; `AzureOpenAIProvider.cs`: realtime detection/fallback |
| Cleanup | `src/PostProcessor/LLMPostProcessor.cs`: payload/response parsing; `PostProcessorPresets.cs`; `IPostProcessor.cs` |
| Settings | `src/Models/AppSettings.cs` contains **all** settings records, `ProviderType`, and manual `PostProcessorSettingsJsonConverter`; `src/Core/SettingsManager.cs` handles DPAPI; `src/UI/SettingsForm.cs` handles controls/Apply |
| Output/recovery | `src/Core/OutputHandler.cs`, `WindowsTextOutput.cs`, `WindowsTextOutputPlatform.cs`, `WindowsClipboard.cs`; `src/UI/ReviewPopupForm.cs`, `OverlayWindow.cs`, `TrayIcon.cs`; `HotkeyManager.cs` |
| Persistence/feedback | `src/Core/HistoryLogger.cs`, `SoundFeedback.cs`; `src/Models/TranscriptionEntry.cs`; `src/UI/HistoryForm.cs` |
| Existing verification | `tests/Bisik.Tests/`; `tools/Bisik.Diagnostics/{Program.cs,AzureDiagnostics.cs,FocusDiagnostics.cs,New-SyntheticSpeech.ps1}`; `VERIFICATION.md` |

`Core` must not reference UI. Only `TranscriptionService` combines provider and postprocessor knowledge. The composition root may construct concrete services. Keep the file-provider contract for multipart/local/custom providers; introduce streaming as an optional capability. If adding files/classes, follow `AGENTS.md` and update its project map where relevant.

## Execution and completion rules

Work on one numbered parent item at a time, in order unless its dependencies are satisfied and another item is independent. Search for existing helpers before adding one. Mark a subitem `[x]` only after its stated behavior/check is verified; mark its parent only when every child is complete. Add a short evidence note with the command/result or receipt path. Leave unavailable live experiments `[ ]` with a concrete blocker; continue independent coding tasks. Dependencies require prerequisite code/offline checks, not unavailable optional live comparisons; enabling defaults still requires the relevant validation. A skipped check is not a pass.

Do not commit or replace the installed application automatically. Use ignored `artifacts/` for scratch files and reports. Keep keys/endpoints/headers encrypted, including scratch configurations; follow the diagnostic tool's encrypted-copy loading pattern because `SettingsManager.Load()` can migrate files. Do not put real dictation, field contents, keys, tokens, or endpoint values in timing reports.

Preserve these invariants throughout: final text only; one selected final text per session and no duplicate paste, with explicit recovery after non-inserting fallback; no Enter/backspacing/automatic refocus; sticky original-target validation; clipboard sequence ownership; no provider failures returning partial text; no silent audio loss; cancellation observes/disposes background work. New settings must round-trip and load safely when absent. Keep experimental features off by default; retain a file-path fallback for rollout of live capture.

Useful existing commands, run from the repository root:

```powershell
dotnet test Bisik.sln --nologo --verbosity minimal
dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter FullyQualifiedName~RealtimeTranscriptionStreamTests
dotnet build tools/Bisik.Diagnostics/Bisik.Diagnostics.csproj
powershell -NoProfile -File tools/Bisik.Diagnostics/New-SyntheticSpeech.ps1 -OutputPath artifacts/verification/synthetic.wav
# Explicit live call using existing settings and synthetic audio; no cleanup:
dotnet run --project tools/Bisik.Diagnostics -- --azure-once artifacts/verification/synthetic.wav
git -c core.whitespace=cr-at-eol diff --check
```

Extend the relevant existing test classes instead of duplicating harnesses. Tests use fake capture/WebSocket/output seams; they do not prove microphone quality, deployed-model compatibility, or app acceptance of a paste. Keep build artifacts separate if a running executable locks an output; do not kill unrelated processes. New benchmark switches mentioned below must be implemented before use.

## Numbered checklist

- [x] **1. Establish regression and timing baselines.** Start with `TranscriptionService`, `Program`, output code, and diagnostics.
  - [x] **1.1** Record the starting Git diff/status and current test result under `artifacts/latency/`; distinguish pre-existing failures. Preserve all existing changes.
  - [x] **1.2** Introduce per-session monotonic timings: capture start/stop request, capture drained, secret creation, WSS ready, queued/sent bytes, first partial, each commit/final completion, cleanup start/end, history, sound, paste dispatch, and clipboard restoration. Record counts/retries/outcomes, not content. Existing `duration_ms` is transcription + cleanup only; do not silently redefine it.
  - [x] **1.3** Add an injectable metrics sink consumed by diagnostics and a bounded, opt-in app trace. Distinguish Stop-to-ready from Stop-to-paste; unavailable stages must be null/not applicable, not zero. Include audio duration and a nonsecret configuration label.
  - [x] **1.4** Extend diagnostics with a non-inserting full-pipeline benchmark, repetition limit, cancellation, and JSON report. Test parsing/aggregation and stage ordering offline. Record a baseline with the existing configured endpoint when accessible; Settings' current Test Connection disables cleanup and is not a substitute.
  - Evidence: baseline 171 tests passed; item 1 now 178 passed. Diagnostic build passed. artifacts/latency/starting-*, item-1-tests.txt, baseline-file.json: one 6.765 s English fixture, 8.671 s Stop-to-ready including saved cleanup; no insertion. App trace and bounded reports tested offline. One sample is not a latency distribution.

- [x] **2. Introduce a reusable live-session boundary.** Depends on 1; edit service/provider interfaces, composition root, and session tests.
  - [x] **2.1** Add optional streaming capability alongside `ITranscriptionProvider.TranscribeAsync`. Have `TranscriptionService` create/manage the live pipeline through injected interfaces, reusing `IRealtimeTranscriber`/`RealtimeTranscriptionStream`. Route Azure realtime using the existing detection rules; leave other providers on their file path.
  - [x] **2.2** Share the final-transcript-to-cleanup/result path between file and live transcription. Avoid a second transcription request, duplicate history, or separate cleanup behavior on live success.
  - [x] **2.3** Give each dictation an identity, immutable settings snapshot, and cancellation ownership. Define recording, stopping, processing, ready, cancelled, and failed transitions. Reject stale callbacks/results from earlier sessions.
  - [x] **2.4** Test capability selection, unchanged file providers, empty results, cancellation/late completion, and exactly-once completion using service fakes. Add a reversible setting for live versus completed-file capture; initially preserve missing-field behavior until rollout verification.
  - Evidence: artifacts/latency/item-2-tests.txt: 37 focused tests passed; optional Azure capability, immutable collection copies, default/file path, cancellation of late finals, empty final, and one cleanup/completion verified. liveCapture defaults false.

- [x] **3. Connect microphone capture to live upload.** Depends on 2; use `AudioRecorder`, `AudioChunkEventArgs`, streaming interfaces, and capture/stream test seams.
  - [x] **3.1** Subscribe before capture starts, including VAD pre-roll. Start the microphone immediately and establish Azure setup concurrently; never await network readiness before listening. Unsubscribe exactly once during cleanup.
  - [x] **3.2** Feed copied PCM chunks into one ordered, bounded queue with one asynchronous sender. Keep network awaits off capture callbacks and UI. Define the byte/time bound and fail or fall back explicitly on overflow; never discard speech silently.
  - [x] **3.3** Normalize continuously from the actual capture format to PCM16 mono 24 kHz. The existing `PushAudioChunkAsync`/upsampler assumes 16 kHz: adapt the contract or converter rather than ignoring `SampleRate`. Preserve converter state, pre-roll, sample alignment, and final samples.
  - [x] **3.4** Continue collecting the complete recording for history/file fallback. Test delayed setup, immediate Stop, slow upload, queue overflow, sample-rate variants, chunk ordering, and pre-roll; prove upload occurs before Stop without delaying microphone startup.
  - Evidence: artifacts/latency/item-3-tests.txt: 55 focused passes; item-3-preroll-tests.txt: pre-roll queue/file ordering passed. Queue copies PCM, bounds queued/in-flight bytes to 10 seconds/2 MiB, faults on overflow, and drains one sender. Conversion parity/tail verified at 8/16/24/44.1/48 kHz. Opt-in Settings live capture; microphone starts before setup.

- [x] **4. Finalize, cancel, and recover live sessions correctly.** Depends on 3; extend `Program`, service, capture lifecycle, and realtime tests.
  - [x] **4.1** On Stop: await `RecordingStopped`/last callbacks, close the producer, drain sends, flush conversion, commit remaining audio, await acknowledgement of every issued commit, then every item's authoritative final text in audio order. Preserve duplicate/out-of-order and empty-completion handling.
  - [x] **4.2** Separate capture drain from optional file persistence so a live-success path can finalize recognition without waiting for WAV export/reopen. File providers still await a complete valid file. Own buffers/tasks until persistence completes.
  - [x] **4.3** Keep the five-minute recording limit separate from one two-minute post-Stop deadline covering drain, live finalization, file fallback/retries, and cleanup; never restart that budget on fallback. Cancel/dispose during setup, capture, queue drain, finalization, cleanup, and shutdown; observe all tasks and release the microphone/socket.
  - [x] **4.4** Centralize recoverable stream failure/fallback policy in `TranscriptionService`. If a complete recording is usable and the session was not cancelled, permit at most the bounded configured file retry; record it and suppress abandoned live results. Never retry after explicit cancellation or accept partial text on failure.
  - [x] **4.5** Test final-tail delivery, stop/failure races, reconnect/fallback, no duplicate cleanup/history/output, repeated recordings, and settings changes between sessions. Run the synthetic live-versus-file benchmark and record results separately from offline passes.
  - Evidence: artifacts/latency/item-4-tests.txt: 57 focused passes, including tail drain, authoritative finals, cancellation, empty final, one fallback/cleanup/history, and owned export. item-4-live.json vs item-4-file.json: identical 6.765 s English fixture and saved cleanup, 4.397 s vs 8.998 s Stop-to-ready, one sample each; no insertion. One post-Stop budget includes fallback; shutdown drains owned processing/disposal.

- [ ] **5. Add transcription delay tuning and stable transcript snapshots.** Depends on 4; edit settings, `AzureRealtimeProtocol`, stream events, and relevant tests.
  - [x] **5.1** Add an optional transcription-delay setting with unset/default omission plus documented supported values; serialize it and expose it in Settings. Do not assume omitted means `medium`. Send it only for the applicable realtime capability.
  - [x] **5.2** Add optional client-side pause-boundary intermediate commits using existing audio-level/`VoiceActivityTracker` logic and `CommitIntermediateSegmentAsync`. Enqueue commit markers in the same ordered queue as audio so they cannot overtake buffered speech. Use separate detector state from VAD recording-stop logic; avoid empty/tiny commits and retain a final Stop commit. Keep server turn detection unchanged.
  - [x] **5.3** Expose ordered snapshots of authoritative completed transcript items, separately from partial previews. Deduplicate snapshots and retain the current complete-final reconciliation. Test pauses, ongoing speech, out-of-order completions, silence, and the final phrase.
  - [ ] **5.4** Benchmark omission versus `low` and optional phrase commits using available deployment capabilities. Measure deltas before the first commit and final latency after Stop; do not assume recognition starts only at commit. Record English/Indonesian quality alongside timings.
  - Evidence: artifacts/latency/item-5-tests.txt: 48 focused passes. item-5-low.json: English low + pause commits, 5.505 s Stop-to-ready vs prior omission 4.397 s; one sample, no improvement claim. 5.4 blocker: installed synthesis voices are en-US only, so Indonesian/mixed-language quality comparison remains untested. Delay values and request shape verified from official Azure microphone documentation. Defaults remain omitted/off.

- [ ] **6. Add model-aware cleanup controls and comparisons.** Depends on 1; start in `AppSettings.cs`, `LLMPostProcessor`, `SettingsForm`, and `PostProcessorTests`.
  - [ ] **6.1** Add optional reasoning effort and explicit/verified capability metadata for the selected model/API. Default to omission. Deployment aliases alone do not prove supported parameters; validate combinations and preserve custom prompts verbatim.
  - [ ] **6.2** Update both read/write branches of `PostProcessorSettingsJsonConverter`, Settings Apply, and placeholder sample settings. Test old settings, round-trips, model changes, supported request payloads, and omission for unsupported providers.
  - [ ] **6.3** Tighten response handling for incomplete/truncated outputs before adding optional output limits. Never insert a clipped translation as a successful final result. Keep cancellation/errors distinguishable; test finish reasons and malformed/empty responses.
  - [ ] **6.4** Extend the benchmark to compare current cleanup, supported lower reasoning, and already accessible faster models using in-memory/scratch configuration overrides. Preserve the active model. Evaluate negation, technical names, English, Indonesian, and mixed-language whole-text translation; report latency and meaning errors separately.

- [ ] **7. Overlap whole-text cleanup with recording.** Depends on 5 and 6; ownership stays in `TranscriptionService`.
  - [ ] **7.1** Add an opt-in speculative cleanup coordinator consuming completed-transcript snapshots. Submit the entire accumulated text, not independently polished sentence fragments, so bilingual/custom-prompt semantics stay intact.
  - [ ] **7.2** Debounce/coalesce snapshots; bound speculative work to one active request plus the newest pending snapshot and a per-session speculative-request limit that reserves required final cleanup. Cache only within the session using exact raw text plus relevant cleanup settings. Observe cancelled/stale tasks; cancellation may not stop server-side work.
  - [ ] **7.3** At Stop, reuse a completed or already-running result only when its input/settings exactly match the authoritative final transcript. Otherwise cancel obsolete work and immediately run final cleanup without waiting for an obsolete remote result. Ignore late speculative results. Never output speculative text or automatically revise delivered text.
  - [ ] **7.4** Test exact-match reuse, last-word changes, later language switches, different custom prompts, out-of-order responses, empty speech, request limits, cancellation, and new-session isolation. Benchmark saved post-Stop time against extra requests; leave disabled unless results justify enabling it.

- [ ] **8. Remove confirmed local delivery overhead.** Depends on 1 and 4; use `SoundFeedback`, service, history, and retention tests.
  - [ ] **8.1** Move success feedback after the output decision and make playback nonblocking with owned player/stream lifetime. Sound failure must be logged without converting successful delivery into a transcription failure. Test using delayed/failing playback fakes.
  - [ ] **8.2** Measure history lock/write and audio export costs. If material, move persistence after delivery or into a managed queue; otherwise document measured cost and retain the simpler ordering. Do not add unobserved fire-and-forget tasks.
  - [ ] **8.3** If persistence is deferred, drain it during shutdown, surface failures, retain recording ownership until history is durable, and preserve failed/empty/no-history deletion rules. Test slow writes, cancellation, write errors, and repeated sessions without duplicate/missing history.

- [ ] **9. Retain completed results and support explicit insertion recovery.** Depends on 2; start in output classes, hotkeys, `Program`, overlay/review UI, and output tests.
  - [ ] **9.1** Keep one latest completed result in memory and show a nonactivating Ready/manual-insertion state. New finalized results replace the pending result; clear it after paste dispatch, dismissal, or exit. Clipboard-only fallback must not discard it.
  - [ ] **9.2** Add a separate configurable "Insert latest result here" hotkey/command, with conflict reporting and Apply-time registration. Atomically claim the pending-result identity; capture the current field/selection and use the existing guarded paste path. Prevent concurrent claims and clear only the dispatched identity, not a newer result. Preserve normal toggle/cancel bindings.
  - [ ] **9.3** Preserve clipboard ownership, modifier release, and protected/terminal/inaccessible-field handling. Keep original-session target invalidation sticky; never reactivate the old field automatically. Distinguish an initially unsupported target from one changed during processing.
  - [ ] **9.4** Report paste dispatched before clipboard restoration finishes, while still managing/serializing the restoration transaction through shutdown. Do not describe dispatch as proof the target accepted text. Keep the existing 50 ms release delay and 600 ms restoration grace.
  - [ ] **9.5** Test repeated recovery hotkeys, replacement during an older paste/restoration, failed insertion retention, competing clipboard copies, focus/caret changes, modifier holds, and overlapping clipboard transactions; prove no duplicate paste, lost newer result, or automatic submission.

- [ ] **10. Offer explicit fast output and optional progress preview.** Depends on 2 and 9; include speculative-work cancellation if 7 is implemented. Retain current cleanup defaults/preferences.
  - [ ] **10.1** Add a per-recording fast-mode command that skips cleanup without modifying the saved normal mode. Expose "Use transcript now" only after STT is final, using a hotkey or nonactivating interaction that does not itself invalidate the captured target.
  - [ ] **10.2** Cancel/discard cleanup independently of the ready transcript. Arbitrate fast/normal completion once per session; never replace already delivered raw text when cleanup finishes. Route changed targets into the pending-result workflow.
  - [ ] **10.3** Optionally show live transcript/cleanup progress in the nonactivating overlay. If adding LLM SSE streaming, keep it capability-gated, collect/validate the complete response, and measure first-token versus final latency separately. Preview is feedback, not an insertion-speed claim.
  - [ ] **10.4** Test fast-mode settings isolation, simultaneous fast/cleanup completion, cancellation, and stale responses. For streaming, test fragmented events, Unicode boundaries, final markers, and error/truncation handling; partials never reach external fields.

- [ ] **11. Add bounded connection prewarming.** Depends on 4 and 5; perform only after setup timing is visible.
  - [ ] **11.1** Add an opt-in policy that prepares at most one fresh unused realtime session for a short configurable idle window. Never activate the microphone or transmit audio while prewarming. Reuse the app's existing shared `HttpClient`.
  - [ ] **11.2** Match prewarmed sessions to provider/language/protocol settings; invalidate on Apply, expiry, disconnect, or shutdown. Atomically claim one session, disarm idle expiry, and transfer lifetime ownership so old setup/idle tokens cannot cancel active recording. Fall back to ordinary setup if unavailable; bound idle lifetime and connection/request counts.
  - [ ] **11.3** Test claim/expiry/cancel races, settings changes, disconnected sockets, and no cross-recording text. Compare cold versus prewarmed short dictations and record idle cost/activity. Prefer this fresh-session approach; reuse a completed session only if documented support and isolation tests justify the added reset logic.

- [ ] **12. Automate comparisons of already available infrastructure.** Depends on 1, 4, and 6; extend diagnostics rather than changing Azure configuration.
  - [ ] **12.1** Add named candidate configurations drawn from existing authorized settings/services or supported provider discovery. Keep secrets encrypted/in memory and report opaque labels. Compare session-creation, WSS, and cleanup timings separately; preserve the ABB gateway-to-direct-WSS routing already in `BuildRealtimeUri`.
  - [ ] **12.2** Capture attempt count, safe status codes, throttling/`Retry-After`, first/full response timing, and sequential cold/warm repetitions. Improve the bounded retry policy to respect valid server delays within cancellation/deadline limits. Test 429/5xx, invalid headers, and cancellation; do not add load tests or automatically change capacity.
  - [ ] **12.3** Exercise existing `LocalWhisperProvider` and compatible local/LAN cleanup endpoints when available. Reuse resident models/services; distinguish cold model loading from warm inference. Record hardware and quality; do not assume CPU inference beats Azure or require hardware purchases/model downloads to complete the tooling.
  - [ ] **12.4** Generate a comparison table by audio duration/configuration: sample count, median, p95, failures, fallback rate, and quality observations. Report unavailable candidates as untested. Do not attribute delay to region/gateway/capacity without evidence or automatically overwrite the active configuration.

- [ ] **13. Prepare an isolated audio-to-edited-text experiment.** Depends on 12; lower priority, diagnostic-only.
  - [ ] **13.1** Check already accessible endpoints for explicit support for instructed audio input with text output. Add a capability-gated diagnostic adapter only against a verified API contract; return unsupported cleanly otherwise. Never send chat-style editing instructions to the dedicated transcription protocol expecting equivalent behavior.
  - [ ] **13.2** Run the same synthetic corpus and cleanup instructions through one-stage audio generation and the two-stage baseline, when supported. Measure latency, meaning preservation, language policy, silence, and final-tail accuracy; test request/response parsing offline.
  - [ ] **13.3** Write an evidence-based keep/reject recommendation. Keep production routing unchanged; lack of an accessible capable model leaves the live comparison unchecked, not a requirement to create an Azure deployment.

- [ ] **14. Complete regression, performance, and handoff evidence.** Depends on completed implementation items; conditional experiments retain their own unchecked status.
  - [ ] **14.1** Run focused tests after each change, then the complete suite and Release build once the combined implementation is stable. Cover file providers, settings/DPAPI migration, toggle/push-to-talk/VAD, limits, final-only output, and shutdown. Record commands and failures separately.
  - [ ] **14.2** Use bounded synthetic short/medium/long English, Indonesian, and mixed-language fixtures; do not label an English recording with an Indonesian hint and claim Indonesian accuracy. Replay live fixtures at capture speed. Compare identical inputs/settings for file baseline, live upload, delay tuning, cleanup variants, speculation, and prewarming.
  - [ ] **14.3** Verify insertion/recovery in agent-created disposable scratch fields using existing diagnostics where available. Check actual text acceptance separately from dispatch, no Enter, no stale-field output, and newer clipboard preservation. Leave physical-microphone/application checks unverified when they cannot be observed.
  - [ ] **14.4** Update `README.md`, `VERIFICATION.md`, sample settings, and affected `AGENTS.md` architecture entries. Document defaults, fast/recovery commands, rollback to file transcription, new diagnostic commands, measured results, and remaining gaps. Enable only validated defaults; leave optional speculation/prewarming off until justified.
  - [ ] **14.5** Review the final diff and run whitespace checks. Leave implementation uncommitted and provide a short list of completed item numbers, evidence paths, remaining unchecked work, and deployment status. Publishing an artifact does not update the installed application.

## Protocol references and cautions

Use current official docs when implementing request fields; verify compatibility with the configured deployment rather than assuming model aliases are interchangeable.

| Reference | Relevance |
|---|---|
| [Azure realtime transcription overview](https://learn.microsoft.com/en-us/azure/foundry/openai/concepts/gpt-realtime-whisper) | Audio can be processed while arriving; file upload after Stop defeats this overlap |
| [Azure microphone streaming example](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets#transcribe-audio-in-real-time) | PCM24k, manual commits, supported transcription-delay values; sample commit interval is not an accuracy recommendation |
| [OpenAI latency optimization](https://developers.openai.com/api/docs/guides/latency-optimization) | Model size, output generation, overlap, and speculative execution |
| [Reasoning controls](https://developers.openai.com/api/docs/guides/reasoning) | Effort is model/API dependent; Azure aliases and compatible servers need capability checks |
| [Azure latency guidance](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/latency) | Separate first/full-response latency, token generation, and capacity effects |

Timeouts are ceilings, not fixed waits. The existing upload loop already sends as fast as transport permits. Smaller timeouts, another `HttpClient`, aggressive clipboard-delay reductions, prompt micro-trimming, and partial typing are not substitutes for overlapping the remote stages.
