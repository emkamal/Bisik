# AGENTS.md — Bisik

This file is the authoritative guide for AI coding agents working on this repository. Read it fully before writing any code.

---

## What is Bisik?

**Bisik** (Indonesian for "whisper") is a lightweight Windows-first desktop transcription app. It lives in the system tray, activates on a configurable hotkey, records microphone audio, sends it to a speech-to-text API (Whisper-compatible), and outputs the transcribed text at the cursor. It supports an optional LLM post-processing step for cleanup, rephrasing, or translation.

Current behavior and verification instructions are in [`README.md`](./README.md). The historical BUILDPROMPT.md is not present. Explicit user requirements take precedence over older guidance.

---

## Technology Stack

| Concern | Choice |
|---|---|
| Language | C# (.NET 8) |
| UI | WinForms (Windows-first) |
| Audio | NAudio |
| HTTP | `System.Net.Http.HttpClient` (built-in, no extra HTTP library) |
| Auto-type | UI Automation target guard + `InputSimulatorCore` Ctrl+V |
| Global hotkeys | Win32 `RegisterHotKey` via P/Invoke |
| Settings | `System.Text.Json` → `%APPDATA%\Bisik\settings.json` |
| History | Append-only JSONL → `%APPDATA%\Bisik\history.jsonl` |
| Key encryption | `System.Security.Cryptography.ProtectedData` (DPAPI) |
| Sound | `System.Media.SoundPlayer` or NAudio |

Do not introduce new dependencies without a clear reason. If you add a NuGet package, explain why in a code comment at the using site.

---

## Project Structure

```
/src
  /Core
    AudioRecorder.cs         # NAudio capture/drain + owned optional file persistence + waveform stream
    TranscriptionService.cs  # Orchestrates provider call + post-processor call
    SessionMetrics.cs       # Bounded monotonic stage/count receipts without content or secrets
    BenchmarkOptions.cs     # Bounded non-inserting diagnostic options
    TranscriptionSession.cs # Per-dictation settings, cancellation and exactly-once completion
    LiveAudioQueue.cs       # Ordered bounded capture queue with one async sender
    SpeculativeCleanup.cs  # Session-scoped whole-text cleanup; final exact-match arbitration
    HotkeyManager.cs         # Win32 RegisterHotKey P/Invoke wrapper
    OutputHandler.cs         # Final-text insertion / clipboard / popup dispatch
    WindowsTextOutput.cs     # Target guard and clipboard transaction orchestration
    WindowsTextOutputPlatform.cs # Win32/UI Automation focus validation
    WindowsClipboard.cs      # Clipboard snapshots with sequence ownership
    LatestResultStore.cs     # Atomic result retention and explicit insertion claims
    FinalTextSelection.cs    # Exactly-once fast/edited result selection with independent cleanup cancellation
    RealtimePrewarmer.cs     # Bounded unused connection and atomic lifetime transfer
    HttpMetricsHandler.cs   # Content-free HTTP first/full response and safe status receipts
    RetryPolicy.cs          # Validated server retry delays within the shared deadline
    HistoryLogger.cs         # Appends TranscriptionEntry to history.jsonl
    HistoryExport.cs         # Complete JSON/CSV/Excel clipboard rows without UI dependencies
    SettingsManager.cs       # Load/save/migrate settings.json via DPAPI
  /Providers
    ITranscriptionProvider.cs       # File-path contract plus optional streaming capability
    AzureOpenAIProvider.cs
    AzureRealtimeTranscriber.cs # Recorded audio through Azure realtime WebSocket
    AzureRealtimeProtocol.cs # Session creation and provider protocol configuration
    OpenAIProvider.cs
    OpenRouterProvider.cs
    LocalWhisperProvider.cs
    CustomEndpointProvider.cs
  /PostProcessor
    IPostProcessor.cs               # Task<string> ProcessAsync(string rawText)
    LLMPostProcessor.cs
  /Models
    AppSettings.cs                  # All settings records, ProviderType and reasoning capability metadata
    TranscriptionEntry.cs           # { timestamp, rawText, processedText, provider, language, durationMs }
    HistoryConfigurationSnapshot.cs # Per-dictation config with connection secrets excluded
  /UI
    TrayIcon.cs                     # NotifyIcon, context menu, icon state machine
    OverlayWindow.cs                # Frameless always-on-top recording indicator
    SettingsForm.cs                 # Tabbed settings window
    HistoryForm.cs                  # History viewer
  Program.cs                        # Entry point, single-instance mutex, ApplicationContext
/tools
  /Bisik.Launcher                    # Stable native Start/startup launcher for workspace builds
  Bisik.LatestBuild.targets          # Successful Build/Publish registration; imported by src/Bisik.csproj
  Register-BisikBuild.ps1            # Immutable runtime copies and atomic latest-build pointer
  Install-BisikLauncher.ps1          # One-time launcher/fallback install preserving the existing pin
  Bisik.BuildSupport.ps1             # Shared file hashing for Windows PowerShell compatibility
```

