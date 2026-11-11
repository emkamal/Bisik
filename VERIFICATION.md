# Verification — 2026-11-06

## Current latency implementation

The source work is committed per the current user instruction. The installed application, active settings, startup registration and cloud resources were not changed. Live capture, phrase commits, speculative cleanup and prewarming remain off by default; reasoning and transcription delay default to omission. File capture is the rollback path.

- `dotnet test Bisik.sln --nologo --verbosity minimal`: **231 passed, 0 failed, 0 skipped**. Receipt: `artifacts/latency/item-14-full-tests.txt`.
- `dotnet build src/Bisik.csproj -c Release --nologo`: passed with **0 warnings, 0 errors**. Receipt: `artifacts/latency/item-14-release-build.txt`.
- Focused receipts `item-1-tests.txt` through `item-13-tests.txt` cover session ownership, capture/pre-roll/tail drain, ordered bounded upload, actual-rate conversion, final reconciliation, fallback/retry deadlines, settings/DPAPI, whole-text speculation, fast arbitration, recovery/clipboard transactions and prewarm ownership. Tests use production components with fake capture/socket/output seams.
- Combined diff and whitespace checks passed. No physical microphone or target application acceptance is claimed by unit tests.

## Synthetic measurements

Full pipeline includes the saved cleanup. All live fixtures replay at capture speed; no insertion is attempted by these benchmarks. Initial English paired samples:

| Fixture/audio duration | File Stop-to-ready | Live Stop-to-ready | Samples per path |
|---|---:|---:|---:|
| Short, 2.210 s | 8.500 s | 4.468 s | 1 |
| Medium, 12.960 s | 11.444 s | 6.501 s | 1 |
| Long, 39.495 s | 14.958 s | 4.771 s | 1 |

Receipts: `artifacts/latency/item-14-{short,medium,long}-{file,live}.json`. These are samples, not a latency promise or distribution. `comparison.md` includes other runs, failures and fallback rates without merging older receipts that lack feature identity.

A separate medium quality pair preserved the first/final sentence, negation and fifteen-versus-fifty bar. Saved cleanup corrected raw ABB/cpmPlus/Vtrin casing to the expected text in both paths. Receipts: `item-14-quality-{file,live}.quality.json`; their timing receipts are separate.

Current versus low-reasoning cleanup used fixed English/Indonesian/mixed whole text: 4.191 s versus 5.081 s, one each, preserving negation, technical names and English translation. Speculation used two extra requests and was slower in its paired paused sample (5.123 s versus 4.213 s), so it stays off. A prewarmed sample took 3.460 s after Stop; that single sample does not justify enabling it. Local WAV/history costs are in `item-8-local-costs.json`.

## Remaining unchecked validation

- **5.4 / 14.2:** only en-US synthesis voices are installed; a bounded catalog-advertised speech-synthesis probe also returned 404 (speech-probe/receipt.json). Indonesian/mixed-language audio accuracy and the complete cross-language comparison matrix remain untested; English audio is not labelled Indonesian to claim accuracy.
- **6.4:** the existing catalog advertised smaller models, but bounded candidate probes returned 404. Their availability and cleanup quality remain unverified. Current model and prompt are preserved.
- **12.3:** no local/LAN inference candidate is configured or resident on the checked common local ports. No models/services were installed.
- **13.2:** instructed-audio generation is catalog-advertised and uses the documented diagnostic-only contract, but the bounded call returned 404. `audio-edit-recommendation.md` recommends retaining the two-stage route.
- **14.3:** the command-runner and interactive desktop-launched disposable native insertion probes both encountered **Win32 clipboard access denied (5)**. `item-14-scratch-insertion.json` and `clipboard-access-probe.json` record the blocker, with the desktop receipt under interactive-probe/run/acceptance.json; existing clipboard content was preserved. Actual paste acceptance remains unverified for this implementation.

All current receipts above are under ignored `artifacts/latency/`. Timing receipts exclude dictation, endpoints and credentials; separate quality receipts contain only fixed synthetic input/output.

## Reproduce

