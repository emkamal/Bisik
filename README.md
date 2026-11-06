# Bisik

Bisik is a native Windows tray dictation app. Place the cursor in an editable field, use the global hotkey to record and stop, then review the inserted text and submit it yourself. It operates independently of ChatGPT/Codex profiles.

## Run

The self-contained Windows 10/11 build needs no separate .NET installation or administrator privileges. Click the pinned **Bisik** entry in Start to open Settings, or launch the per-user installation with:

~~~powershell
& 'C:\Users\kamal\AppData\Local\Programs\Bisik\Bisik.exe' --settings
~~~

Omit --settings to start quietly in the tray. Double-click the tray icon for Settings. Starting a second instance requests the existing instance's Settings window. The installation uses the existing %APPDATA%\Bisik settings. Adding a Start shortcut does not enable automatic startup.

For development (.NET 8 SDK or a newer SDK supporting the target):

~~~powershell
dotnet restore Bisik.sln
dotnet build Bisik.sln
dotnet test Bisik.sln
dotnet run --project src/Bisik.csproj -- --settings
dotnet publish src/Bisik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish/win-x64
~~~

The repository publish output remains at D:\Dev\Bisik\artifacts\publish\win-x64\Bisik.exe. Publishing alone does not update the per-user installation.

## Initial configuration

Existing settings are retained. New settings default to **Toggle**, **Ctrl+Shift+Space**, **Insert final text**, automatic language, and post-processing off. Older settings can have different hotkeys, clipboard preferences, and post-processing enabled.

Every settings field has a small **ⓘ** help button. Hover over it, click it, or focus it with Tab to read the full description in a wrapping tooltip with a bold heading. Longer tabs scroll to keep all options reachable.

1. General: choose Toggle and capture a convenient hotkey, preferably Ctrl+Shift+Space. Apply reports conflicts. Win-key combinations remain supported but Windows can intercept them.
2. Provider: select Azure OpenAI and reuse the saved endpoint, API key and existing gpt-realtime-whisper deployment. No new deployment is needed.
3. Audio: choose the microphone, WAV and 16000 Hz. Azure realtime converts continuously to PCM16 mono 24000 Hz.
4. Language: Auto-detect, English (en) or Indonesian (id). Automatic language omits the Azure language hint; it does not force English.
5. Post-Processor: **turn Enabled off for initial verification**, including when an older configuration had it enabled.
6. General: leave **Always copy transcription result to clipboard** off to restore the previous clipboard after insertion. An explicitly enabled preference keeps the result on the clipboard.
7. Apply, then Test Connection on the Provider tab. Speak for three seconds. This tests the fields currently shown, even before Apply, and displays a result without typing anywhere. Post-processing and history are disabled for this test.

Settings cannot open during dictation. Stop listening first. Language and post-processor tray choices affect the next recording. Apply rebuilds providers and registers the new hotkey immediately.

## Recording and insertion

- Toggle: hotkey starts; hotkey stops and transcribes. Push-to-talk: hold, then release to transcribe. Optional VAD is armed/disarmed by the hotkey, with a short pre-roll buffer for speech onset.
- The indicator stays above other windows without activating them. Recording is limited to five minutes; stopping/upload/processing has a two-minute overall timeout. Pressing the hotkey during processing cancels. In VAD mode this also stops listening.
- Completed-file capture remains the default. Opt-in live capture starts listening immediately,
  queues PCM while Azure connects, and uploads during recording. Stop drains capture, queued
  sends and conversion, then waits for all acknowledged final transcript items. A stream failure
  may use the complete file within the same Stop deadline and bounded retry policy. Turn live
  capture off to return to the file path. Text is inserted once; legacy realtime-auto-type uses
  the same final-only behavior.
