# AGENTS.md — Bisik

This file is the authoritative guide for AI coding agents working on this repository. Read it fully before writing any code.

---

## What is Bisik?

**Bisik** (Indonesian for "whisper") is a lightweight Windows-first desktop transcription app. It lives in the system tray, activates on a configurable hotkey, records microphone audio, sends it to a speech-to-text API (Whisper-compatible), and outputs the transcribed text at the cursor. It supports an optional LLM post-processing step for cleanup, rephrasing, or translation.

Full feature specification is in [`BUILDPROMPT.md`](./BUILDPROMPT.md). When in doubt about a feature's intended behavior, that document is the source of truth.

---

## Technology Stack

| Concern | Choice |
|---|---|
| Language | C# (.NET 8) |
| UI | WinForms (Windows-first) |
| Audio | NAudio |
| HTTP | `System.Net.Http.HttpClient` (built-in, no extra HTTP library) |
| Auto-type | `InputSimulator` NuGet package |
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
    AudioRecorder.cs         # NAudio mic capture + waveform data stream
    TranscriptionService.cs  # Orchestrates provider call + post-processor call
    HotkeyManager.cs         # Win32 RegisterHotKey P/Invoke wrapper
    OutputHandler.cs         # Auto-type / clipboard / popup dispatch
    HistoryLogger.cs         # Appends TranscriptionEntry to history.jsonl
    SettingsManager.cs       # Load/save/migrate settings.json via DPAPI
  /Providers
    ITranscriptionProvider.cs       # Task<string> TranscribeAsync(byte[] audio, string language)
    AzureOpenAIProvider.cs
    OpenAIProvider.cs
    OpenRouterProvider.cs
    LocalWhisperProvider.cs
    CustomEndpointProvider.cs
  /PostProcessor
    IPostProcessor.cs               # Task<string> ProcessAsync(string rawText)
    LLMPostProcessor.cs
  /Models
    AppSettings.cs                  # Full settings model including PostProcessorSettings
    TranscriptionEntry.cs           # { timestamp, rawText, processedText, provider, language, durationMs }
    ProviderSettings.cs             # Shared model for provider config (transcription + post-processor)
  /UI
    TrayIcon.cs                     # NotifyIcon, context menu, icon state machine
    OverlayWindow.cs                # Frameless always-on-top recording indicator
    SettingsForm.cs                 # Tabbed settings window
    HistoryForm.cs                  # History viewer
  Program.cs                        # Entry point, single-instance mutex, ApplicationContext
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
    Task<string> TranscribeAsync(byte[] audioData, string language, CancellationToken ct);
}
```
The post-processor interface:
```csharp
public interface IPostProcessor
{
    Task<string> ProcessAsync(string rawText, CancellationToken ct);
}
```
Never call provider APIs directly from UI code.

### Settings
- All settings are loaded once at startup via `SettingsManager` and injected where needed.
- API keys are stored encrypted using DPAPI (`ProtectedData.Protect` with `DataProtectionScope.CurrentUser`). Never store keys as plain strings in `settings.json`.
- When adding a new settings field, always provide a sensible default and handle the case where the field is missing (forward compatibility for older settings files).

### Error handling
- Never let an unhandled exception crash the app silently. Wrap the main hotkey handler and transcription pipeline in try/catch and show a tray balloon notification on failure.
- Log all exceptions to `%APPDATA%\Bisik\error.log` with timestamp and stack trace.
- On API failure: show tray notification, do not type anything, do not crash.

---

## Key Implementation Notes

### Hotkey (Ctrl+Win)
- Use `RegisterHotKey` via P/Invoke. Override `WndProc` in the main `ApplicationContext` form to receive `WM_HOTKEY`.
- Be aware: `Win` key combinations can be intercepted by Windows before reaching the app. If `Ctrl+Win` proves unreliable in testing, fall back to `Ctrl+Shift+Space` as default and document this in `README.md`.
- For push-to-talk mode, use both `RegisterHotKey` (keydown) and `WM_HOTKEY` release detection — or alternatively use a low-level keyboard hook (`SetWindowsHookEx WH_KEYBOARD_LL`) which is more reliable for held-key detection.

### Audio recording
- Record as 16-bit PCM, 16000 Hz mono (optimal for Whisper).
- Buffer audio in memory (`MemoryStream`) during recording; do not write temp files to disk unless necessary.
- Expose a `WaveformDataAvailable` event from `AudioRecorder` that fires with recent amplitude values so the overlay can render a live level meter.

### Overlay window
- Must be `TopMost = true`, `FormBorderStyle = None`, `ShowInTaskbar = false`.
- For the "dot near cursor" style, update position on a timer to follow the mouse.
- For the "bar overlay" style, position at top or bottom of the current screen (use `Screen.FromPoint(Cursor.Position)`).
- The overlay must not steal focus from the active window under any circumstances (`SetWindowPos` with `HWND_TOPMOST` and `SWP_NOACTIVATE`).

### Auto-type output
- Use `InputSimulator` for auto-typing. It is more reliable than `SendKeys` for Unicode characters (important for Indonesian text).
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
- Do not implement the WebSocket/streaming Whisper protocol. Bisik uses standard request-response (`multipart/form-data` POST) even with streaming-capable models like `gpt-realtime-whisper`.
- Do not add Electron, WebView2, or any browser-based UI component. This must be a native WinForms app.
- Do not store sensitive data (API keys, endpoints) as plain text anywhere.

---

## Testing Guidance

There are no automated tests yet. When adding logic:
- Write pure functions in Core where possible so they can be unit tested later without mocking UI or audio.
- Keep provider implementations thin — just HTTP call + response parsing. All retry/error logic lives in `TranscriptionService`, not in individual providers.
- The "Test connection" button in Settings should exercise the full pipeline: record 3 seconds → call provider → show result in a dialog. This is the primary integration test for now.

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

---

## Adding a New Provider

1. Create `src/Providers/YourProviderName.cs` implementing `ITranscriptionProvider`
2. Add a new entry to the `ProviderType` enum in `Models/ProviderSettings.cs`
3. Register it in the provider factory in `TranscriptionService.cs`
4. Add UI fields for it in `SettingsForm.cs` (show/hide based on selected provider)
5. Document the required fields (endpoint, key format, etc.) in `README.md`

The same steps apply for adding a provider to the post-processor — it shares the same `ProviderSettings` model and factory pattern.