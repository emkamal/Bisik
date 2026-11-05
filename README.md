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
- Azure receives the completed recording after Stop through the realtime WebSocket protocol. Text is inserted once, after every committed transcript item is final. Legacy realtime-auto-type settings now use the same final-text behavior.
- Bisik captures the foreground window, accessible text field and selection before recording. Switching fields/windows, moving the caret, or switching away and back invalidates insertion. Do not edit the destination while dictating.
- Automatic insertion uses **Ctrl+V**, preserving Unicode, punctuation and paragraphs. Bisik never sends Enter, submits a message, or interprets transcription as a command. Terminal fields are excluded because pasting newlines there can execute commands.
- If the target changed or cannot be verified, a balloon explains manual paste. If another clipboard copy occurred, Bisik offers a review window instead of replacing that newer content. Review's Copy button is an explicit user action.
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

## Local data

Persistent user data is under %APPDATA%\Bisik\:

- settings.json: keys, endpoints and sensitive headers encrypted using Windows DPAPI for the current user. Legacy plaintext secret fields migrate on load/save; undecryptable ciphertext is preserved. API-key UI fields are masked.
- history.jsonl and recorded audio: retained when history is enabled and a successful nonempty result is logged. These contain dictation content. Turn history off if retention is unwanted. Empty/failed/no-history recordings and Test Connection recordings are removed.
- error.log: timestamps and exception stack traces with credential redaction. Provider failures report safe status/error identifiers instead of raw response bodies.
- settings.json.bad: an encrypted recovery copy if malformed settings were replaced with defaults.

sample.settings.json contains placeholders only. Bisik never reads or modifies either ChatGPT profile. Startup registration changes only when you explicitly change the startup checkbox and Apply.

## Verification

Non-inserting full-pipeline timing (uses an encrypted disposable copy of existing settings,
including the configured cleanup; accepts synthetic WAV only):

~~~powershell
dotnet run --project tools/Bisik.Diagnostics -- --benchmark artifacts/latency/synthetic-en.wav --output artifacts/latency/baseline-file.json --repeat 1
~~~

Repetitions are limited to 1–5 and each attempt to 120 seconds; Ctrl+C cancels.
Reports contain opaque labels, monotonic stages and byte/attempt counts, without transcripts or endpoints.
Use `--mode live` to replay PCM16 mono at capture speed. Optional `--delay low` and
`--pause-commits on` override only the diagnostic copy. Settings exposes live capture,
phrase commits (both off by default) and provider-default delay omission. The dedicated
Azure transcription capability supports `minimal`, `low`, `medium`, `high`, `xhigh`;
omission does not imply `medium`. See the [Azure microphone example](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets#transcribe-audio-in-real-time).
Optional whole-text speculation (`--speculation on` for diagnostics) coalesces completed
snapshots with a 250 ms debounce, at most two speculative requests plus required final cleanup.
Only an exact final transcript/settings match is reused. It is disabled by default because
extra requests and measured benefit depend on pauses and endpoint response time.
Missing stages are unavailable, including paste for this benchmark. `duration_ms` keeps its original
transcription-plus-cleanup meaning. Set `BISIK_LATENCY_TRACE=1` before launching the app to opt into
`%APPDATA%\Bisik\latency.jsonl` (100 sessions/2 MiB maximum).

See [VERIFICATION.md](VERIFICATION.md) for checks actually run, remaining manual checks, and opt-in diagnostic tools. Automated tests do not establish universal application compatibility or microphone quality.