Stick to this structure. Do not reorganize folders or rename files unless you explain the reason in a comment and update this document.

---

## Architecture Principles

### Separation of concerns
- `Core` classes must not reference `UI` classes. UI depends on Core, never the reverse.
- `TranscriptionService` is the only class that knows about both providers and post-processors. It does not know about audio recording or UI.
- `AudioRecorder` knows nothing about transcription — it just captures audio and exposes it as `byte[]` or a stream.

### Provider interface
Every transcription provider implements:
```csharp
public interface ITranscriptionProvider
{
    string Name { get; }
    Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken ct);
}
```
The post-processor interface:
```csharp
public interface IPostProcessor
{
    Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken ct);
}
```
Never call provider APIs directly from UI code.

### Settings
- All settings are loaded once at startup via `SettingsManager` and injected where needed.
- API keys, endpoints, and sensitive extra headers are stored encrypted using DPAPI (`ProtectedData.Protect` with `DataProtectionScope.CurrentUser`). Never store keys as plain strings in `settings.json`.
- Diagnostic candidates use `SettingsManager.LoadEncryptedCopy`: decrypt in memory, write only an encrypted scratch copy, and never migrate the active source.
- When adding a new settings field, always provide a sensible default and handle the case where the field is missing (forward compatibility for older settings files).

### Error handling
- Never let an unhandled exception crash the app silently. Wrap the main hotkey handler and transcription pipeline in try/catch and show a tray balloon notification on failure.
- Log all exceptions to `%APPDATA%\Bisik\error.log` with timestamp and stack trace.
- On API failure: show tray notification, do not type anything, do not crash.

---

## Key Implementation Notes

### Hotkey (Ctrl+Shift+Space by default)
- Use `RegisterHotKey` via P/Invoke. Override `WndProc` in the main `ApplicationContext` form to receive `WM_HOTKEY`.
- New settings default to Toggle with `Ctrl+Shift+Space`. Preserve existing bindings. Win-key combinations remain supported but may be intercepted by Windows. Check registration failure and use MOD_NOREPEAT.
- For push-to-talk mode, use both `RegisterHotKey` (keydown) and `WM_HOTKEY` release detection — or alternatively use a low-level keyboard hook (`SetWindowsHookEx WH_KEYBOARD_LL`) which is more reliable for held-key detection.

### Audio recording
- Record as 16-bit PCM, 16000 Hz mono (optimal for Whisper).
- Buffer in memory during capture and await RecordingStopped before finalizing the file. The existing provider/history contract uses recorded files; delete failed/empty/no-history recordings. Azure realtime converts continuously to PCM16 mono 24 kHz.
- Expose a `WaveformDataAvailable` event from `AudioRecorder` that fires with recent amplitude values so the overlay can render a live level meter.

### Overlay window
- Must be `TopMost = true`, `FormBorderStyle = None`, `ShowInTaskbar = false`.
- For the "dot near cursor" style, update position on a timer to follow the mouse.
- For the "bar overlay" style, position at top or bottom of the current screen (use `Screen.FromPoint(Cursor.Position)`).
- The overlay must not steal focus from the active window under any circumstances (`SetWindowPos` with `HWND_TOPMOST` and `SWP_NOACTIVATE`).

### Auto-type output
- Capture the intended field and selection before recording. Insert finalized text with Ctrl+V only after verifying focus, field, selection, and modifier release. Never type partials, backspace over user text, send Enter, or automatically submit/execute transcription.
- Add a small delay (~50ms) after recording stops and before typing begins, to allow the user to release modifier keys.
- For clipboard mode: set clipboard text, then optionally simulate `Ctrl+V` (configurable — some users may not want the auto-paste).

### Single instance
- Use a named `Mutex` in `Program.cs`. If another instance is already running, bring its settings window to front and exit.

