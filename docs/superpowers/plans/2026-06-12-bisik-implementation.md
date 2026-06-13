# Bisik Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Bisik, a Windows-first .NET 8 WinForms tray transcription app with hotkey recording, provider-based transcription, optional LLM post-processing, configurable output, and local history.

**Architecture:** Use a thin WinForms shell over UI-agnostic core services. Keep platform-specific Windows integrations isolated in small classes so the core pipeline, settings, providers, and history can be reused if another UI is added later.

**Tech Stack:** C#/.NET 8, WinForms, NAudio, System.Text.Json, System.Net.Http, Win32 P/Invoke, DPAPI (`ProtectedData`), xUnit.

---

## File Map

- Create `Bisik.sln`: solution containing the app and tests.
- Create `src/Bisik.csproj`: Windows-only WinForms app targeting `net8.0-windows`.
- Create `src/Program.cs`: single-instance startup, DPI setup, dependency wiring, tray app lifetime.
- Create `src/Models/AppSettings.cs`: strongly typed settings including provider, audio, language, history, sound, and post-processor settings.
- Create `src/Models/TranscriptionEntry.cs`: history entry model with raw and post-processed text fields.
- Create `src/Core/SettingsManager.cs`: load/save `%APPDATA%\Bisik\settings.json`, defaults, DPAPI API key protection.
- Create `src/Core/HistoryLogger.cs`: append/read/delete/clear/export `%APPDATA%\Bisik\history.jsonl`.
- Create `src/Core/ErrorLogger.cs`: append `%APPDATA%\Bisik\error.log`.
- Create `src/Core/AudioRecorder.cs`: NAudio microphone capture, fixed sample rate conversion, live level events, recorded audio output.
- Create `src/Core/HotkeyManager.cs`: Win32 hotkey registration and key event routing.
- Create `src/Core/TranscriptionService.cs`: orchestration from audio file to provider result with retry and error handling.
- Create `src/Core/OutputHandler.cs`: auto-type, clipboard, and popup output behavior.
- Create `src/Core/SoundFeedback.cs`: embedded success/error tones and enable/disable handling.
- Create `src/Core/StartupRegistration.cs`: launch-at-startup registry toggle.
- Create `src/Providers/ITranscriptionProvider.cs`: provider interface.
- Create `src/Providers/OpenAICompatibleProviderBase.cs`: shared multipart upload and OpenAI-compatible response parsing.
- Create `src/Providers/AzureOpenAIProvider.cs`: Azure audio transcription endpoint.
- Create `src/Providers/OpenAIProvider.cs`: standard OpenAI audio transcription endpoint.
- Create `src/Providers/OpenRouterProvider.cs`: OpenRouter audio transcription endpoint.
- Create `src/Providers/LocalWhisperProvider.cs`: local HTTP transcription endpoint.
- Create `src/Providers/CustomEndpointProvider.cs`: custom OpenAI-compatible endpoint.
- Create `src/PostProcessor/IPostProcessor.cs`: post-processing interface.
- Create `src/PostProcessor/LLMPostProcessor.cs`: OpenAI-compatible chat completion cleanup/translation call.
- Create `src/PostProcessor/PostProcessorPresets.cs`: built-in system prompts.
- Create `src/UI/TrayIcon.cs`: notify icon, context menu, language quick-switch, post-processor toggle, lifecycle actions.
- Create `src/UI/OverlayWindow.cs`: dot and bar recording indicators with audio level display.
- Create `src/UI/SettingsForm.cs`: tabbed settings UI.
- Create `src/UI/HistoryForm.cs`: searchable history UI with copy/delete/clear/export.
- Create `src/UI/ReviewPopupForm.cs`: popup output review window.
- Create `src/Resources/success.wav` and `src/Resources/error.wav`: embedded tones.
- Create `sample.settings.json`: documented placeholder settings file.
- Create `README.md`: setup, provider configuration, development, publish instructions.
- Create `tests/Bisik.Tests/Bisik.Tests.csproj`: xUnit test project.
- Create `tests/Bisik.Tests/*Tests.cs`: focused tests for settings, history, providers, post-processing, and pipeline behavior.

