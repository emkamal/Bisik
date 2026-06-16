# Bisik Architecture Guide

This document explains the architecture, design decisions, and internal workings of the **Bisik** desktop transcription app. It's intended for developers who need to understand, modify, or extend the codebase.

---

## Table of Contents

1. [High-Level Overview](#1-high-level-overview)
2. [Project Structure](#2-project-structure)
3. [Application Lifecycle](#3-application-lifecycle)
4. [Core Layer](#4-core-layer)
5. [Provider Layer](#5-provider-layer)
6. [Post-Processor Layer](#6-post-processor-layer)
7. [Models Layer](#7-models-layer)
8. [UI Layer](#8-ui-layer)
9. [Data Flow — Hotkey to Output](#9-data-flow--hotkey-to-output)
10. [Settings Persistence & Encryption](#10-settings-persistence--encryption)
11. [History Logging](#11-history-logging)
12. [Error Handling Strategy](#12-error-handling-strategy)
13. [Configuration File Formats](#13-configuration-file-formats)
14. [Adding a New Provider](#14-adding-a-new-provider)
15. [Testing Approach](#15-testing-approach)

---

## 1. High-Level Overview

Bisik is a Windows-first, system-tray-based desktop app that:

1. Listens for a configurable global hotkey (default: `Ctrl+Win`)
2. Records microphone audio (16-bit PCM, 16000 Hz mono)
3. Sends the audio to a Whisper-compatible speech-to-text API
4. Optionally passes the raw transcription through an LLM for cleanup/translation
5. Outputs the result via auto-typing, clipboard, or popup

**Key architectural principles:**

- **Separation of concerns**: `Core` classes never reference `UI` classes. UI depends on Core, never the reverse.
- **Provider abstraction**: All transcription providers implement `ITranscriptionProvider`. The `TranscriptionService` is the only class that knows about both providers and post-processors.
- **Single responsibility**: `AudioRecorder` knows nothing about transcription — it captures audio and exposes it as `byte[]` or a file path. `TranscriptionService` knows nothing about audio recording.
- **No `async void`** except in UI event handlers.
- **No `Thread.Sleep`**: use `await Task.Delay` instead.

---

## 2. Project Structure

```
/src
  Program.cs                       # Entry point, single-instance mutex, ApplicationContext
  Bisik.csproj                     # Project file (WinForms, NAudio, InputSimulator)

  /Core
    AudioRecorder.cs               # NAudio mic capture, state machine, VAD, waveform level
    TranscriptionService.cs         # Orchestrates provider call + post-processor call + history logging
    HotkeyManager.cs               # Win32 RegisterHotKey P/Invoke wrapper, gesture parsing
    OutputHandler.cs                # Dispatches to auto-type / clipboard / popup
    HistoryLogger.cs                # Append-only JSONL history file management
    SettingsManager.cs              # Load/save/migrate settings.json with DPAPI encryption
    ErrorLogger.cs                  # Structured error logging with secret redaction
    SoundFeedback.cs                # Embedded WAV resource playback
    StartupRegistration.cs          # Windows startup registry management

  /Providers
    ITranscriptionProvider.cs               # Interface all providers implement
    OpenAICompatibleProviderBase.cs         # Shared base: multipart/form-data, auth headers, JSON parsing
    AzureOpenAIProvider.cs                  # Azure AI Foundry / Azure OpenAI with realtime fallback
    OpenAIProvider.cs                       # Standard OpenAI API (whisper-1)
    OpenRouterProvider.cs                   # OpenRouter
    LocalWhisperProvider.cs                 # Local model via HTTP (whisper.cpp, faster-whisper, Ollama)
    CustomEndpointProvider.cs               # Generic OpenAI-compatible endpoint
    AzureRealtimeTranscriber.cs             # GPT-4o-realtime-preview fallback for Azure

  /PostProcessor
    IPostProcessor.cs                       # Interface for LLM cleanup/translation step
    LLMPostProcessor.cs                     # Sends raw text to chat completions API
    PostProcessorPresets.cs                 # System prompt library (clean-up, homogenize, translate, custom)

  /Models
    AppSettings.cs                          # All settings records + enums + JSON converters
    TranscriptionEntry.cs                   # History record model

  /UI
    TrayIcon.cs                             # NotifyIcon, context menu, 3-state icon, language submenu
    OverlayWindow.cs                        # Frameless always-on-top recording indicator (dot + bar)
    SettingsForm.cs                         # 6-tab settings window
    HistoryForm.cs                          # History viewer with search, filter, export
    ReviewPopupForm.cs                      # Popup output mode review dialog

  /Resources
    success.wav                            # Ding sound for successful transcription
    error.wav                              # Lower tone for transcription failure
```

---

## 3. Application Lifecycle

### 3.1 Entry Point (`Program.cs`)

```csharp
[STAThread]
static void Main()
```

1. **Single-instance guard**: Creates a named `Mutex` (`Global\Bisik.SingleInstance`). If creation fails (another instance is already running), the process exits silently.
2. **Exception handling**: Registers handlers for:
   - `Application.ThreadException` — WinForms UI thread exceptions
   - `AppDomain.CurrentDomain.UnhandledException` — fatal crashes
   - `TaskScheduler.UnobservedTaskException` — async task exceptions
3. **DPI awareness**: Sets `HighDpiMode.PerMonitorV2` for correct overlay positioning on high-DPI/multi-monitor setups.
4. **Application.Run**: Starts the WinForms message loop with `BisikApplicationContext`, a custom `ApplicationContext` that owns the entire app lifetime.

### 3.2 BisikApplicationContext

This is the **composition root** of the application. It:

- Creates all dependencies (settings, HTTP client, recorders, managers, UI wrappers)
- Wires up the hotkey → recording → transcription → output pipeline
- Owns the tray icon lifetime
- Handles settings/history form show/bring-to-front logic
- Manages the push-to-talk release timer (35ms polling for held-key detection)
- Provides the `HiddenMessageWindow` — an invisible WinForms window whose `WndProc` receives `WM_HOTKEY` messages

**Key fields:**

| Field | Type | Purpose |
|---|---|---|
| `_hotkeyGesture` | `HotkeyGesture` | Parsed hotkey from settings |
| `_pushToTalkReleaseTimer` | `Timer` | 35ms interval polling `GetAsyncKeyState` for push-to-talk |
| `_shutdownCancellation` | `CancellationTokenSource` | Signals shutdown to async operations |
| `_messageWindow` | `HiddenMessageWindow` | Invisible form receiving `WM_HOTKEY` |
| `_vadTracker` | `VoiceActivityTracker` | Tracks speech/silence durations for VAD mode |

**Pipeline wiring:**

```
_messageWindow.HotkeyPressed
  → HandleRegisteredHotkeyAsync()
    → hotkeyManager.HandleKeyDown(triggerMode)
      → audioRecorder.StartRecording() / StopRecordingAsync()
        → transcriptionService.TranscribeAsync()
          → outputHandler.DispatchAsync()
```

### 3.3 Shutdown Sequence

`ExitThreadCore()`:
1. Sets `_isShuttingDown = true`
2. Cancels `_shutdownCancellation`
3. Closes settings/history windows
4. Stops push-to-talk timer and VAD monitoring
5. Force-stops recording (if active)
6. Unregisters all hotkeys
7. Disposes: `AudioRecorder`, `OverlayWindow`, `HttpClient`, `CancellationTokenSource`, `HiddenMessageWindow`, `TrayIcon`
8. Calls `base.ExitThreadCore()`

---

## 4. Core Layer

### 4.1 AudioRecorder (`Core/AudioRecorder.cs`)

**Dependencies:** NAudio (`WaveInEvent`, `WaveFileWriter`, `MediaFoundationEncoder`)

**State machine:**

```
Idle → (Start) → Recording → (Stop) → Stopping → (FinalizeStop) → Idle
Any  → (Reset) → Idle
```

**Two operation modes:**

1. **Recording**: `StartRecording()` → captures PCM data to `MemoryStream` via `WaveFileWriter`, writes to temp file on stop.
2. **Voice Activity Monitoring**: `StartVoiceActivityMonitoring()` → separate `WaveInEvent` instance sends audio levels to a callback, no file writing.

**Audio format:**
- 16-bit PCM, 16000 Hz mono (optimal for Whisper)
- Configurable output format: WAV (direct), MP3 (via MediaFoundationEncoder), OGG (not yet implemented — throws `NotSupportedException`)

**Output path:** `%TEMP%\Bisik\recordings\bisik-{yyyyMMdd-HHmmss}.{ext}`

**Key events:**
- `AudioLevelChanged` — fires with normalized amplitude (0.0–1.0) for overlay level meter

**VoiceActivityTracker** (nested class):
- Implements a simple threshold-based VAD
- Tracks consecutive speech duration vs. silence duration
- Configurable: `VadSilenceThreshold` (amplitude threshold), `VadMinSpeechMs` (minimum speech/silence duration)

### 4.2 HotkeyManager (`Core/HotkeyManager.cs`)

**Abstraction:** Uses `IHotkeyRegistrar` interface to allow testing without actual Win32 hotkey registration.

**The default implemention** `Win32HotkeyRegistrar` wraps `RegisterHotKey`/`UnregisterHotKey` P/Invoke.

**Gesture parsing:**
- `ParseHotkey("Ctrl+Win")` → `HotkeyGesture(Modifiers: Ctrl|Win, Key: None)`
- Supports: Ctrl, Shift, Alt, Win as modifiers; any `Keys` enum value as primary key
- Special: "Space" → `Keys.Space`, single letters → `Keys.A` etc.

**Trigger mode behavior:**

| Mode | `HandleKeyDown` | `HandleKeyUp` |
|---|---|---|
| **PushToTalk** | Sets `_pushToTalkActive = true`, returns `StartRecording` | Clears flag, returns `StopRecordingAndTranscribe` |
| **Toggle** | Toggles `_toggleRecordingActive`, returns `StartRecording` or `StopRecordingAndTranscribe` | Returns `None` |
| **VAD** | Toggles `_vadListeningActive`, returns `StartVoiceActivityDetection` or `StopVoiceActivityDetection` | Returns `None` |

### 4.3 TranscriptionService (`Core/TranscriptionService.cs`)

**Orchestrates the full pipeline:**
```
audioFilePath → ResolveProvider(settings) → TranscribeWithRetry()
  → rawText → [optional] postProcessor.ProcessAsync()
    → historyLogger.AppendAsync() → playSuccess → TranscriptionResult
```

**Retry logic:** One automatic retry on `TimeoutException` or `HttpRequestException`.

**Returns `TranscriptionResult`:**
```csharp
record TranscriptionResult(bool Succeeded, string OutputText, string RawText, string ErrorMessage = "");
```

Both `OutputText` (post-processed or raw) and `RawText` are stored in history.

### 4.4 OutputHandler (`Core/OutputHandler.cs`)

Receives delegate-based sinks:
- `autoTypeSink` → uses `InputSimulator` to type Unicode text (50ms delay before typing to release modifier keys)
- `clipboardSink` → `Clipboard.SetText()` 
- `popupSink` → shows `ReviewPopupForm` modally

### 4.5 RealtimeTranscriptionStream (`Core/RealtimeTranscriptionStream.cs`)

A streaming transcription implementation that uses the OpenAI/Azure Realtime API WebSocket protocol.

**Implemented interface:**
- `IRealtimeTranscriber` — `StartAsync()`, `PushAudioChunkAsync()`, `CommitAndFinalizeAsync()`, `DisposeAsync()`
- Events: `PartialTranscriptAvailable` (delta), `TranscriptionCompleted`, `TranscriptionFailed`

**Lifecycle flow:**
```
1. StartAsync() → obtains ephemeral client_secret via REST → connects WebSocket
2. PushAudioChunkAsync() → sends input_audio_buffer.append events
3. (while recording) delta events fire → PartialTranscriptAvailable
4. CommitAndFinalizeAsync() → sends commit → waits for completed transcript
5. DisposeAsync() → closes WebSocket gracefully
```

**Design notes:**
- Supports the same AZR realtime endpoint resolution (`Bisik-Realtime-Calls-Endpoint` custom header, Azure API Management gateway derivation) as `AzureRealtimeTranscriber`.
- Resamples audio to 24 kHz PCM (Azure Realtime expectation) via the existing NAudio pipeline.
- Thread-safe event dispatch via lock.

### 4.6 HistoryLogger (`Core/HistoryLogger.cs`)

**File:** `%APPDATA%\Bisik\history.jsonl` (JSONL = one JSON object per line)

**Operations:**
- `AppendAsync` — thread-safe append via `SemaphoreSlim`-locked file write
- `ReadAllAsync` — parses all lines, skips malformed lines gracefully
- `SearchAsync` — case-insensitive text search across `Text` and `RawText`
- `DeleteAsync` — removes by timestamp+text match, rewrites file via atomic temp-file swap
- `ClearAsync` — deletes the history file
- `ExportToTextAsync` — writes timestamped plain text

**Thread safety:** Uses `ConcurrentDictionary<string, SemaphoreSlim>` keyed by file path, so multiple HistoryLogger instances targeting the same file share a lock.

**Test hooks:** `HistoryLoggerTestHooks` provides `AfterReadOpenAsync` and `BeforeRewriteAsync` callbacks for test synchronization.

### 4.6 ErrorLogger (`Core/ErrorLogger.cs`)

**File:** `%APPDATA%\Bisik\error.log`

**Secret redaction:** The `Sanitize` method strips:
- HTTP request/response headers (entire block redacted)
- JSON property values for `authorization`, `api-key`, `token`, `secret`, `password`
- Bearer tokens
- Assignment-style secrets (`api_key=...`, `secret:...`)

**Thread safety:** Per-file `lock` on a `ConcurrentDictionary`-keyed object, as above.

### 4.7 SoundFeedback (`Core/SoundFeedback.cs`)

Plays embedded WAV resources (`Resources/success.wav`, `Resources/error.wav`) via `System.Media.SoundPlayer.PlaySync()`.

- Plays synchronously (blocks transcription pipeline briefly) — intentional to prevent audio overlap
- Silently no-ops when `soundEnabled = false`

---

## 5. Provider Layer

### 5.1 Interface

```csharp
public interface ITranscriptionProvider
{
    string Name { get; }
    ProviderType Type { get; }
    Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken);
}
```

### 5.2 Base Class: `OpenAICompatibleProviderBase`

All concrete providers inherit from this abstract base, which handles:

- **`SendMultipartRequestAsync`**: POST with `multipart/form-data` containing the audio file, optional model name, and optional language parameter
- **Response parsing**: deserializes `{ "text": "transcribed text" }` from JSON response
- **Header management**:
  - `MergeHeaders` — combines auth headers + extra headers
  - `ToHeaders` — filters out reserved headers (Authorization, api-key, Content-Type, Content-Length)
  - `CreateBearerAuthHeader` / `CreateApiKeyHeader` — structured auth header creation
- **URI utilities**: `RequireAbsoluteUri`, `AppendPath`, `AppendQueryParameter`
- **Error handling**: reads response body on non-success for detailed error messages

### 5.3 Concrete Providers

| Provider | Endpoint | Auth | Model |
|---|---|---|---|
| **OpenAIProvider** | `https://api.openai.com/v1/audio/transcriptions` | Bearer token (`Authorization: Bearer {key}`) | Configurable (default `whisper-1`) |
| **AzureOpenAIProvider** | `{endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version={version}` | `api-key` header | Deployment name or configurable model |
| **OpenRouterProvider** | `https://openrouter.ai/api/v1/audio/transcriptions` | Bearer token | Required (no default) |
| **LocalWhisperProvider** | `{endpoint}/audio/transcriptions` | Optional Bearer token | Optional |
| **CustomEndpointProvider** | `{endpoint}` (fully user-specified) | Optional Bearer token | Optional |

**Azure realtime fallback:** `AzureOpenAIProvider` has a special fallback: if the standard transcription endpoint returns HTTP 400 with `OperationNotSupported` (which happens when using `gpt-realtime-whisper` deployment), it falls back to `AzureRealtimeTranscriber` which posts to the chat completions endpoint with the audio file.

### 5.4 Provider Map Construction

In `BisikApplicationContext.BuildProviderMap()`:
```csharp
new Dictionary<ProviderType, ITranscriptionProvider>
{
    [ProviderType.AzureOpenAi] = new AzureOpenAIProvider(httpClient, settings with { Type = ProviderType.AzureOpenAi }),
    [ProviderType.OpenAi] = new OpenAIProvider(httpClient, settings with { Type = ProviderType.OpenAi }),
    // ... etc
};
```

The settings' `ApiKey` is passed through; the provider's `TranscribeAsync` method uses it at call time. Settings can be updated at runtime via the `SettingsForm`.

---

## 6. Post-Processor Layer

### 6.1 Interface

```csharp
public interface IPostProcessor
{
    Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken cancellationToken);
}
```

### 6.2 LLMPostProcessor (`PostProcessor/LLMPostProcessor.cs`)

Sends the raw transcription to a chat completions API endpoint with a system prompt.

**Request format:**
```json
{
  "model": "...",
  "messages": [
    { "role": "system", "content": "<system prompt>" },
    { "role": "user", "content": "<raw transcription>" }
  ]
}
```

**Response parsing:** Extracts `choices[0].message.content` — supports both string content and content array (text parts).

**URI building:** Same pattern as providers — supports Azure (with deployment + api-version), OpenAI, OpenRouter, Local, and Custom endpoints.

**Model resolution precedence:**
1. `settings.Model`
2. `settings.Provider.Model`
3. Azure OpenAI deployment name

### 6.3 Presets (`PostProcessor/PostProcessorPresets.cs`)

| Preset | Behavior |
|---|---|
| `CleanUp` | Fix grammar, punctuation, remove filler words |
| `HomogenizeToEnglish` | Rewrite as fluent English |
| `HomogenizeToIndonesian` | Rewrite as fluent Indonesian |
| `TranslateToLanguage` | Translate to user-specified target language |
| `Custom` | User-provided system prompt |

---

## 7. Models Layer

### 7.1 AppSettings (`Models/AppSettings.cs`)

**Enums with JSON serialization:**

All enums use `EnumMemberJsonConverter<TEnum>` which serializes via `[EnumMember(Value = "...")]` attributes:

| Enum | Values | JSON Examples |
|---|---|---|
| `TriggerMode` | `PushToTalk`, `Toggle`, `Vad` | `"push-to-talk"`, `"toggle"`, `"vad"` |
| `OutputMode` | `AutoType`, `Clipboard`, `Popup` | `"auto-type"`, `"clipboard"`, `"popup"` |
| `IndicatorStyle` | `DotNearCursor`, `BarOverlay`, `TrayIconOnly` | `"dot-near-cursor"`, `"bar-overlay"`, `"tray-icon-only"` |
| `ProviderType` | `AzureOpenAi`, `OpenAi`, `OpenRouter`, `LocalWhisper`, `CustomEndpoint` | `"azure-openai"`, `"openai"` |
| `AudioFormat` | `Wav`, `Mp3`, `Ogg` | `"wav"`, `"mp3"`, `"ogg"` |
| `PostProcessorPreset` | `CleanUp`, `HomogenizeToEnglish`, `HomogenizeToIndonesian`, `TranslateToLanguage`, `Custom` | `"clean-up"`, `"custom"` |

**Key records:**

- **`ProviderSettings`**: Type, Endpoint, ApiKey, DeploymentName, ApiVersion, Model, ExtraHeaders (key-value dictionary). Also has internal fields (`PersistedUndecryptableApiKeyCiphertext`, `PersistedUndecryptableHeaderCiphertexts`) that survive DPAPI re-encryption cycles.
- **`AudioSettings`**: DeviceId, Format (default WAV), SampleRate (default 16000), VadSilenceThreshold (default 0.01), VadMinSpeechMs (default 300).
- **`PostProcessorSettings`**: Enabled, Provider, Model, TargetLanguage, PresetName, SystemPrompt. Has a custom `PostProcessorSettingsJsonConverter` for robust deserialization.

### 7.2 TranscriptionEntry (`Models/TranscriptionEntry.cs`)

```csharp
record TranscriptionEntry(
    DateTimeOffset Timestamp,
    string Text,           // Post-processed text (or raw if post-processor disabled)
    string RawText,        // Raw transcription from provider
    string Provider,       // Provider name
    string Language,       // Language code used
    double DurationMs,     // Recording duration
    string? AudioFilePath  // Optional path to saved audio file
);
```

---

## 8. UI Layer

### 8.1 TrayIcon (`UI/TrayIcon.cs`)

- **`NotifyIcon`** with system tray presence
- **3-state icons**: Created programmatically via `GDI+` — circles with different colors:
  - Idle: blue/gray (#4474A7)
  - Recording: red (#C43E2F)
  - Transcribing: amber (#D08C24)
- **Context menu**: Settings, History, Language (submenu), Post-Processor toggle, About, Exit
- **Language selection**: quick-access from tray without opening Settings

### 8.2 OverlayWindow (`UI/OverlayWindow.cs`)

A frameless `Form` with `WS_EX_NOACTIVATE` (never steals focus), `WS_EX_TOOLWINDOW` (hidden from Alt+Tab), and `TopMost = true`.

**Two visual modes:**

1. **Dot near cursor** (56×56): Animated pulsing red dot that follows the mouse cursor with a 33ms refresh timer.
2. **Bar overlay** (320×30): Horizontal bar at top or bottom of screen showing a real-time audio level meter (filled proportional to amplitude).

**SetWindowPos** P/Invoke with `HWND_TOPMOST | SWP_NOACTIVATE | SWP_SHOWWINDOW` ensures the overlay stays on top without stealing focus.

**Key properties:**
- `ShowWithoutActivation` → `true` (critical for not stealing focus from the active typing window)
- `DoubleBuffered = true` (smooth animation)
- `BackColor = Magenta`, `TransparencyKey = Magenta` (chroma-key transparency)

### 8.3 SettingsForm (`UI/SettingsForm.cs`)

Six-tab window (~860×640):

1. **General**: Launch at startup, trigger mode, hotkey capture, output mode, indicator style
2. **Provider**: Provider dropdown with dynamic field panels, Test Connection button
3. **Audio**: Microphone selector, format, sample rate, VAD sensitivity
4. **Language**: Default language, quick-switch language list
5. **History**: Enable/disable, file path, open location, clear
6. **Post-Processor**: Enable toggle, provider config, preset selector, system prompt editor

### 8.4 HistoryForm (`UI/HistoryForm.cs`)

Displays transcription history with search, copy, delete, clear, and export to `.txt` functionality.

### 8.5 ReviewPopupForm (`UI/ReviewPopupForm.cs`)

Modal popup for the "popup" output mode — shows transcribed text with copy and dismiss buttons.

---

## 9. Data Flow — Hotkey to Output

### Push-to-Talk Flow

```
1. User presses Ctrl+Win
2. HiddenMessageWindow.WndProc receives WM_HOTKEY (0x0312)
3. → BisikApplicationContext.HandleRegisteredHotkeyAsync()
4.   → hotkeyManager.HandleKeyDown(PushToTalk) → StartRecording
5.   → audioRecorder.StartRecording()
6.   → overlayWindow.ShowDotNearCursor() / ShowBarOverlay()
7.   → trayIcon.SetState(Recording)
8.   → pushToTalkReleaseTimer.Start() (35ms polling)

9. User releases Ctrl+Win
10. pushToTalkReleaseTimer.Tick → IsGestureStillPressed() returns false
11. → hotkeyManager.HandleKeyUp(PushToTalk) → StopRecordingAndTranscribe
12. → CompleteRecordingAndTranscriptionAsync()
13.   → audioRecorder.StopRecordingAsync() → RecordedAudio
14.   → transcriptionService.TranscribeAsync()
15.     → provider.TranscribeAsync(audioFilePath, language)
16.     → [optional] postProcessor.ProcessAsync(rawText)
17.     → historyLogger.AppendAsync(entry)
18.     → soundFeedback.PlaySuccess()
19.   → outputHandler.DispatchAsync(result, outputMode)
20.     → inputSimulator.Keyboard.TextEntry(text) / Clipboard.SetText() / ReviewPopupForm
21.   → trayIcon.SetState(Idle)
```

### VAD Flow

```
1. User presses Ctrl+Win to enable VAD mode
2. → hotkeyManager.HandleKeyDown(Vad) → StartVoiceActivityDetection
3. → audioRecorder.StartVoiceActivityMonitoring(callback)
4.   → monitorWaveIn sends audio levels to HandleVadLevelSample()

5. User starts speaking:
   → VoiceActivityTracker detects speech > VadMinSpeechMs
   → callback returns StartRecording
   → audioRecorder.StartRecording() (stops VAD monitor first)

6. User stops speaking:
   → VoiceActivityTracker detects silence > VadMinSpeechMs
   → callback returns StopRecording
   → CompleteRecordingAndTranscriptionAsync(restartVadMonitoring: true)

7. After transcription completes:
   → If _vadMonitoringEnabled is still true, restarts VAD monitoring
```

### Toggle Flow

```
1. First hotkey press → StartRecording (like push-to-talk but no release detection)
2. Second hotkey press → StopRecordingAndTranscribe → same pipeline as steps 12-21 above
```

---

## 10. Settings Persistence & Encryption

### File location: `%APPDATA%\Bisik\settings.json`

### Encryption scheme (`SettingsManager.cs`)

API keys and sensitive headers are encrypted using **DPAPI** (`ProtectedData.Protect` / `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser`).

**Encryption format:** `dpapi:v1:{base64-ciphertext}`

**Flow:**

1. **Save:** 
   - `ProtectSecrets()` scans `Provider.ApiKey` and `Provider.ExtraHeaders` values
   - If a value isn't empty and doesn't already have the `dpapi:v1:` prefix, it encrypts it with DPAPI
   - The plaintext value is never written to disk

2. **Load:**
   - `UnprotectSecrets()` scans for `dpapi:v1:`-prefixed values
   - Decrypts them and returns plaintext for in-memory use
   - If decryption fails (e.g., different user account or DPAPI scope mismatch):
     - `CryptographicException` → preserves the encrypted ciphertext in `PersistedUndecryptableApiKeyCiphertext` and sets in-memory value to empty
     - `FormatException` (corrupted base64) → clears the value entirely and flags for re-save
   - `AppSettings.HadHealedSecrets` triggers a re-save if any values were repaired

**Extra headers:** Sensitive header names are detected via a heuristic (contains "auth", "key", "token", "secret", "password" — case-insensitive, alphanumeric-only). Safe headers like "Referer" and "User-Agent" are excluded.

**Atomic writes:** Settings are written to a temp file first (`settings.json.{guid}.tmp`), then atomically replaced using `File.Replace` (atomic on NTFS). If the original file doesn't exist, `File.Move` is used instead.

**Corrupted settings handling:** On `JsonException` during load, the corrupted file is backed up to `settings.json.bad` and fresh defaults are written.

---

## 11. History Logging

**Format:** `%APPDATA%\Bisik\history.jsonl` — JSONL (one JSON object per line)

**Example entries:**
```jsonl
{"timestamp":"2026-06-15T12:00:00+07:00","text":"Hello world","rawText":"Hello world","provider":"OpenAI","language":"en","durationMs":2450,"audioFilePath":null}
{"timestamp":"2026-06-15T12:05:00+07:00","text":"Halo, apa kabar?","rawText":"halo apa kabar","provider":"Azure OpenAI","language":"id","durationMs":3200,"audioFilePath":"C:\\Temp\\Bisik\\recordings\\bisik-20260615-120500.wav"}
```

**Thread safety:** Each history file path has a shared `SemaphoreSlim` (via `ConcurrentDictionary<string, SemaphoreSlim>`) to coordinate concurrent reads/writes across instances.

**Best-effort loading:** Malformed JSON lines are silently skipped during parse.

---

## 12. Error Handling Strategy

### Layers of error handling

1. **Global handlers** (in `Program.cs`):
   - `Application.ThreadException` → calls `HandleUnhandledException` → logs to error file → shows tray notification
   - `AppDomain.UnhandledException` → same handler
   - `TaskScheduler.UnobservedTaskException` → marks as observed + logs

2. **Runtime exception handler**: Set after construction in `BisikApplicationContext`, routes to `HandleRuntimeException`:
   - Logs to `error.log`
   - Hides overlay, resets tray icon to idle, shows error balloon notification
   - Catches any secondary exceptions from the error handling itself

3. **Transcription service**: Wraps the entire transcribe + post-process pipeline in try/catch:
   - Logs the exception
   - Plays error sound
   - Returns `TranscriptionResult(Succeeded: false, ErrorMessage: ...)`
   - Does NOT auto-type on failure

4. **Provider level**: 
   - `TranscribeWithRetryAsync` retries once on `TimeoutException` or `HttpRequestException`
   - Non-success HTTP responses are read for error details and thrown as `HttpRequestException`

### Secret redaction in error logs

`ErrorLogger.Sanitize()` uses regex to redact:
- HTTP header blocks (entire request/response headers)
- JSON property values for known sensitive keys
- Formatted secrets (`apikey: XXX`, `Authorization: Bearer XXX`)

---

## 13. Configuration File Formats

### 13.1 `settings.json` (at `%APPDATA%\Bisik\settings.json`)

```json
{
  "triggerMode": "push-to-talk",
  "hotkey": "Ctrl+Win",
  "outputMode": "auto-type",
  "indicatorStyle": "bar-overlay",
  "defaultLanguage": "auto",
  "quickLanguages": ["en", "id", "auto"],
  "launchAtStartup": false,
  "soundEnabled": true,
  "historyEnabled": true,
  "provider": {
    "type": "openai",
    "endpoint": "",
    "apiKey": "dpapi:v1:...",
    "deploymentName": "",
    "apiVersion": "2024-06-01",
    "model": null
  },
  "audio": {
    "deviceId": null,
    "format": "wav",
    "sampleRate": 16000,
    "vadSilenceThreshold": 0.01,
    "vadMinSpeechMs": 300
  },
  "postProcessor": {
    "enabled": false,
    "provider": {
      "type": "azure-openai",
      "endpoint": "",
      "apiKey": "",
      "deploymentName": "",
      "apiVersion": "2024-06-01",
      "model": null
    },
    "model": "gpt-4o-mini",
    "targetLanguage": "",
    "presetName": "clean-up",
    "systemPrompt": "You are a transcription editor..."
  }
}
```

### 13.2 `error.log` (at `%APPDATA%\Bisik\error.log`)

```
2026-06-15T12:00:00.0000000+07:00 | System.Net.Http.HttpRequestException | Provider request failed with status 401 (Unauthorized): {"error":{"message":"Incorrect API key...","type":"invalid_request_error"}}
[REDACTED HEADERS]
   at Bisik.Providers.OpenAICompatibleProviderBase.EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken) in ...
```

### 13.3 `history.jsonl` (at `%APPDATA%\Bisik\history.jsonl`)

See [Section 11](#11-history-logging) above.

---

## 14. Adding a New Provider

1. **Create the provider class** in `src/Providers/` implementing `ITranscriptionProvider`.
   - If it's an OpenAI-compatible (multipart/form-data) endpoint, inherit from `OpenAICompatibleProviderBase`.
   - Override `Name`, `Type`, and `TranscribeAsync`.

2. **Add to the enum** in `Models/AppSettings.cs`:
   - Add a new value to `ProviderType` enum with `[EnumMember(Value = "your-provider-name")]`

3. **Register in the factory** in `Program.cs` → `BuildProviderMap()`:
   ```csharp
   [ProviderType.YourProvider] = new YourProvider(httpClient, settings with { Type = ProviderType.YourProvider })
   ```

4. **Add UI fields** in `SettingsForm.cs`:
   - Add dynamic panel and input fields for the provider's configuration
   - Wire show/hide in the provider selection handler

5. **Document** in `README.md` the required configuration fields.

The same steps apply for post-processor providers — the post-processor shares the same `ProviderType` enum and `ProviderSettings` model.

---

## 15. Testing Approach

There are no automated tests currently, but the architecture supports them:

- **`IHotkeyRegistrar`** interface allows mocking hotkey registration
- **`HistoryLogger`** accepts injectable base directory and test hooks (`HistoryLoggerTestHooks`)
- **`ErrorLogger`** accepts injectable base directory and test hooks (`ErrorLoggerTestHooks`)
- **`SoundFeedback`** accepts injectable resource loader and player
- **Pure logic** in `AudioRecorderLogic` (state transition, level calculation, path building) can be unit tested without audio hardware
- **Provider base class** methods like `CreateMultipartContent`, `AppendPath`, `AppendQueryParameter` are pure and testable

The "Test Connection" button in Settings serves as the primary integration test: it records 3 seconds, runs the full pipeline, and shows the result in a dialog.