~~~powershell
powershell -NoProfile -File tools/Bisik.Diagnostics/New-SyntheticSpeech.ps1 -Fixture medium -OutputPath artifacts/latency/medium-en.wav
dotnet run --project tools/Bisik.Diagnostics -- --benchmark artifacts/latency/medium-en.wav --mode live --label medium --output artifacts/latency/live.json --repeat 1
dotnet run --project tools/Bisik.Diagnostics -- --cleanup-benchmark --output artifacts/latency/cleanup.json --repeat 1
dotnet run --project tools/Bisik.Diagnostics -- --compare artifacts/latency --output artifacts/latency/comparison.md
dotnet run --project tools/Bisik.Diagnostics -- --scratch-insert --output artifacts/latency/scratch-insertion.json
~~~

Optional benchmark overrides are documented in README.md. `duration_ms` retains transcription-plus-cleanup timing; for live capture, recognition overlaps recording and its post-Stop finalization is measured separately from capture, history, sound and paste. Dispatch is not proof a target accepted text. The existing 50 ms release delay and 600 ms restoration grace remain.

## Historical 2026-11-04 verification snapshot

The following describes the prior installation and checks. It is not acceptance evidence for the current latency implementation.
### Automated checks

Command:

~~~powershell
dotnet test Bisik.sln --no-restore --nologo --verbosity minimal --logger "trx;LogFileName=bisik-install-verification.trx" --results-directory artifacts/verification
~~~

Baseline full-suite result: **171 passed, 0 failed, 0 skipped**, including the clipboard and embedded-field ownership regressions. Tests cover:

- Azure setup, language hints, fragmented messages, final acknowledgements, out-of-order completion, duplicate events, cancellation, timeouts, resampling and direct realtime routing.
- Target changes, modifier release, Unicode/paragraph output, clipboard ownership/restoration, cancellation and fallback using injected platform fakes.
- Capture drain/cancellation/device failures/recovery/pre-roll with an injected audio driver; hotkey registration/state and repeat suppression.
- Real Windows DPAPI migration/recovery; silence, history, cancellation, HTTP retries, existing providers and UI hotkey capture.

Receipt: artifacts/verification/bisik-install-verification.trx. Unit tests do not use a physical microphone or drive external applications.

### Installed build

Self-contained win-x64 publication succeeded. The installed copy at C:\Users\kamal\AppData\Local\Programs\Bisik\Bisik.exe matches the published file: 162,490,090 bytes, SHA256 00FC3AB49F201EAFB706BDAF27DE01C2FE18A282D7BB1509272D4E874AB05BF3. A per-user Start Menu shortcut named Bisik targets that executable with --settings. Startup registration remains off. Receipt: artifacts/verification/installation.json.

Bisik is pinned to Start. The actual shortcut was verified to offer **Unpin from Start** and to target the installed executable with --settings. Double-clicking it launched **Bisik Settings** from that installed copy. Pin receipt: artifacts/verification/start-pin.json. No microphone was opened during installation or this launch check.

### Live ABB Azure

Executed against existing Bisik Azure settings through an encrypted scratch copy. No keys/tokens/endpoints printed, resources created, actual microphone, post-processing, history or ChatGPT/profile changes.

Windows SpeechSynthesizer generated: “Bisik dictation verification. Hello world. This is the final sentence.”

- Auto, English, Indonesian-hint and repeated Auto sessions completed, including the final sentence.
- Azure recognized the synthetic proper name “Bisik” as “Isaac”. This is an accuracy limitation, not a lost-tail failure.
- Two seconds of digital silence returned empty text.
- Cancellation was observed during a live request.

The Indonesian hint check used English synthetic audio. **Indonesian speech accuracy and actual microphone speech remain unverified.** Receipt: artifacts/protocol/live-azure-verification.json.

### Cross-application status

The production insertion probe pasted fixed test text containing Indonesian, an accented character, emoji and a paragraph break. Screenshots confirmed insertion in:

| Application | Result | Receipt under artifacts/verification |
|---|---|---|
| Notepad scratch tab | Inserted and visually confirmed | notepad-insertion-fixed.json |
| VS Code scratch editor | Inserted and visually confirmed | vscode-editor-insertion.json |
| VS Code Copilot draft | Inserted and visually confirmed; not submitted | vscode-chat-insertion.json |
| Separate GitHub Copilot app | Clipboard fallback; automatic insertion unverified | copilot-app-focused-insertion.json |
| Chrome browser field | Clipboard fallback; automatic insertion unverified | browser-insertion.json |

The first Notepad run exposed a clipboard revision change at CloseClipboard. The fix obtains the published revision after closing and reopening the clipboard, and verifies ownership before accepting it; regression tests cover competing copies and lock failures. A later source change supports embedded fields whose native/UI Automation ancestry belongs to the target window despite differing process IDs. That change has focused test coverage but has not yet been verified by successful insertion into the separate GitHub Copilot app.

Browser automation subsequently stopped because it could not verify the current URL's safety. No message was submitted and no microphone was opened during these checks. They verify text insertion, not microphone recording or end-to-end spoken dictation.

### Short microphone/application check

1. Launch the installed Bisik app with --settings, or open Settings from its tray icon. Choose Toggle, Ctrl+Shift+Space, WAV, intended microphone, existing Azure provider and Auto language. Disable post-processing and Always copy. Apply, then Test Connection and speak for three seconds.
2. Close Settings. In a new Notepad document, place the caret between two known words. Hotkey, say “Hello, this is the final sentence”, hotkey again. Check placement, exactly one transcript and the final word. Repeat with “Selamat pagi. Saya sedang menguji dikte bahasa Indonesia.” Use explicit id if desired.
3. Repeat in a disposable browser textarea, VS Code scratch editor, VS Code/Copilot chat draft and GitHub Copilot draft. Submit nothing. Check the indicator does not steal focus. Dictated line breaks depend on the model; the diagnostic below supplies deterministic multiline text.
4. Start dictation and switch fields/apps before stopping. Expect a clipboard/manual-paste balloon or review window; no insertion into the new field.
5. Copy a harmless marker first, dictate without changing focus, then paste elsewhere to confirm the marker was restored. Copy a different marker during transcription and confirm Bisik preserves the newer clipboard change during fallback/restoration.
6. Try silence, another recording, and cancellation via the hotkey during processing. No partial/duplicate text should appear. Check %APPDATA%\Bisik\error.log after failures. Elevated/protected/terminal/custom inaccessible fields may require manual paste.

### Opt-in diagnostics

Build:

~~~powershell
dotnet build tools/Bisik.Diagnostics/Bisik.Diagnostics.csproj
~~~

Insertion uses fixed harmless Unicode/multiline text and never Enter. Open an empty scratch field, launch hidden, and leave the field focused during the delays:

~~~powershell
New-Item -ItemType Directory -Force artifacts/verification | Out-Null
Start-Process -FilePath 'D:\Dev\Bisik\tools\Bisik.Diagnostics\bin\Debug\net8.0-windows\Bisik.Diagnostics.exe' -WindowStyle Hidden -ArgumentList @('--insert', '--capture-delay-ms', '5000', '--after-capture-delay-ms', '2000', '--output', 'D:\Dev\Bisik\artifacts\verification\insertion.json')
~~~

Use --capture-only to test target recognition without writing the clipboard or sending input. Inserted means a paste was sent; inspect the target to verify acceptance. Reports omit target titles and existing document text.

Live Azure uses existing DPAPI settings through an encrypted scratch copy and sends only the named test WAV:

~~~powershell
powershell -NoProfile -File tools/Bisik.Diagnostics/New-SyntheticSpeech.ps1 -OutputPath artifacts/verification/synthetic.wav
dotnet run --project tools/Bisik.Diagnostics -- --azure-file artifacts/verification/synthetic.wav
~~~

These tools do not enable startup registration, replace installed executables or modify ChatGPT configuration.

### History metadata and copying (2026-11-06)

- Full xUnit suite: 245 passed, zero failed/skipped. Covers old JSONL rows and metadata-preserving
  rewrites, duration/config options, captured session settings, resolved preset prompts,
  credential/endpoint/header exclusion, fast/raw result provenance, and capture-drain latency.