---

## Task 1: Scaffold Solution and Projects

**Files:**
- Create: `Bisik.sln`
- Create: `src/Bisik.csproj`
- Create: `tests/Bisik.Tests/Bisik.Tests.csproj`

- [x] **Step 1: Create the solution**

  Run:

  ```powershell
  dotnet new sln -n Bisik
  ```

  Expected: `Bisik.sln` exists at the repo root.

- [x] **Step 2: Create the WinForms app project**

  Run:

  ```powershell
  dotnet new winforms -n Bisik -o src -f net8.0-windows
  ```

  Expected: `src/Bisik.csproj` and a default WinForms entry point are created.

- [x] **Step 3: Create the xUnit test project**

  Run:

  ```powershell
  dotnet new xunit -n Bisik.Tests -o tests/Bisik.Tests -f net8.0
  ```

  Expected: `tests/Bisik.Tests/Bisik.Tests.csproj` exists.

- [x] **Step 4: Add projects to the solution**

  Run:

  ```powershell
  dotnet sln Bisik.sln add src/Bisik.csproj
  dotnet sln Bisik.sln add tests/Bisik.Tests/Bisik.Tests.csproj
  dotnet add tests/Bisik.Tests/Bisik.Tests.csproj reference src/Bisik.csproj
  ```

  Expected: both projects are listed by `dotnet sln Bisik.sln list`.

- [x] **Step 5: Add required NuGet packages**

  Run:

  ```powershell
  dotnet add src/Bisik.csproj package NAudio
  dotnet add src/Bisik.csproj package InputSimulatorCore
  dotnet add tests/Bisik.Tests/Bisik.Tests.csproj package FluentAssertions
  ```

  Expected: restore succeeds and package references appear in the project files.

- [x] **Step 6: Configure the app project**

  Edit `src/Bisik.csproj` so it contains these project settings:

  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <OutputType>WinExe</OutputType>
      <TargetFramework>net8.0-windows</TargetFramework>
      <Nullable>enable</Nullable>
      <ImplicitUsings>enable</ImplicitUsings>
      <UseWindowsForms>true</UseWindowsForms>
      <ApplicationManifest>app.manifest</ApplicationManifest>
    </PropertyGroup>
  </Project>
  ```

- [x] **Step 7: Add DPI awareness manifest**

  Create `src/app.manifest` with per-monitor DPI awareness enabled:

  ```xml
  <?xml version="1.0" encoding="utf-8"?>
  <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
    <application xmlns="urn:schemas-microsoft-com:asm.v3">
      <windowsSettings>
        <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      </windowsSettings>
    </application>
  </assembly>
  ```

- [x] **Step 8: Build the empty solution**

  Run:

  ```powershell
  dotnet build Bisik.sln
  ```

  Expected: build succeeds before feature work starts.

- [x] **Step 9: Commit scaffold**

  Run:

  ```powershell
  git add Bisik.sln src tests
  git commit -m "chore: scaffold Bisik solution"
  ```

---

## Task 2: Define Models and Defaults

**Files:**
- Create: `src/Models/AppSettings.cs`
- Create: `src/Models/TranscriptionEntry.cs`
- Create: `tests/Bisik.Tests/AppSettingsTests.cs`

- [x] **Step 1: Write model default tests**

  Add tests proving defaults match the spec: push-to-talk trigger, auto-type output, bar overlay indicator, auto language, Azure provider defaults, 16000 Hz audio, history and sound enabled, post-processing disabled.

- [x] **Step 2: Implement settings enums and records**

  In `src/Models/AppSettings.cs`, define enums for trigger mode, output mode, indicator style, provider type, audio format, and post-processor preset. Use JSON string enum conversion so settings stay human-readable.

- [x] **Step 3: Implement nested settings types**

  Add `ProviderSettings`, `AudioSettings`, `PostProcessorSettings`, and `AppSettings` with defaults from `spec.md`. Keep API keys as strings here; encryption belongs in `SettingsManager`.

- [x] **Step 4: Implement history entry model**

  In `src/Models/TranscriptionEntry.cs`, include:

  ```csharp
  public sealed record TranscriptionEntry(
      DateTimeOffset Timestamp,
      string Text,
      string? RawText,
      string Provider,
      string Language,
      long DurationMs,
      string? AudioFilePath);
  ```

- [x] **Step 5: Run model tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter AppSettingsTests
  ```

  Expected: tests pass.

