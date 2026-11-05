param([string]$OutputPath = (Join-Path $PSScriptRoot 'synthetic-speech.wav'), [ValidateSet('default', 'short', 'medium', 'long')][string]$Fixture = 'default')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    # Generates a fixed, harmless fixture without recording or playing microphone audio.
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synthesizer.SetOutputToWaveFile([System.IO.Path]::GetFullPath($OutputPath), $format)
    $text = switch ($Fixture) {
        'short' { 'Do not restart the pump.' }
        'medium' { 'Do not restart ABB cpmPlus History. Keep the Vtrin configuration unchanged. The pressure is fifteen bar, not fifty bar. This is the final sentence.' }
        'long' { ('Do not restart the pump. Keep the existing configuration unchanged. The pressure is fifteen bar, not fifty bar. ' * 4) + 'This is the final sentence.' }
        default { 'Bisik dictation verification. Hello world. This is the final sentence.' }
    }
    $synthesizer.Speak($text)
    # Fixed synthetic text only; timing reports remain content-free and quality gets a separate receipt.
    $fixtureManifest = @{ Synthetic = $true; Language = 'en'; Fixture = $Fixture; Text = $text } | ConvertTo-Json
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath + '.fixture.json'), $fixtureManifest, [Text.UTF8Encoding]::new($false))
}
finally {
    $synthesizer.Dispose()
}