- Copy tests cover single/multi-row JSON, CSV/TSV multiline/quote/tab/Unicode round trips,
  UTF-8 HTML clipboard offsets, numeric Excel timings, multi-selection, context-cell copying,
  toolbar menu actions, and settings load/save. Clipboard tests use a capture callback;
  they do not overwrite the user's clipboard.
- Release build passed with zero warnings/errors and registered the latest workspace build.
  Source changes are uncommitted. Exit the running app and reopen its Start pin to load them.
- Native computer-use inspection found the synthetic History controls, but input/activation
  failed with `failed to activate captured window`. The scratch process was closed. Actual
  mouse interaction, Excel paste, and spoken microphone duration still need manual verification.

### History automatic refresh and header help (2026-11-06)

- `dotnet test Bisik.sln -c Release -p:BisikRegisterLatestBuild=false --nologo --verbosity minimal`:
  260 passed, zero failed/skipped. Receipt: `artifacts/history-live-verification/history-live-full.trx`.
- WinForms message-loop tests cover completed transcription/cleanup, background saves, external
  append/replace/rename/delete/recreate, missing initial history, watcher reconnection, locked-file
  retry, superseded searches, case-insensitive filters, duplicate rows, selection/scroll retention,
  minimized updates, copying during refresh, and cancellation/unsubscription on close.
- Logger tests verify notifications after closing the file/releasing its lock, no notifications
  for disabled/failed/cancelled/no-op mutations, and isolation/logging of failed observers.
- Every header retains its title and help text; cell-value tooltips remain enabled. Native
  computer-use inspection confirmed the repaired column titles. The hover attempt failed with
  `foreground window did not report a process id`; the preview was absent on recovery, so actual
  hover rendering remains unverified. Synthetic tests do not use live history, provider APIs,
  the physical microphone, or the user's clipboard.
- Final Release build passed with zero warnings/errors and registered as the latest workspace
  build. Changes remain uncommitted; exit Bisik and reopen its existing Start pin to load them.

## Experiments first version (6 November 2026)

Local validation uses fakes and isolated test data: immutable contracts, exact capability/matrix behavior, WER/identifier/speed/repeat/tie/missing factors, blinded judge caps/cache/schema, final-buffer capture, cancellation/deadlines, generation-before-judge, seeded repeats, selected evaluation reruns, DPAPI snapshots/source preservation, interrupted/corrupt/disk-failure recovery, Unicode exports, STA editors/weights/selection/close ownership, shared fixture scheduling and raw recovery claims. Solution tests passed: 335 before the final cross-component case; the final receipt is recorded in plan.md. Builds use BisikRegisterLatestBuild=false; installed runtime/launcher, active settings and cloud resources were not updated.

Unrun/postponed: real silence/speech quality, microphone disconnection, physical 100/150/200% DPI, constrained monitor layout and keyboard-only acceptance; installed-app tray/global hotkey routing; live configured-provider quality/latency/judge comparisons. Fake requests prove client behavior only. E1/E2 access/probes and E3 paid CI/agent/secrets/private destinations require external setup. Item 21 and E4 Azure Speech service/contract/training selection are postponed by the user. No cloud compatibility/performance or universal insertion acceptance is claimed.

Pipeline source validation: manual YAML parsed with PyYAML; helper command generation executed in ValidateOnly with isolated fake files and no inference; full solution suite passed 360 tests before final audit. Real Azure DevOps registration/agent/secret/artifact setup and paid execution are unrun/postponed. Build/launcher registration remained disabled.

Final autonomous integration receipt: dotnet build Bisik.sln -p:BisikRegisterLatestBuild=false passed with zero warnings/errors; dotnet test Bisik.sln --no-build -p:BisikRegisterLatestBuild=false passed 366 tests with zero skips. Manual YAML parsed successfully. Final audit covers failed/skipped phase progress, saved drafts/native bounds, separate Settings test shutdown ownership and actual Stop-based segment overlap. All diff checks passed; no installed runtime/launcher or cloud execution was changed.