- [x] **Step 6: Commit models**

  Run:

  ```powershell
  git add src/Models tests/Bisik.Tests/AppSettingsTests.cs
  git commit -m "feat: add settings and history models"
  ```

---

## Task 3: Implement Settings Persistence with DPAPI

**Files:**
- Create: `src/Core/SettingsManager.cs`
- Create: `tests/Bisik.Tests/SettingsManagerTests.cs`

- [x] **Step 1: Write settings persistence tests**

  Cover first-run default creation, round-trip save/load, malformed JSON fallback behavior, and API key encryption at rest. The saved file must not contain plain API key values.

- [x] **Step 2: Implement app data path handling**

  Add a constructor that accepts an optional base directory for tests and defaults to `%APPDATA%\Bisik` in production.

- [x] **Step 3: Implement JSON serialization options**

  Use indented JSON, camelCase names, and string enums. Keep schema stable with the sample in `spec.md`.

- [x] **Step 4: Implement DPAPI protection**

  Encrypt provider API keys before writing with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`. Store encrypted values using a clear prefix such as `dpapi:v1:`.

- [x] **Step 5: Implement DPAPI unprotect on load**

  If an API key starts with `dpapi:v1:`, decrypt it. If it does not, treat it as legacy/plain input, return it, and re-encrypt on the next save.

- [x] **Step 6: Run settings tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter SettingsManagerTests
  ```

  Expected: tests pass and no test writes outside its temp directory.

- [x] **Step 7: Commit settings persistence**

  Run:

  ```powershell
  git add src/Core/SettingsManager.cs tests/Bisik.Tests/SettingsManagerTests.cs
  git commit -m "feat: persist settings with encrypted secrets"
  ```

---

## Task 4: Implement History and Error Logging

**Files:**
- Create: `src/Core/HistoryLogger.cs`
- Create: `src/Core/ErrorLogger.cs`
- Create: `tests/Bisik.Tests/HistoryLoggerTests.cs`
- Create: `tests/Bisik.Tests/ErrorLoggerTests.cs`

- [x] **Step 1: Write history logger tests**

  Cover JSONL append, read all, search by text, delete a single entry, clear all, export to `.txt`, and disabled history mode.

- [x] **Step 2: Implement append-only JSONL writes**

  Append one compact JSON object per line to `%APPDATA%\Bisik\history.jsonl`. Create the directory automatically.

- [x] **Step 3: Implement history read/search/delete/clear/export**

  Use the `Timestamp` plus text as the stable delete selector unless an explicit ID is added to the model. Export plain text in chronological order.

- [x] **Step 4: Write error logger tests**

  Cover creating `error.log`, appending exception messages, and including timestamps.

- [x] **Step 5: Implement error logger**

  Append non-sensitive error details to `%APPDATA%\Bisik\error.log`. Do not write API keys or full request headers.