- Bisik captures the foreground window, accessible text field and selection before recording. Switching fields/windows, moving the caret, or switching away and back invalidates insertion. Do not edit the destination while dictating.
- Automatic insertion uses **Ctrl+V**, preserving Unicode, punctuation and paragraphs. Bisik never sends Enter, submits a message, or interprets transcription as a command. Terminal fields are excluded because pasting newlines there can execute commands.
- If the target changed or cannot be verified, a balloon explains manual paste. If another clipboard copy occurred, Bisik offers a review window instead of replacing that newer content. Review's Copy button is an explicit user action.
- The overlay fades away when dictation finishes, including clipboard-only fallback. A retained
  result is marked Ready in the tray; select the intended field and press
  **Ctrl+Alt+Shift+V** (configurable Insert latest hotkey in General). This captures the current
  field and selection and performs the same guarded paste. It never reactivates the old field.
  New completed results replace the pending result; dispatch, review dismissal, tray Dismiss,
  and exit clear it. Repeated recovery commands cannot dispatch the same result twice.
- **Ctrl+Alt+Shift+Space** (configurable Fast output hotkey) starts one fast recording or
  stops the current recording with cleanup skipped. During cleanup it selects the final
  transcript immediately. It does nothing while STT is still pending. Saved cleanup settings
  remain unchanged. Late cleanup cannot replace delivered raw text; changed targets use Ready
  recovery. In VAD, arming fast mode applies to the next detected recording.
- Clipboard preservation supports bounded global-memory formats such as text, HTML, RTF, DIB images and file lists. Unsupported owner-rendered/GDI formats or a busy clipboard fall back to review. Restoration waits 600 ms and checks ownership under the clipboard lock; a newer copy is never restored over.

Windows has no universal paste acknowledgement. A sent paste does not prove an app accepted it. A very slow app may read the clipboard after restoration, and focus/input can still race at OS dispatch time. Review the destination. Standard and Electron fields must expose a writable UI Automation field; custom/inaccessible controls fall back. Password/protected and terminal fields are excluded. Windows integrity isolation can block insertion into elevated apps; use manual paste there. Administrator privileges are not recommended for normal dictation.

## Azure realtime configuration

gpt-realtime-whisper does **not** use ordinary multipart /audio/transcriptions. Bisik detects "realtime" in the deployment/model name. Select **Use Azure realtime transcription** when a custom deployment name hides that identity (provider.azureRealtime in JSON).

The native connection follows the proven mywhisper pattern:

1. POST {endpoint}/openai/v1/realtime/client_secrets using the saved Azure API key.
2. Connect WSS to /openai/v1/realtime?intent=transcription using the short-lived client secret.
3. Send session.update with deployment, optional language, PCM16 mono 24 kHz and manual commits (turn_detection: null); wait for session.updated.
4. Append audio, commit, and await final results for every acknowledged item. Duplicate events are ignored. Setup, upload and finalization have bounded waits; failures never return partial text for insertion.

An existing ABB-style *-gateway.azure-api.net endpoint continues using its gateway for session creation and the corresponding direct Azure resource for WSS. **Realtime Calls Endpoint (optional)** overrides the direct route; enter the existing resource HTTPS URL or existing realtime calls URL. No credentials go to ChatGPT Personal, and no profile launchers/configuration/authentication are changed.

Microsoft references:

- [gpt-realtime-whisper concepts](https://learn.microsoft.com/en-us/azure/foundry/openai/concepts/gpt-realtime-whisper)
- [Realtime WebSocket protocol](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets)

## Other providers and cleanup

Azure multipart models, OpenAI, OpenRouter, Local Whisper and Custom Endpoint support remain. Azure multipart uses endpoint, deployment, API version and key. OpenAI/OpenRouter use model and key; local/custom servers use base URL and optional key/model. Custom extra headers use one Name:Value per line.

Optional LLM cleanup retains Clean up, Homogenize to English/Indonesian, Translate and Custom presets. It runs after final transcription and is disabled by default. WAV is recommended; MP3 uses Windows Media Foundation. OGG encoding is not implemented.

Reasoning effort defaults to omission. Settings accepts verified supported efforts for the
exact model/API; changing the model, deployment or endpoint clears that claim. Custom prompts
remain verbatim. Cleanup requires a complete `finish_reason: stop` response; truncated,
filtered, refused, malformed and empty responses fail without insertion. Compatible servers
must return this completion marker. See [OpenAI reasoning controls](https://developers.openai.com/api/docs/guides/reasoning).

`--cleanup-benchmark --output report.json` runs a fixed synthetic English/Indonesian corpus
against the saved cleanup. `--probe-reasoning low` explicitly probes that request parameter;
`--cleanup-model <existing-model>` compares an already accessible model. Overrides stay in
memory. Successful probes establish compatibility for that run, not general model availability.
Separate `.quality.json` files contain only that fixed synthetic input/output for meaning review.
Candidates can use `--settings <existing-encrypted-settings.json> --label <opaque-name>`;
the source is never migrated. `--cleanup-api v1` and `--cleanup-endpoint-source transcription`
are diagnostic overrides for an advertised API, leaving production routing intact. A catalog
entry is not proof of a callable deployment; unavailable probes remain failed/untested.
`--discover --output report.json` records safe discovery status and candidate availability;
`--compare artifacts/latency --output comparison.md` groups compatible report identities,
showing sample count, median, p95, failures and fallback rate. HTTP receipts record first/full
arrival, status and Retry-After without connection details. Valid retry delays respect the
single post-Stop deadline; a larger requested delay cannot trigger an early retry.
Diagnostic-only `--audio-edit <synthetic.wav> --output report.json` gates instructed audio on
an advertised generation model and the [documented audio Chat Completions contract](https://developers.openai.com/api/docs/guides/audio-chat-completions).
It never sends editing instructions to the dedicated transcription protocol. The current
bounded probe returned 404; one-stage quality/latency are unverified, and production keeps
the two-stage route. No deployment is created by this tool.
English fixtures can be generated with `New-SyntheticSpeech.ps1 -Fixture short|medium|long`.
`--scratch-insert --output report.json` creates disposable native fields and checks actual
acceptance, recovery, Enter events and clipboard preservation. This run was blocked by Win32
clipboard access denied (5), so successful acceptance remains unverified. No installed app was updated.

## Local data

Persistent user data is under %APPDATA%\Bisik\:

- settings.json: keys, endpoints and sensitive headers encrypted using Windows DPAPI for the current user. Legacy plaintext secret fields migrate on load/save; undecryptable ciphertext is preserved. API-key UI fields are masked.
- history.jsonl and recorded audio: retained when history is enabled and a successful nonempty result is logged. These contain dictation content. Turn history off if retention is unwanted. Empty/failed/no-history recordings and Test Connection recordings are removed.
- error.log: timestamps and exception stack traces with credential redaction. Provider failures report safe status/error identifiers instead of raw response bodies.
- settings.json.bad: an encrypted recovery copy if malformed settings were replaced with defaults.

sample.settings.json contains placeholders only. Bisik never reads or modifies either ChatGPT profile. Startup registration changes only when you explicitly change the startup checkbox and Apply.

## Verification

### History comparisons and copying

An open History window updates automatically after saved transcriptions and external changes
to `history.jsonl`, including file replacement or deletion. New entries remain newest first;
search, provider/language filters, selected rows and reading position are preserved. Updates
also work while minimized and do not bring the window forward. Temporary file-access failures
leave the displayed rows intact, show a warning, and retry automatically.

Hover over any column header for its meaning, timing boundaries and unavailable-value behavior.
Cell tooltips remain available for long values.

Settings > History has two additional options: **Include recording duration** (on by default)
and **Include configuration snapshot** (off by default). They apply to future entries while
history logging is enabled. Recording duration is captured audio length, including pauses,
silence and VAD pre-roll, rather than a speech-only estimate. Old entries leave unavailable
values blank; changing options does not backfill old history.

Each snapshot contains the settings used when that dictation started, including audio,
language, provider/model, live capture, pause commits, speculation, prewarming, cleanup,
reasoning effort and the configured prompt. The cleanup section also includes the effective
system prompt and reasoning effort. Built-in presets can use a different prompt from the
saved custom field. Keys, endpoint URLs and extra-header names/values are excluded; connection
presence and header count are retained. Prompts remain plain text alongside the transcript.
`post_processing_applied` identifies fast/raw selection even when cleanup was configured.

Select rows with Ctrl/Shift and right-click, or use **Copy Rows**, to copy JSON, CSV or
**table (Excel)**. One selected row produces a JSON object; several produce an ordered array.
JSON includes the raw/final text, timings and optional snapshot for comparison with your
ground-truth script. CSV and Excel tables include column headers and the snapshot as one
JSON cell. The Excel clipboard includes an HTML table plus tab-separated text so multiline
transcripts stay in their corresponding cells. Ctrl+C copies the selected rows as a table.
Right-click a cell and choose **Copy cell value** to copy its complete value.

History timings are milliseconds:

- **Recording** (`recording_duration_ms`): captured audio length, if enabled and available.
- **Processing** (`duration_ms`): existing transcription-plus-cleanup duration.
- **Transcription / Post-Proc**: time spent in each final pipeline stage, including retries;
  live/speculative work during recording can overlap these stages.
- **Stop to result** (`stop_to_result_duration_ms`): from Stop through capture drain and final
  processing until the chosen result exists. File-only callers without capture timing use
  the elapsed processing time.
- **Start to result** (`capture_to_result_duration_ms`): wall time from recording activation
  until the result exists, when capture timing is available.

Result timings exclude history persistence, success sound and paste/clipboard dispatch.
They measure result availability, rather than acceptance by the destination application.

Non-inserting full-pipeline timing (uses an encrypted disposable copy of existing settings,
including the configured cleanup; accepts synthetic WAV only):

~~~powershell
dotnet run --project tools/Bisik.Diagnostics -- --benchmark artifacts/latency/synthetic-en.wav --output artifacts/latency/baseline-file.json --repeat 1
~~~

Repetitions are limited to 1–5 and each attempt to 120 seconds; Ctrl+C cancels.
Reports contain opaque labels, monotonic stages and byte/attempt counts, without transcripts or endpoints.
Success audio is queued after the output decision; owned player/stream playback and shutdown
drain run without blocking insertion. Sound failure is logged separately. History remains
durable before delivery; local synthetic measurements showed ~0.3–0.5 ms warm history writes
(27 ms first write) and ~0.5 ms warm WAV export (5.4 ms first write). These disk samples are
not production guarantees. Measure locally with `--local-costs <synthetic.wav> --output report.json --repeat 3`.
Use `--mode live` to replay PCM16 mono at capture speed. Optional `--delay low` and
`--pause-commits on` override only the diagnostic copy. Settings exposes live capture,
phrase commits (both off by default) and provider-default delay omission. The dedicated
Azure transcription capability supports `minimal`, `low`, `medium`, `high`, `xhigh`;
omission does not imply `medium`. See the [Azure microphone example](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets#transcribe-audio-in-real-time).
Optional whole-text speculation (`--speculation on` for diagnostics) coalesces completed
snapshots with a 250 ms debounce, at most two speculative requests plus required final cleanup.
Only an exact final transcript/settings match is reused. It is disabled by default because
extra requests and measured benefit depend on pauses and endpoint response time.
Optional prewarming prepares one fresh unused connection for 15 seconds (configurable 5–60),
without microphone capture or audio transmission. Claim transfers its lifetime to recording;
Apply, expiry, disconnect and shutdown invalidate unused sessions. It is off by default.
Diagnostics can compare `--mode live --prewarm on`; setup remains visible before CaptureStart.
Missing stages are unavailable, including paste for this benchmark. `duration_ms` keeps its original
transcription-plus-cleanup meaning. Set `BISIK_LATENCY_TRACE=1` before launching the app to opt into
`%APPDATA%\Bisik\latency.jsonl` (100 sessions/2 MiB maximum).

See [VERIFICATION.md](VERIFICATION.md) for checks actually run, remaining manual checks, and opt-in diagnostic tools. Automated tests do not establish universal application compatibility or microphone quality.

## Always launch the latest workspace build

The Start pin can use the stable native launcher installed at `%LOCALAPPDATA%\Programs\Bisik\Bisik.exe`.
After the one-time setup, ordinary `dotnet build`, solution builds and `dotnet publish` automatically
register the newest successful app output. Debug, Release and custom output paths all work.
The shortcut itself stays unchanged; no repeated installation or copying is needed.

**Exit Bisik and reopen the Start pin after rebuilding** to load the new code. A rebuild leaves
an active recording/session alone. The launcher runs a separate copy under `%APPDATA%\Bisik\builds`,
so the app does not lock `bin` files during the next build. Five recent copies are retained;
running copies are protected. A failed compilation keeps the previous target. If build copies
are missing, the launcher uses its self-contained fallback. Development copies need the installed
.NET Windows Desktop runtime; published self-contained copies do not.

One-time setup for another machine, from the repository root:

~~~powershell
dotnet publish tools/Bisik.Launcher/Bisik.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/launcher
dotnet publish src/Bisik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:BisikRegisterLatestBuild=true -o artifacts/fallback
powershell -NoProfile -File tools/Install-BisikLauncher.ps1 -LauncherExecutable artifacts/launcher/Bisik.Launcher.exe -FallbackExecutable artifacts/fallback/Bisik.exe
~~~

The installer backs up the previous executable/settings and keeps the existing Start pin and
startup enablement. It may require Bisik to exit if Windows locks the installed executable.
Subsequent builds need no setup command. Use `-p:BisikRegisterLatestBuild=false` to opt out on
an opted-in machine. The installed launcher also supports `--resolve-target <report.json>` for
non-inserting verification. Test/build receipts for this setup are under `artifacts/launcher-verification/`.

## Experiments

Tray > Experiments opens one native modeless window. Paste a reference, review identifier suggestions (unchecked until accepted), add named cleanup prompts and candidate deployment/model/effort rows, and select one fixed judge. `default` omits effort; `none` explicitly requests none. Unknown pairs require the explicit verification command before Run. Compatibility is exact API/model evidence, not proof of deployment entitlement. Every successful candidate receives the identical single raw take. Experiments never insert/copy automatically, apply a winner, update normal history/latest results or change active settings. Active recording/batches exclude normal dictation, recovery insertion, Settings tests and Apply; an idle window does not exclude dictation.

Start listens immediately while recognition connects; Done drains capture and final buffers. The reference remains scrollable/selectable. Real microphone levels drive quiet hints; silence does not stop a recording. Defaults: five-minute capture, five-second initial quiet hint, 120-second shared recognition after Stop and independently bounded 120-second trials; one to five randomized rounds, at most 100 cleanup calls. All generation precedes evaluation. The raw baseline costs no cleanup request. Judging is a separate call per distinct reference/output/terms/rubric/judge configuration; explicit probes and optional transient retries add calls. One sample is provisional evidence for that script/take, not universal superiority; small-sample p95 is not a reliable tail estimate.

Scoring v1 uses NFKC word normalization (bounded to 4096 tokens), Wording = 100 × max(0, 1 − WER). Numbers/negation stay significant. Reviewed identifiers use exact ordinal canonical case versus configured wrong aliases/case variants; longest matches win, ambiguous aliases are flagged, absent required terms score zero. Empty term lists make Identifiers unavailable. The blinded fixed judge reports independent meaning/readability 0–100 and bounded evidence. Major/extreme semantic evidence caps effective meaning at 30/10, while preserving reported scores and readability. Warnings never exclude a valid row.

Repeat wording/identifier factors average valid generated samples; meaning/readability average valid judged samples with per-sample caps. Speed uses median valid cleanup duration. Estimated Stop→final = shared measured Stop→raw + measured cleanup (including retries/parsing), excluding matrix queueing, recording, judge traffic and insertion. Speed = 100 × B/(B+t), default B=5 seconds. Default relative weights: Wording 5, Identifiers 25, Meaning 40, Readability 15, Speed 15. Missing positive-weight factors leave a row unranked; measured zero is valid. All-zero weights have no winner. Displayed 0.1-point ties share rank. The weights popup reranks cached scores immediately without requests and checkpoints the view preferences.

Versioned reference/raw/prompts/results/receipts live in `%APPDATA%\Bisik\experiments\<id>`, with the connection in a separate DPAPI settings snapshot. Audio retention defaults off; only explicit retention copies audio, and owned capture temporaries are deleted. Interrupted checkpoints reopen as interrupted. Copy JSON/CSV/Excel tables and Export are explicit; these contain user content and prompts but exclude keys/endpoints/headers. Imported results can be inspected without credentials; rerunning requires the matching available connection. Treat exported content as private.

Fixture diagnostics use the same runner:

~~~powershell
dotnet run --project tools/Bisik.Diagnostics -p:BisikRegisterLatestBuild=false -- --experiment --definition setup.json --fixture take.json --settings protected-settings.json --output artifacts/experiments/timing.json --quality artifacts/experiments/quality.json
~~~

The definition uses the public ExperimentDefinition JSON schema; the take fixture is `{ "RawText": "...", "MeasuredStopToRawMs": 1234, "Kind": 0 }` (Kind: 0 human, 1 synthetic text, 2 synthetic audio). Saved raw requires actual measured timing; missing timing is not zero. Optional `--wav saved.wav` recognizes that file once, then runs the matrix. `--quality` is optional and explicitly retains full text; the main report is content-free and groups full input/raw/config/scoring/rubric identities. Sources and output paths must differ. Existing benchmark/live/delay/pause/speculation/prewarm commands remain available. Authorized model access and live comparisons are postponed.

Normal dictation cleanup failure/deadline now retains authoritative final raw text with the original failure status. It never automatically copies/inserts it. Use the labelled tray Review recovered raw text or Insert recovered raw text command; explicit insertion captures and validates a new target. User cancellation, shutdown, partial STT and completed fast selection do not automatically recover.

Optional cleanup vocabulary: Post-Processor > Vocabulary profile accepts canonical | context | comma-separated variants per line. The editable ABB template is optional; both empty fields retain legacy prompts. Profiles append after each preset, preserve ordinary happy, and distinguish C# APIs from tool names. Profiles are separate from experiment evaluation terms, appear in safe history snapshots, and invalidate cleanup/speculation identity while leaving exact reasoning capability evidence unchanged.

Recognition context is optional and bounded (1024-character domain prompt, 32 keywords, language hint). Exact model/API/transport field evidence controls emission; changing the route invalidates it. The explicit realtime switch selects transport and never forces delay. Known gpt-realtime-whisper retains language/delay but does not receive prompt/keywords. New aliases require explicit verified fields; multipart emits new fields only with matching evidence. Context is included in snapshots and warm-slot identity. Do not inject the reference script. Current live Azure hint/model contracts remain unverified; [Realtime transcription](https://developers.openai.com/api/docs/guides/realtime-transcription) describes OpenAI contracts, not Azure entitlement.

Completion telemetry records nullable prompt/output/reasoning/cached/prediction token counts when the provider returns them. Header arrival is separate from first streamed content; complete-response parsing does not invent first-text time. Optional StreamCompletion defaults off and requires an exact CompletionCapability Streaming claim; experiment variants include streaming in identity. SSE accumulates privately within bounded frames/body/text and accepts only stop plus DONE with complete text, rejecting refusal/filter/truncation/EOF. It never inserts partials or guarantees faster final delivery. [Chat Completions stream options](https://developers.openai.com/api/docs/api-reference/chat/create) describe optional usage; Azure routes remain unverified until E2 probes.

Advanced experiment model rows can explicitly request Stream or Prediction variants; JSON fixture definitions use StreamCompletion/UsePrediction booleans, giving on/off distinct identities. Prediction defaults off, uses only the raw transcript, and requires exact model/API CompletionCapability evidence. The legacy Azure 2024-06-01 route is blocked; a changed API invalidates evidence. GPT-6 support is not presumed. Missing accepted/rejected token counts leave actual prediction use unverified; compare complete latency and quality independently. Judge reference data is never prediction content. [Predicted Outputs](https://developers.openai.com/api/docs/guides/predicted-outputs) documents the current OpenAI model families; Azure entitlement/performance remains postponed.

Diagnostic-only stable segments: --segment-replay accepts the same definition/fixture/settings/output/quality options plus required --wav PCM16 mono (at most five minutes). It replays at capture pace, recognizes once, compares bounded segment overlap against complete full-text cleanup, and uses the same reference/rubric/judge. Three segment requests plus required full fallback are bounded; corrections, stale settings, incompatible whole-text prompts, failures or incomplete coverage force one complete fallback within the remaining Stop deadline. Receipts separate recording-overlap request time from after-Stop tail time and baseline cleanup. Cross-segment corrections, translation/glossary meaning and stitched fluency require matched live quality evidence. This does not change production speculation or promote segmentation from mocks.

Instructed-audio comparison: --audio-edit saved.wav --settings protected.json --output timing.json --audio-model existing-model --audio-contract contract.json --fixture-kind human|synthetic [--definition setup.json --quality quality.json]. Contract JSON carries exact ApiFingerprint, Model, MaximumSeconds/MaximumBytes and CallableContractVerified evidence. Defaults do not assume callable access; unknown provenance/contracts are unsupported receipts. Existing one-minute/8 MiB limits may increase only via verified contract bounds; no truncation. The optional fixed evaluator sees the same reference/rubric/terms, never injects reference into audio answer instructions, and reports upload+generation separately. Quality files are explicitly requested. Prior 404 remains access failure, not quality. [Audio Chat Completions](https://developers.openai.com/api/docs/guides/audio-chat-completions) is the adapter contract; production transcription routing is unchanged.

Manual Azure DevOps source template: tools/azure-pipelines-experiments.yml has no push, PR or schedule trigger. It requires an existing Windows agent pool with .NET Desktop runtime, private fixture/setup paths, and protected settings provisioned for that exact agent account. Desktop DPAPI ciphertext is not portable to another CI host/account; this template requires a compatible preprovisioned source and never creates plaintext secret JSON. E1/E3 authorized access, agent/pipeline registration and secret/private-destination policy remain external and postponed.

The helper tools/Invoke-BisikExperimentEvaluation.ps1 accepts explicit paths/opaque label/repetitions and -ValidateOnly to inspect CLI arguments without inference. It rejects source overwrites, reused nonempty summary directories and quality paths within the publication folder. Only summary.json is published, including failure/unavailable statuses; failed CLI execution fails the job. Optional privateQualityDirectory writes full judge/output content only to the explicitly configured private agent destination; the pipeline never publishes it automatically. No fixtures, personal settings, agents or resources are installed/registered by this source task. Actual paid pipeline execution remains unrun.

Setup limits/retry options are editable natively and saved draft setups can reopen before recording. The speed-view B editor accepts 0.001–1,000,000 seconds; imported values are validated before popup construction. Closed Settings connection tests remain owned through cancellation/drain and block new capture until released.
