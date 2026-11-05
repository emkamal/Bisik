# Verification — 2026-11-04

## Automated checks

Command:

~~~powershell
dotnet test Bisik.sln --no-restore --nologo --verbosity minimal --logger "trx;LogFileName=bisik-install-verification.trx" --results-directory artifacts/verification
~~~

Latest full-suite result: **171 passed, 0 failed, 0 skipped**, including the clipboard and embedded-field ownership regressions. Tests cover:

- Azure setup, language hints, fragmented messages, final acknowledgements, out-of-order completion, duplicate events, cancellation, timeouts, resampling and direct realtime routing.
- Target changes, modifier release, Unicode/paragraph output, clipboard ownership/restoration, cancellation and fallback using injected platform fakes.
- Capture drain/cancellation/device failures/recovery/pre-roll with an injected audio driver; hotkey registration/state and repeat suppression.
- Real Windows DPAPI migration/recovery; silence, history, cancellation, HTTP retries, existing providers and UI hotkey capture.

Receipt: artifacts/verification/bisik-install-verification.trx. Unit tests do not use a physical microphone or drive external applications.

## Installed build

Self-contained win-x64 publication succeeded. The installed copy at C:\Users\kamal\AppData\Local\Programs\Bisik\Bisik.exe matches the published file: 162,490,090 bytes, SHA256 00FC3AB49F201EAFB706BDAF27DE01C2FE18A282D7BB1509272D4E874AB05BF3. A per-user Start Menu shortcut named Bisik targets that executable with --settings. Startup registration remains off. Receipt: artifacts/verification/installation.json.

Bisik is pinned to Start. The actual shortcut was verified to offer **Unpin from Start** and to target the installed executable with --settings. Double-clicking it launched **Bisik Settings** from that installed copy. Pin receipt: artifacts/verification/start-pin.json. No microphone was opened during installation or this launch check.

## Live ABB Azure

Executed against existing Bisik Azure settings through an encrypted scratch copy. No keys/tokens/endpoints printed, resources created, actual microphone, post-processing, history or ChatGPT/profile changes.

Windows SpeechSynthesizer generated: “Bisik dictation verification. Hello world. This is the final sentence.”

- Auto, English, Indonesian-hint and repeated Auto sessions completed, including the final sentence.
- Azure recognized the synthetic proper name “Bisik” as “Isaac”. This is an accuracy limitation, not a lost-tail failure.
- Two seconds of digital silence returned empty text.
- Cancellation was observed during a live request.

The Indonesian hint check used English synthetic audio. **Indonesian speech accuracy and actual microphone speech remain unverified.** Receipt: artifacts/protocol/live-azure-verification.json.

## Cross-application status

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

## Short microphone/application check

1. Launch the installed Bisik app with --settings, or open Settings from its tray icon. Choose Toggle, Ctrl+Shift+Space, WAV, intended microphone, existing Azure provider and Auto language. Disable post-processing and Always copy. Apply, then Test Connection and speak for three seconds.
2. Close Settings. In a new Notepad document, place the caret between two known words. Hotkey, say “Hello, this is the final sentence”, hotkey again. Check placement, exactly one transcript and the final word. Repeat with “Selamat pagi. Saya sedang menguji dikte bahasa Indonesia.” Use explicit id if desired.
3. Repeat in a disposable browser textarea, VS Code scratch editor, VS Code/Copilot chat draft and GitHub Copilot draft. Submit nothing. Check the indicator does not steal focus. Dictated line breaks depend on the model; the diagnostic below supplies deterministic multiline text.
4. Start dictation and switch fields/apps before stopping. Expect a clipboard/manual-paste balloon or review window; no insertion into the new field.
5. Copy a harmless marker first, dictate without changing focus, then paste elsewhere to confirm the marker was restored. Copy a different marker during transcription and confirm Bisik preserves the newer clipboard change during fallback/restoration.
6. Try silence, another recording, and cancellation via the hotkey during processing. No partial/duplicate text should appear. Check %APPDATA%\Bisik\error.log after failures. Elevated/protected/terminal/custom inaccessible fields may require manual paste.

## Opt-in diagnostics

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
