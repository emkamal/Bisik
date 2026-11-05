param([string]$OutputPath = (Join-Path $PSScriptRoot 'synthetic-speech.wav'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    # Generates a fixed, harmless fixture without recording or playing microphone audio.
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synthesizer.SetOutputToWaveFile([System.IO.Path]::GetFullPath($OutputPath), $format)
    $synthesizer.Speak('Bisik dictation verification. Hello world. This is the final sentence.')
}
finally {
    $synthesizer.Dispose()
}
