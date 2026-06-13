# Bisik

Bisik is a lightweight Windows-first desktop transcription app that runs in the system tray, records microphone audio on a hotkey, sends it to a Whisper-compatible transcription endpoint, and outputs the result at the cursor.

## Prerequisites

- Windows 10 or Windows 11
- .NET 8 SDK
- A configured transcription provider before running real transcriptions

## Setup

Restore dependencies:

```powershell
dotnet restore
```

Build the app:

```powershell
dotnet build
```

Run the app:

```powershell
dotnet run --project src/Bisik.csproj
```

Run the test suite:

```powershell
dotnet test Bisik.sln
```

Publish a single self-contained executable:

```powershell
dotnet publish src/Bisik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## First Launch

On first launch, Bisik starts in the system tray without opening a main window. Open `Settings` from the tray menu to configure:

- provider connection details
- microphone and audio options
- output mode
- language options
- optional post-processing

`sample.settings.json` shows the expected settings shape with placeholder values only.

## App Data

Bisik stores local data under:

```text
%APPDATA%\Bisik\
```

Files used there:

- `settings.json` — persisted app settings
- `history.jsonl` — append-only transcription history
- `error.log` — exception logging

API keys are stored encrypted at rest through Windows DPAPI.

## Provider Configuration

Bisik supports multiple Whisper-compatible providers from the `Provider` tab in `Settings`.

### Azure OpenAI

Required fields:

- `Endpoint URL`
- `API Key`
- `Deployment Name`
- `API Version`

Request pattern:

```text
{endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version={apiVersion}
```

Use an `api-key` header and the deployment that hosts your transcription model.

### OpenAI

Required fields:

- `API Key`
- `Model`

Default model:

```text
whisper-1
```

Bisik uses the standard OpenAI transcription endpoint.

### OpenRouter

Required fields:

- `API Key`
- `Model`

Use an audio transcription-capable model that OpenRouter exposes through its OpenAI-compatible endpoint.

### Local Whisper

Common fields:

- `Base URL`
- `Model` (optional depending on server)
- `API Key` (optional)

Use this for local HTTP servers such as `whisper.cpp`, `faster-whisper`, or other OpenAI-compatible local transcription services.

### Custom Endpoint

Common fields:

- `Base URL`
- `API Key` (optional)
- `Model` (optional)
- `Extra Headers`

Use this for other OpenAI-compatible transcription providers that require custom header or endpoint settings.

## Post-Processing Presets

Bisik can optionally run a second LLM pass after transcription. This is configured in the `Post-Processor` tab.

Available presets:

- `Clean up`
  - Fix grammar, punctuation, and filler words while preserving meaning.
- `Homogenize to English`
  - Normalize mixed-language transcription into clean English.
- `Homogenize to Indonesian`
  - Normalize mixed-language transcription into clean Indonesian.
- `Translate to language`
  - Translate the final text into the configured target language.
- `Custom`
  - Use your own system prompt directly.

When post-processing is enabled, Bisik stores both:

- the raw transcription
- the post-processed final text

in the local history log.

## Publish

To publish a standalone Windows executable:

```powershell
dotnet publish src/Bisik.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The published output is placed under the project `bin/Release` publish folder for the selected runtime.