Experiments follow-ups 23-25 (7 November 2026): full suite passed 371 tests, zero skips. Fake completion/STA tests cover automatic effort checks, explicit rejected-pair retries, saved evidence, edited model/prompt expansion, unchanged raw/timings, preserved prior runs/audio, setup reload, and all factor/B help buttons without weight changes. Release built with zero warnings/errors and was registered for the existing Start launcher; copied runtime hash matches Release, settings and stable launcher hashes remain unchanged. Live model compatibility probes and microphone acceptance were not run by the agent.

Experiment workspace persistence (9 November 2026): `dotnet test Bisik.sln -p:BisikRegisterLatestBuild=false --no-restore` passed 379 tests, zero failures/skips. New isolated DPAPI/STA tests recreate the window and store after window close and the application shutdown close path, checking all editable fields, uncommitted cell edits, multiline prompts/names, selected prompt, unfinished drafts, unchecked identifiers, judge, limits, retry/audio options, weights and connection identity. Additional checks cover saved raw with later setup edits, unchanged original runs, unattached draft prompt text, encrypted credentials/headers/content, cancelled writes and temporary-file cleanup, save failure with close retry, and preserving unreadable snapshots. These tests make no provider requests and do not change personal experiment data. Actual installed-app exit/reopen remains a manual check; exit Bisik and reopen its existing Start pin to load the updated build. Changes remain uncommitted.

Experiment result prompt column (9 November 2026): full suite passed 379 tests, zero failures/skips; Release built with zero warnings/errors and was registered for the existing Start launcher. An isolated native-form smoke check in ignored artifacts/prompt-column-smoke verified duplicate candidate labels with different saved prompts, full prompt tooltips and selected-row details, baseline labeling, later Setup edits, reranking, multiline CSV/table/HTML exports and unmatched imported-prompt fallback without provider or capture calls. Registered runtime DLL hash matches Release; active settings and stable launcher hashes are unchanged. Installed-app visual acceptance remains manual; exit Bisik and reopen the existing Start pin to load the new column. Changes remain uncommitted.

Settings interaction fixes (9 November 2026): full suite passed 389 tests, zero failures/skips. After the final popup-expiration change, 12 focused settings/help/weights tests passed. Regression checks cover native Enter preprocessing and newline insertion without Apply/close, popup toggle/hide/deactivate/close lifetime, background Settings allowing recording, busy Apply/test guards, registered shortcut capture and rejecting a bare Windows key. An isolated native preview in ignored artifacts/settings-ui-check showed complete icons and wrapped, dismissible help; physical microphone/global-toggle acceptance was not run. Release built with zero warnings/errors and was registered for the existing Start launcher; registered DLL matches Release, active settings and stable launcher hashes are unchanged. Exit Bisik and reopen Start to load these fixes. Changes remain uncommitted.

Overlay, progressive History and vocabulary improvements (11 November 2026): Release built
with zero warnings/errors and registered for the existing Start launcher; registered DLL
matches Release, and active settings/stable launcher hashes are unchanged. Eleven new
regression cases pass: persisted audio before recognition, raw before cleanup, one stable
row identity, terminal audio/raw retention, disabled History and failed persistence,
deleted-row suppression/legacy duplicates, selection retention, structured vocabulary
round trips/active-cell commit/Add-term focus/bounds, and quiet-speech display scaling.
The final full run passed 399 of 400 tests (zero skips); the existing
SettingsManagerTests.Load_should_accept_legacy_plaintext_api_keys_and_encrypt_them_on_next_save
failed with File.Replace's "Unable to remove the file to be replaced." Earlier full runs
had varying temporary-file access failures in settings/experiment persistence. The 24
focused settings-manager/History checks passed separately. Serial test collections and an
isolated temporary directory did not eliminate the intermittent failure. Final receipt:
artifacts/ui-improvements/ui-improvements-isolated.trx. SettingsManager source was not changed.

Computer Use inspected isolated native Settings/vocabulary, original-audio Play/Stop and
status-rendering previews. Play started a synthetic WAV and returned to Play on completion.
Physical microphone speech, global hotkey/installed-app acceptance and multiple physical
DPI settings were not run. No provider calls or personal settings/history changes were made.
Exit Bisik and reopen its existing Start pin to load the updated Release; edits are uncommitted.