- [x] **Step 6: Run logging tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter "HistoryLoggerTests|ErrorLoggerTests"
  ```

  Expected: tests pass.

- [x] **Step 7: Commit logging**

  Run:

  ```powershell
  git add src/Core/HistoryLogger.cs src/Core/ErrorLogger.cs tests/Bisik.Tests/HistoryLoggerTests.cs tests/Bisik.Tests/ErrorLoggerTests.cs
  git commit -m "feat: add local history and error logging"
  ```

---

## Task 5: Implement Provider Interface and HTTP Providers

**Files:**
- Create: `src/Providers/ITranscriptionProvider.cs`
- Create: `src/Providers/OpenAICompatibleProviderBase.cs`
- Create: `src/Providers/AzureOpenAIProvider.cs`
- Create: `src/Providers/OpenAIProvider.cs`
- Create: `src/Providers/OpenRouterProvider.cs`
- Create: `src/Providers/LocalWhisperProvider.cs`
- Create: `src/Providers/CustomEndpointProvider.cs`
- Create: `tests/Bisik.Tests/ProviderRequestTests.cs`

- [x] **Step 1: Write provider request tests**

  Use a fake `HttpMessageHandler` to assert each provider sends the correct URL, auth header, multipart `file`, `model`, and `language` fields.

- [x] **Step 2: Define provider contract**

  `ITranscriptionProvider` should expose provider name/type and:

  ```csharp
  Task<string> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken);
  ```

- [x] **Step 3: Implement shared multipart upload base**

  Add protected helpers for `MultipartFormDataContent`, response status validation, and parsing `{ "text": "..." }` responses.

- [x] **Step 4: Implement Azure provider**

  Build URL as `{endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version={apiVersion}` and authenticate with `api-key`.

- [x] **Step 5: Implement OpenAI provider**

  Post to `https://api.openai.com/v1/audio/transcriptions`, authenticate with bearer token, and default model to `whisper-1`.

- [x] **Step 6: Implement OpenRouter provider**

  Post to `https://openrouter.ai/api/v1/audio/transcriptions` with bearer token and configured model.

- [x] **Step 7: Implement local provider**

  Post to `{baseUrl}/audio/transcriptions` by default. Include optional bearer auth only when an API key is configured.

- [x] **Step 8: Implement custom endpoint provider**

  Post to configured base URL, include optional API key, optional model, language, and configured extra headers.

- [x] **Step 9: Run provider tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter ProviderRequestTests
  ```

  Expected: tests pass without making network calls.

- [x] **Step 10: Commit providers**

  Run:

  ```powershell
  git add src/Providers tests/Bisik.Tests/ProviderRequestTests.cs
  git commit -m "feat: add transcription providers"
  ```

---

## Task 6: Implement LLM Post-Processor

**Files:**
- Create: `src/PostProcessor/IPostProcessor.cs`
- Create: `src/PostProcessor/LLMPostProcessor.cs`
- Create: `src/PostProcessor/PostProcessorPresets.cs`
- Create: `tests/Bisik.Tests/PostProcessorTests.cs`

- [x] **Step 1: Write post-processor tests**

  Cover disabled passthrough, preset prompt generation, custom prompt use, provider auth, and parsing chat completion output.

- [x] **Step 2: Implement preset prompts**

  Add clean-up, homogenize-to-English, homogenize-to-Indonesian, translate-to-language, and custom prompt handling.

- [x] **Step 3: Implement post-processor contract**

  `IPostProcessor` should return both raw and final text context:

  ```csharp
  Task<string> ProcessAsync(string rawText, PostProcessorSettings settings, CancellationToken cancellationToken);
  ```

- [x] **Step 4: Implement OpenAI-compatible chat call**

  Send the selected system prompt and raw transcription as the user message. Return only the model text content.

- [x] **Step 5: Run post-processor tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter PostProcessorTests
  ```

  Expected: tests pass with fake HTTP.

- [x] **Step 6: Commit post-processing**

  Run:

  ```powershell
  git add src/PostProcessor tests/Bisik.Tests/PostProcessorTests.cs
  git commit -m "feat: add LLM post-processing"
  ```

---

## Task 7: Implement Audio Recording and Levels

**Files:**
- Create: `src/Core/AudioRecorder.cs`
- Create: `tests/Bisik.Tests/AudioRecorderTests.cs`

- [x] **Step 1: Write audio recorder unit tests for pure logic**

  Keep tests focused on device-independent logic: level calculation, silence detection thresholds, output filename generation, and recording state transitions.

- [x] **Step 2: Implement recording state model**

  Track `Idle`, `Recording`, and `Stopping` states so hotkey and VAD code cannot start duplicate recordings.

- [x] **Step 3: Implement NAudio capture**

  Capture the selected microphone device, convert or write to the configured format, and default to 16000 Hz WAV.

- [x] **Step 4: Emit live audio level events**

  Raise an event with a normalized level from `0.0` to `1.0` for overlay and tray animation.

- [x] **Step 5: Implement VAD helpers**

  Add silence threshold and minimum speech duration logic using the audio settings. Keep the always-listening loop separate from the UI.

- [x] **Step 6: Run audio tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter AudioRecorderTests
  ```

  Expected: pure logic tests pass. Manual microphone testing happens after UI wiring.

- [x] **Step 7: Commit audio recording**

  Run:

  ```powershell
  git add src/Core/AudioRecorder.cs tests/Bisik.Tests/AudioRecorderTests.cs
  git commit -m "feat: add microphone recording service"
  ```

---

## Task 8: Implement Hotkeys and Trigger Modes

**Files:**
- Create: `src/Core/HotkeyManager.cs`
- Create: `tests/Bisik.Tests/HotkeyManagerTests.cs`

- [x] **Step 1: Write hotkey parsing tests**

  Cover parsing configured strings such as `Ctrl+Win`, validating unsupported combinations, and formatting captured hotkeys back to settings.

- [x] **Step 2: Implement Win32 registration wrapper**

  Use `RegisterHotKey` and `UnregisterHotKey` through P/Invoke. Keep OS calls behind an interface so tests can use a fake registrar.

- [x] **Step 3: Implement push-to-talk behavior**

  Press starts recording. Release stops recording and begins transcription.

- [x] **Step 4: Implement toggle behavior**

  First press starts recording. Second press stops recording and begins transcription.

- [x] **Step 5: Implement VAD enable/disable behavior**

  Hotkey toggles VAD listening. VAD starts/stops recordings based on `AudioRecorder` level and silence helpers.

- [x] **Step 6: Run hotkey tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter HotkeyManagerTests
  ```

  Expected: parser and state-machine tests pass without registering real global hotkeys.

- [x] **Step 7: Commit hotkeys**

  Run:

  ```powershell
  git add src/Core/HotkeyManager.cs tests/Bisik.Tests/HotkeyManagerTests.cs
  git commit -m "feat: add global hotkey trigger modes"
  ```

---

## Task 9: Implement Transcription Pipeline

**Files:**
- Create: `src/Core/TranscriptionService.cs`
- Create: `tests/Bisik.Tests/TranscriptionServiceTests.cs`

- [x] **Step 1: Write pipeline tests**

  Cover provider selection, language forwarding, one retry on timeout, no output on failure, raw/final history fields, sound feedback calls, and error logging.

- [x] **Step 2: Implement provider factory**

  Convert `ProviderSettings.Type` into the correct `ITranscriptionProvider` instance.

- [x] **Step 3: Implement transcription orchestration**

  Run provider transcription, optionally run post-processing, log history, play success sound, and return final output text.

- [x] **Step 4: Implement retry behavior**

  Retry once for timeout/network errors only. Do not retry validation errors such as missing API key or invalid endpoint.

- [x] **Step 5: Implement failure behavior**

  Log the error, play error sound, return a failed result, and leave output text empty.

- [x] **Step 6: Run pipeline tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter TranscriptionServiceTests
  ```

  Expected: tests pass with fake providers and fake dependencies.

- [x] **Step 7: Commit pipeline**

  Run:

  ```powershell
  git add src/Core/TranscriptionService.cs tests/Bisik.Tests/TranscriptionServiceTests.cs
  git commit -m "feat: orchestrate transcription pipeline"
  ```

---

## Task 10: Implement Output and Feedback

**Files:**
- Create: `src/Core/OutputHandler.cs`
- Create: `src/Core/SoundFeedback.cs`
- Create: `src/UI/ReviewPopupForm.cs`
- Create: `src/Resources/success.wav`
- Create: `src/Resources/error.wav`
- Create: `tests/Bisik.Tests/OutputHandlerTests.cs`

- [x] **Step 1: Write output handler tests**

  Cover auto-type dispatch, clipboard mode, popup mode, and "do nothing on failed transcription" behavior using fakes for UI-facing dependencies.

- [x] **Step 2: Implement auto-type output**

  Use InputSimulator where possible. Fall back to `SendKeys.SendWait` only if the selected library cannot type the required characters.

- [x] **Step 3: Implement clipboard output**

  Set clipboard text silently on the UI thread.

- [x] **Step 4: Implement review popup output**

  Show a small always-on-top form with transcribed text plus Copy and Dismiss actions.

- [x] **Step 5: Add embedded sound resources**

  Add short success and error WAV files under `src/Resources` and configure them as embedded resources in `src/Bisik.csproj`.

- [x] **Step 6: Implement sound feedback**

  Respect `soundEnabled`; play success on completed transcription and lower error tone on failure.

- [x] **Step 7: Run output tests**

  Run:

  ```powershell
  dotnet test tests/Bisik.Tests/Bisik.Tests.csproj --filter OutputHandlerTests
  ```

  Expected: tests pass without interacting with the real clipboard or keyboard.

- [x] **Step 8: Commit output and sounds**

  Run:

  ```powershell
  git add src/Core/OutputHandler.cs src/Core/SoundFeedback.cs src/UI/ReviewPopupForm.cs src/Resources tests/Bisik.Tests/OutputHandlerTests.cs
  git commit -m "feat: add transcription output modes"
  ```

---

## Task 11: Implement Tray App Shell and Overlays

**Files:**
- Modify: `src/Program.cs`
- Create: `src/UI/TrayIcon.cs`
- Create: `src/UI/OverlayWindow.cs`
- Create: `src/Core/StartupRegistration.cs`

- [x] **Step 1: Replace default WinForms startup**

  Update `src/Program.cs` to use `ApplicationContext` instead of showing a main form. Enable visual styles and high DPI mode before starting the context.

- [x] **Step 2: Add single-instance guard**

  Use a named mutex such as `Global\Bisik.SingleInstance`. If another instance exists, exit cleanly.

- [x] **Step 3: Implement tray icon states**

  Add idle, recording, and transcribing visual states. Use simple generated icons first; replace with designed icons later if desired.

- [x] **Step 4: Implement tray context menu**

  Add Settings, History, About, Exit, current language submenu, and post-processor enabled toggle.

- [x] **Step 5: Implement language quick-switch menu**

  Include Auto-detect, English, Indonesian, Japanese, Chinese, Spanish, French, German, and Other. Store selection for the current session and pass it to the pipeline.

- [x] **Step 6: Implement dot overlay**

  Create a frameless always-on-top window near the cursor with a pulsing dot and live level meter.

- [x] **Step 7: Implement bar overlay**

  Create a small top/bottom horizontal bar with waveform or level display.

- [x] **Step 8: Implement tray-only indicator mode**

  Suppress overlay windows and use tray icon animation only.

- [x] **Step 9: Implement launch-at-startup toggle helper**

  Add current-user registry handling under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Do not require admin rights.

- [x] **Step 10: Manual smoke test tray startup**

  Run:

  ```powershell
  dotnet run --project src/Bisik.csproj
  ```

  Expected: app appears only in the tray within 2 seconds, no main window opens, and Exit closes the process.

- [x] **Step 11: Commit shell**

  Run:

  ```powershell
  git add src/Program.cs src/UI/TrayIcon.cs src/UI/OverlayWindow.cs src/Core/StartupRegistration.cs
  git commit -m "feat: add tray shell and recording indicators"
  ```

---

## Task 12: Implement Settings Window

**Files:**
- Create: `src/UI/SettingsForm.cs`

- [x] **Step 1: Create tabbed settings form**

  Add General, Provider, Audio, Language, History, and Post-Processor tabs.

- [x] **Step 2: Implement General tab**

  Add launch-at-startup toggle, trigger mode dropdown, click-to-capture hotkey control, output mode dropdown, and recording indicator style dropdown.

- [x] **Step 3: Implement Provider tab**

  Add provider selector, dynamic fields for each provider, API key fields, and a Test Connection button.

- [x] **Step 4: Implement 3-second test transcription**

  Test Connection records 3 seconds, sends it through the selected provider, and shows a non-blocking success or failure message.

- [x] **Step 5: Implement Audio tab**

  Add microphone selector, upload format dropdown, sample rate input, VAD silence threshold input, and minimum speech duration input.

- [x] **Step 6: Implement Language tab**

  Add default language selector and editable quick-switch language list.

- [x] **Step 7: Implement History tab**

  Add history enabled toggle, current file path display, open location button, and clear history button.

- [x] **Step 8: Implement Post-Processor tab**

  Add enabled toggle, provider fields, model name, preset selector, target language for translate preset, and system prompt textarea.

- [x] **Step 9: Save and reload settings**

  Wire OK/Apply/Cancel behavior through `SettingsManager`. Saving must encrypt API keys.

- [x] **Step 10: Manual smoke test settings**

  Run the app, open Settings from the tray, change each tab, save, close, reopen, and verify values persist in `%APPDATA%\Bisik\settings.json`.

- [x] **Step 11: Commit settings UI**

  Run:

  ```powershell
  git add src/UI/SettingsForm.cs
  git commit -m "feat: add settings window"
  ```

---

## Task 13: Implement History Window

**Files:**
- Create: `src/UI/HistoryForm.cs`

- [x] **Step 1: Build history list UI**

  Show timestamp, provider, language, duration, raw text, and final text where available.

- [x] **Step 2: Add search/filter**

  Filter entries by text, provider, or language without rewriting the history file.

- [x] **Step 3: Add copy entry action**

  Copy final text when present, otherwise raw text.

- [x] **Step 4: Add delete entry action**

  Remove one entry by rewriting the JSONL file without that record.

- [x] **Step 5: Add clear all action**

  Confirm once, then clear the JSONL file.

- [x] **Step 6: Add export to `.txt` action**

  Export visible entries to a user-selected `.txt` file.

- [x] **Step 7: Manual smoke test history UI**

  Seed a few history entries, open History from tray, search, copy, delete, clear, and export.

- [x] **Step 8: Commit history UI**

  Run:

  ```powershell
  git add src/UI/HistoryForm.cs
  git commit -m "feat: add history window"
  ```

---

## Task 14: Wire End-to-End Recording Flow

**Files:**
- Modify: `src/Program.cs`
- Modify: `src/UI/TrayIcon.cs`
- Modify: `src/Core/AudioRecorder.cs`
- Modify: `src/Core/TranscriptionService.cs`
- Modify: `src/Core/OutputHandler.cs`

- [x] **Step 1: Wire dependencies in `Program.cs`**

  Construct settings, history, error logger, audio recorder, hotkey manager, transcription service, output handler, sound feedback, tray icon, settings form factory, and history form factory.

- [x] **Step 2: Wire push-to-talk flow**

  Hotkey press starts recording and recording indicator. Hotkey release stops recording, switches tray state to transcribing, runs transcription, outputs final text, logs history, and returns to idle.

- [x] **Step 3: Wire toggle flow**

  First hotkey press starts recording. Second press stops and transcribes.

- [x] **Step 4: Wire VAD flow**

  Hotkey toggles VAD listening. Audio level state starts recording on speech and stops after configured silence.

- [x] **Step 5: Wire error notification**

  On failed transcription, show a tray balloon notification with a concise error reason and do not auto-type anything.

- [x] **Step 6: Wire cancellation and shutdown**

  Exit should unregister hotkeys, stop recording/listening, dispose tray icon, and cancel in-flight transcription cleanly.

- [x] **Step 7: Manual end-to-end test with clipboard output**

  Configure a provider, select Clipboard output, record a short phrase, and verify clipboard receives text.

- [x] **Step 8: Manual end-to-end test with popup output**

  Switch to Popup output, record a short phrase, and verify the review popup can copy/dismiss text.

- [x] **Step 9: Manual end-to-end test with auto-type output**

  Open Notepad, switch to Auto-type output, record a short phrase, and verify text appears at the cursor.

- [x] **Step 10: Commit end-to-end wiring**

  Run:

  ```powershell
  git add src
  git commit -m "feat: wire recording transcription flow"
  ```

---

## Task 15: Add Sample Settings and README

**Files:**
- Create: `sample.settings.json`
- Create: `README.md`

- [x] **Step 1: Create sample settings file**

  Add the schema from `spec.md` with placeholder provider values, quick languages, audio defaults, and post-processor defaults.

- [x] **Step 2: Document setup**

  In `README.md`, document prerequisites, restore/build/test commands, first launch behavior, and where settings/history/error logs live.

- [x] **Step 3: Document provider configuration**

  Add concise setup notes for Azure OpenAI, OpenAI, OpenRouter, local Whisper-compatible servers, and custom endpoints.

- [x] **Step 4: Document post-processing presets**

  Explain cleanup, homogenize-to-English, homogenize-to-Indonesian, translate-to-language, and custom prompt modes.

- [x] **Step 5: Document publish command**

  Include:

  ```powershell
  dotnet publish src/Bisik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  ```

- [x] **Step 6: Commit docs**

  Run:

  ```powershell
  git add README.md sample.settings.json
  git commit -m "docs: add setup and sample settings"
  ```

---

## Task 16: Final Verification and Release Build

**Files:**
- Modify only if verification exposes defects.

- [ ] **Step 1: Run full test suite**

  Run:

  ```powershell
  dotnet test Bisik.sln
  ```

  Expected: all tests pass.

- [ ] **Step 2: Run release build**

  Run:

  ```powershell
  dotnet build Bisik.sln -c Release
  ```

  Expected: release build succeeds.

- [ ] **Step 3: Publish single-file executable**

  Run:

  ```powershell
  dotnet publish src/Bisik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  ```

  Expected: single executable is produced under `src/bin/Release/net8.0-windows/win-x64/publish/`.

- [ ] **Step 4: Verify startup requirements**

  Launch the published executable and confirm the tray icon appears within 2 seconds and the app does not require admin rights.

- [ ] **Step 5: Verify idle footprint**

  Check Task Manager after the app has been idle for one minute. Expected: memory use is under 50 MB or documented with a clear reason if WinForms/NAudio overhead exceeds it.

- [ ] **Step 6: Verify no plaintext API keys**

  Save settings with API keys, inspect `%APPDATA%\Bisik\settings.json`, and confirm API key values are stored with `dpapi:v1:` encrypted values.

- [ ] **Step 7: Verify history schema**

  Complete a transcription with post-processing enabled and confirm `history.jsonl` stores raw text and final text separately.

- [ ] **Step 8: Verify out-of-scope items are not implemented**

  Confirm there is no macOS/Linux UI, no streaming transcription protocol, no speaker diarization, and no cloud sync.

- [ ] **Step 9: Commit verification fixes**

  If fixes were needed, run:

  ```powershell
  git add src tests README.md sample.settings.json
  git commit -m "fix: resolve release verification issues"
  ```

---

## Implementation Notes

- The spec repeats the `Models` and `Program.cs` entries in the architecture section; implement them once.
- Keep UI code thin. Business logic belongs in `Core`, `Providers`, `PostProcessor`, and `Models`.
- Do not log API keys, full auth headers, or full provider request payloads.
- Prefer small focused classes over a large tray-form object that owns every behavior.
- Use fake dependencies in tests for clipboard, keyboard input, HTTP, sound, and hotkey registration.
- Keep comments short and intentional, especially around DPAPI, hotkey P/Invoke, VAD decisions, and provider quirks.
