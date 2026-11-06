[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DefinitionPath,
    [Parameter(Mandatory)][string]$FixturePath,
    [Parameter(Mandatory)][string]$EncryptedSettingsPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^[a-zA-Z0-9_-]{1,24}$')][string]$Label = 'evaluation',
    [ValidateRange(1,5)][int]$Repetitions = 1,
    [string]$PrivateQualityDirectory = '',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$definition = (Resolve-Path -LiteralPath $DefinitionPath).Path
$fixture = (Resolve-Path -LiteralPath $FixturePath).Path
$settingsSource = (Resolve-Path -LiteralPath $EncryptedSettingsPath).Path
foreach ($source in @($definition, $fixture, $settingsSource)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Each source must be an existing file.' }
}
$summaryRoot = [IO.Path]::GetFullPath($OutputDirectory)
$summaryPath = Join-Path $summaryRoot 'summary.json'
if ((Test-Path -LiteralPath $summaryRoot) -and @(Get-ChildItem -LiteralPath $summaryRoot -Force).Count -gt 0) {
    throw 'Use a fresh summary directory so stale or private files cannot be published.'
}
$cliArgs = @('run', '--no-build', '--project', (Join-Path $repoRoot 'tools/Bisik.Diagnostics'),
    '-p:BisikRegisterLatestBuild=false', '--', '--experiment', '--definition', $definition, '--fixture', $fixture,
    '--settings', $settingsSource, '--output', $summaryPath, '--label', $Label, '--repeat', "$Repetitions")
if ($PrivateQualityDirectory) {
    $qualityRoot = [IO.Path]::GetFullPath($PrivateQualityDirectory)
    if ($qualityRoot.Equals($summaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $qualityRoot.StartsWith($summaryRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Content artifacts must be outside the published summary directory.'
    }
    $qualityPath = Join-Path $qualityRoot 'quality.json'
    $cliArgs += @('--quality', $qualityPath)
}
foreach ($output in @($summaryPath, $qualityPath)) {
    if ($output -and @($definition, $fixture, $settingsSource) -contains $output) { throw 'Outputs must not overwrite sources.' }
}
if ($ValidateOnly) {
    [pscustomobject]@{ Executable = 'dotnet'; Arguments = $cliArgs; PublishedSummary = $summaryPath; PrivateQuality = $qualityPath } | ConvertTo-Json -Depth 4
    return
}
[IO.Directory]::CreateDirectory($summaryRoot) | Out-Null
if ($PrivateQualityDirectory) { [IO.Directory]::CreateDirectory($qualityRoot) | Out-Null }
# The CLI decrypts only in memory and creates its own agent-owned DPAPI scratch copy.
# This template requires an already provisioned source decryptable by this Windows agent account.
Push-Location -LiteralPath $repoRoot
try {
    & dotnet @cliArgs
    if ($LASTEXITCODE -ne 0) { throw 'Experiment evaluation failed or had unavailable access/scores; inspect the content-free summary.' }
} finally { Pop-Location }
