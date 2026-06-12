# Build Prompt: Bisik — Windows Desktop Transcription App

**Bisik** (Indonesian for "whisper") is a lightweight Windows-first desktop transcription app inspired by OpenWhisper.

## Project Overview

Build a Windows-first desktop transcription app named **Bisik**. The app runs silently in the system tray, activates on a configurable hotkey, records the user's microphone, sends the audio to a speech-to-text API, and outputs the transcribed text (auto-typed, clipboard, or shown in a popup — configurable). It should feel lightweight and native, not bloated.

---

## Technology Stack

- **Language:** C# (.NET 8)
- **UI Framework:** WinForms (primary target is Windows; design the core logic to be UI-framework-agnostic so Avalonia can be swapped in later for Linux/macOS support)
- **Audio capture:** NAudio
- **HTTP client:** System.Net.Http.HttpClient (built-in)
- **Auto-type/paste:** InputSimulator or SendKeys
- **Hotkey registration:** Win32 `RegisterHotKey` via P/Invoke
- **Settings persistence:** System.Text.Json to a local `settings.json` file
- **History persistence:** Append-only local `history.json` (JSONL format, one entry per line)
- **Sound feedback:** NAudio or System.Media.SoundPlayer

---

## Architecture

Structure the project as follows:

```
/src
  /Core
    AudioRecorder.cs         # NAudio mic capture, waveform data stream
    TranscriptionService.cs  # Provider-agnostic transcription interface
    HotkeyManager.cs         # Win32 RegisterHotKey wrapper
    OutputHandler.cs         # Auto-type / clipboard / popup logic
    HistoryLogger.cs         # Append to history.json
    SettingsManager.cs       # Load/save settings.json
  /Providers
    ITranscriptionProvider.cs        # Interface all providers implement
    AzureOpenAIProvider.cs           # Azure AI Foundry / Azure OpenAI
    OpenAIProvider.cs                # Standard OpenAI API
    OpenRouterProvider.cs            # OpenRouter
    LocalWhisperProvider.cs          # Local model via HTTP (e.g. whisper.cpp server, faster-whisper)
    CustomEndpointProvider.cs        # Generic: user-supplied base URL + API key
  /UI
    TrayIcon.cs              # NotifyIcon setup, context menu
    OverlayWindow.cs         # Floating recording indicator (frameless, always-on-top)
    SettingsForm.cs          # Settings window
    HistoryForm.cs           # View transcription history
  /Models
    AppSettings.cs           # Strongly-typed settings model
    TranscriptionEntry.cs    # History record model
  Program.cs                 # Entry point, single-instance guard
  /PostProcessor
    IPostProcessor.cs        # Interface for the optional LLM cleanup/translation step
    LLMPostProcessor.cs      # Sends raw transcription to a configurable LLM provider + prompt
  /Models
    AppSettings.cs           # Strongly-typed settings model (includes PostProcessorSettings)
    TranscriptionEntry.cs    # History record model
  Program.cs                 # Entry point, single-instance guard
```

---

## Feature Specifications

### 1. System Tray Behavior
- App starts minimized to system tray with no visible window
- Tray icon has a right-click context menu with: **Settings**, **History**, **About**, **Exit**
- Tray icon changes state visually: idle / recording / transcribing
- App must enforce single-instance (prevent duplicate launches)

### 2. Hotkey Trigger Modes (configurable in Settings)
Support all three modes, selectable per user preference:

| Mode | Behavior |
|---|---|
| **Push-to-talk** | Hold hotkey → record. Release → transcribe. |
| **Toggle** | First press starts recording. Second press stops and transcribes. |
| **VAD (Voice Activity Detection)** | Always listening, auto-starts/stops based on silence detection. Hotkey enables/disables VAD mode. |

Default: Push-to-talk.

### 3. Recording Indicator (configurable in Settings)
All three styles must be implemented, user picks one:

| Style | Description |
|---|---|
| **Dot/pulse near cursor** | Small always-on-top frameless window positioned near mouse cursor, shows animated pulsing dot |
| **Bar overlay** | Small always-on-top horizontal bar at top or bottom of screen (like OpenWhisper), shows waveform/level |
| **Tray icon only** | No overlay — tray icon animation only |

The overlay must also display a **real-time audio level meter or waveform** while recording (applies to dot and bar styles).

### 4. Output Mode (configurable in Settings)
All three output modes must be implemented, user picks one:

| Mode | Behavior |
|---|---|
| **Auto-type** | Simulate keyboard input at current cursor position |
| **Clipboard** | Copy transcribed text to clipboard silently |
| **Popup** | Show small review popup with the transcribed text; user can copy or dismiss |

### 5. Transcription Providers
Implement a provider interface (`ITranscriptionProvider`) with the following built-in providers. All providers are selectable in Settings:

#### Azure AI Foundry / Azure OpenAI
- Fields: Endpoint URL, API Key, Deployment Name, API Version
- Auth: `api-key` header
- Endpoint pattern: `{endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version={api_version}`
- Sends: `multipart/form-data` with `file` (wav/mp3), `model` field

#### Standard OpenAI
- Fields: API Key, Model (default: `whisper-1`)
- Endpoint: `https://api.openai.com/v1/audio/transcriptions`

#### OpenRouter
- Fields: API Key, Model
- Endpoint: `https://openrouter.ai/api/v1/audio/transcriptions`

#### Local Model (whisper.cpp / faster-whisper / Ollama)
- Fields: Base URL (e.g. `http://localhost:8080`), Model name (optional)
- No auth required by default, but optional API key field