### App data folder
- All persistent data goes under `%APPDATA%\Bisik\`. Create the folder on first run if it doesn't exist.
- Files: `settings.json`, `history.jsonl`, `error.log`.

---

## What NOT to Do

- Do not use `async void` except in UI event handlers. Use `async Task` everywhere else.
- Do not block the UI thread. All API calls must be `await`ed on background tasks.
- Do not hardcode any API endpoints, keys, or model names.
- Do not use `Thread.Sleep`. Use `await Task.Delay` instead.
- Do not use `dynamic` types. Keep everything strongly typed.
- Azure realtime transcription is explicitly supported. gpt-realtime-whisper uses Azure client_secrets + WSS transcription sessions, not multipart /audio/transcriptions. Preserve multipart support for other models/providers. Do not introduce a browser UI.
- Live capture, phrase commits, speculative cleanup and prewarming are optional and default off. The file path remains the rollback/fallback. One post-Stop deadline covers drain, fallback/retries and cleanup; all background work is owned through shutdown.
- Final results are retained for explicit recovery; raw/edited completion arbitrates once. Clipboard restoration remains serialized and owned after paste dispatch. Dispatch does not prove acceptance by the target.
- Do not add Electron, WebView2, or any browser-based UI component. This must be a native WinForms app.
- Do not store sensitive data (API keys, endpoints) as plain text anywhere.

---

## Testing Guidance

Automated xUnit tests are under tests/Bisik.Tests. Explicit opt-in insertion and Azure probes are under tools/Bisik.Diagnostics. They reference production components; their files and synthetic recordings go under ignored artifacts/. When adding logic:
- Write pure functions in Core where possible so they can be unit tested later without mocking UI or audio.
- Keep provider implementations thin — just HTTP call + response parsing. All retry/error logic lives in `TranscriptionService`, not in individual providers.
- Settings' "Test connection" records 3 seconds and calls the selected provider through `TranscriptionService`, with cleanup/history disabled, then displays a result. The non-inserting `--benchmark` diagnostic includes configured cleanup for full-pipeline measurements.

---

## Commit & PR Conventions

- Commits should be small and focused. One feature or fix per commit.
- Commit message format: `[area] short description` — e.g. `[hotkey] fix push-to-talk not releasing on Win key combos`
- Areas: `hotkey`, `audio`, `provider`, `postprocessor`, `overlay`, `settings`, `history`, `output`, `tray`, `build`, `docs`
- Do not commit `settings.json` with real API keys. The sample `settings.json` in the repo must use placeholder values only.

---

## Build & Run

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run
dotnet run --project src/Bisik.csproj

# Publish as single self-contained exe
dotnet publish src/Bisik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

The output `Bisik.exe` in `./publish` requires no installer and no admin rights.

### Latest workspace build launcher

- On machines where `tools/Install-BisikLauncher.ps1` has registered the launcher, every successful Windows build/publish of `src/Bisik.csproj` automatically updates `%APPDATA%\Bisik\latest-build.json`.
- The installed `Bisik.exe` is the stable launcher. Keep its filename and Start shortcut intact; do not manually copy an app build over it. Runtime copies live under `%APPDATA%\Bisik\builds\`, so a running app does not lock normal build outputs.
- The latest Debug, Release, RID or custom-output build wins. Failed compilation does not update the pointer. Design-time builds skip registration; use `-p:BisikRegisterLatestBuild=false` to opt out for CI/portable packaging.
- Exit Bisik and reopen the existing Start pin to load changed code; rebuilds do not interrupt recording or hot-swap a running process. Startup registration follows the same launcher.
- Cleanup retains five recent copies and protects running copies with a lease. If build copies were cleaned, the launcher uses its packaged fallback. Framework-dependent development builds require the .NET Windows Desktop runtime; self-contained publish copies/fallback do not.

---

## Adding a New Provider

1. Create `src/Providers/YourProviderName.cs` implementing `ITranscriptionProvider`
2. Add a new entry to the `ProviderType` enum in `Models/AppSettings.cs`
3. Register it in the composition root's `Program.BuildProviderMap`
4. Add UI fields for it in `SettingsForm.cs` (show/hide based on selected provider)
5. Document the required fields (endpoint, key format, etc.) in `README.md`

The same steps apply for adding a provider to the post-processor — it shares the same `ProviderSettings` model and factory pattern.

Experiment contracts: Models/ExperimentDefinition.cs and ExperimentResult.cs hold versioned immutable setup/results without connection secrets.

Experiments components: Core/ExperimentMatrix.cs, ExperimentScoring.cs, ExperimentSession.cs, ExperimentRunner.cs, ExperimentStore.cs, ExperimentExport.cs, ExperimentOptions.cs and OperationGate.cs; PostProcessor/LLMExperimentJudge.cs and ICompletionPostProcessor.cs; UI/ExperimentsForm.cs and ExperimentWeightsForm.cs. TabularExport.cs shares only explicit Unicode table formatting with HistoryExport. Models hold immutable versioned setup/take/trial/evaluation records. No public experiment DTO contains ProviderSettings. The composition root owns the suite HTTP client, operation lease, capture and shutdown drain. Keep generation/evaluation separate and all automatic dictation output success-only; recovery claims require explicit user commands.