#### Custom Endpoint
- Fields: Base URL, API Key (optional), Model (optional), extra headers (key-value list)
- Generic fallback for any OpenAI-compatible endpoint

### 6. Language / Locale
- Language is configurable **per session** via a quick-access dropdown in the tray icon context menu (not just in Settings)
- Common options: Auto-detect, English, Indonesian, Japanese, Chinese, Spanish, French, German, + "Other (type manually)"
- The selected language is passed as the `language` parameter in the API request

### 7. Transcription History
- Every successful transcription is appended to `history.jsonl` in the app's data folder (`%APPDATA%\Bisik\`)
- Each entry contains: `timestamp`, `text`, `provider`, `language`, `duration_ms`, `audio_file_path` (optional)
- History is viewable in-app via **History** in the tray context menu
- History window supports: search/filter, copy entry, delete entry, clear all, export to `.txt`

### 8. Settings Window
Organize settings into tabs:

**General tab:**
- Launch at Windows startup (toggle)
- Trigger mode (push-to-talk / toggle / VAD)
- Hotkey (rebindable, click-to-capture)
- Output mode (auto-type / clipboard / popup)
- Recording indicator style

**Provider tab:**
- Provider selector dropdown
- Dynamic fields per provider (show/hide based on selection)
- "Test connection" button that records 3 seconds and runs a test transcription

**Audio tab:**
- Microphone device selector
- Audio format for upload (wav / mp3 / ogg — configurable)
- Sample rate (16000 Hz default)
- VAD sensitivity (silence threshold, min speech duration)

**Language tab:**
- Default language for transcription
- Quick-switch language list (which languages appear in tray menu)

**History tab:**
- Toggle history logging on/off
- Show current history file path
- Open history file location button
- Clear history button

**Post-Processor tab:**
- Toggle LLM post-processing on/off
- LLM provider (separate from transcription provider — same provider options available)
- Model name
- Preset selector: *Clean up*, *Homogenize to English*, *Homogenize to Indonesian*, *Translate to...*, *Custom*
- System prompt textarea (editable, populated by preset selection)

### 9. LLM Post-Processor (AI Cleanup & Translation)

After transcription, Bisik can optionally pass the raw text through a second LLM call for cleanup, rephrasing, or translation. This is particularly useful for multilingual speakers who mix languages in the same sentence (e.g. Indonesian + English code-switching).

**Pipeline:**
```
Audio → Whisper (transcription) → [optional] LLM Post-Processor → output
```

**Behavior:**
- Toggle on/off in Settings or via tray context menu
- LLM provider is configured independently from the transcription provider — can be any OpenAI-compatible endpoint (Azure AI Foundry, OpenAI, OpenRouter, local Ollama, custom)
- The raw transcription text is sent as the user message; the system prompt defines the behavior
- Built-in system prompt presets:
  - *"Clean up"* — fix grammar, remove filler words, add punctuation
  - *"Homogenize to English"* — mixed-language input → clean English output
  - *"Homogenize to Indonesian"* — mixed-language input → clean Indonesian output
  - *"Translate to [language]"* — full translation, target language configurable
  - *"Custom"* — user writes their own system prompt
- History log stores both the **raw transcription** and the **post-processed result** as separate fields

### 10. Completion Feedback
- Play a short ding/chime sound when transcription completes successfully
- Play a different (lower) tone on transcription error
- Sound files should be bundled as embedded resources
- Option in Settings to disable sounds

### 11. Error Handling
- If the API call fails, show a non-intrusive tray notification with the error reason
- Do not auto-type anything if transcription failed
- Retry logic: 1 automatic retry on network timeout, then fail gracefully
- Log errors to `%APPDATA%\Bisik\error.log`

---

## Settings File Schema

Store settings at `%APPDATA%\Bisik\settings.json`. Example structure:

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
    "type": "azure-openai",
    "endpoint": "",
    "apiKey": "",
    "deploymentName": "",
    "apiVersion": "2024-06-01"
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
      "apiVersion": "2024-06-01"
    },
    "model": "",
    "presetName": "clean-up",
    "systemPrompt": "You are a transcription editor. Fix grammar, punctuation, and remove filler words. Return only the cleaned text, nothing else."
  }
}
```

**Security note:** API keys in settings.json must be encrypted at rest using `System.Security.Cryptography.ProtectedData` (DPAPI — Windows user-scoped encryption).

---

## Non-Functional Requirements

- **Startup time:** App must be ready (tray icon visible) within 2 seconds of launch
- **Memory:** Idle memory footprint should be under 50MB
- **No installer required:** App should be publishable as a single self-contained `.exe` (`dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true`)
- **No admin rights required** for normal operation
- **DPI awareness:** Mark the app as DPI-aware so overlays position correctly on high-DPI / multi-monitor setups

---

## Out of Scope (for now, note as future work)

- macOS / Linux support (architecture should not block this, but don't implement it yet)
- Real-time streaming transcription protocol (WebSocket chunk-based). Note: streaming-capable models like `gpt-realtime-whisper` deployed on Azure are fully supported — Bisik uses them in standard request-response mode, not via the streaming protocol
- Speaker diarization
- Cloud sync of history

---

## Deliverables

1. Full working Visual Studio solution (`.sln`) named `Bisik` with the project structure above
2. `README.md` with: setup instructions, how to configure each provider, how to build and publish as single `.exe`
3. A sample `settings.json` with placeholder values and post-processor presets
4. Bundled sound assets (ding + error tone) as embedded resources