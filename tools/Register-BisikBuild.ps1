param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [string]$AssemblyName = 'Bisik',
    [string]$Configuration = 'Debug',
    [ValidateSet('Build', 'Publish')][string]$Kind = 'Build',
    [string]$StateDirectory = (Join-Path $env:APPDATA 'Bisik')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Bisik.BuildSupport.ps1')
$sourceDirectory = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\', '/')
$stateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$snapshotRoot = Join-Path $stateDirectory 'builds'
if ($snapshotRoot.StartsWith($sourceDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The build output must not contain the launcher state directory.'
}
$entryName = $AssemblyName + '.exe'
if (!(Test-Path -LiteralPath (Join-Path $sourceDirectory $entryName))) { $entryName = $AssemblyName + '.dll' }
$entryPath = Join-Path $sourceDirectory $entryName
if (!(Test-Path -LiteralPath $entryPath)) { throw 'No completed Bisik executable was found.' }

$snapshot = Join-Path $snapshotRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
$criticalNames = @($entryName, ($AssemblyName + '.dll'), ($AssemblyName + '.deps.json'), ($AssemblyName + '.runtimeconfig.json')) | Select-Object -Unique
$sourceHashes = @{}
foreach ($name in $criticalNames) {
    $path = Join-Path $sourceDirectory $name
    if (Test-Path -LiteralPath $path) { $sourceHashes[$name] = Get-BisikFileHash $path }
}
Get-ChildItem -LiteralPath $sourceDirectory -Force | ForEach-Object {
    # SDK output folders can contain other RID/publish builds; they are not runtime dependencies.
    if (!$_.PSIsContainer -or (!(Test-Path -LiteralPath (Join-Path $_.FullName ($AssemblyName + '.dll'))) -and
        !(Test-Path -LiteralPath (Join-Path $_.FullName ($AssemblyName + '.exe'))))) {
        Copy-Item -LiteralPath $_.FullName -Destination $snapshot -Recurse -Force
    }
}
$snapshotEntry = Join-Path $snapshot $entryName
foreach ($name in $sourceHashes.Keys) {
    if ((Get-BisikFileHash (Join-Path $snapshot $name)) -ne $sourceHashes[$name] -or
        (Get-BisikFileHash (Join-Path $sourceDirectory $name)) -ne $sourceHashes[$name]) {
        throw 'Build output changed while the launcher snapshot was being copied.'
    }
}
[IO.File]::WriteAllText((Join-Path $snapshot '.bisik-build'), $Kind)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$gate = [Threading.Mutex]::new($false, ('Local\Bisik.LatestBuild.' + $identity.User.Value))
$acquired = $false
try {
    try { $acquired = $gate.WaitOne([TimeSpan]::FromSeconds(30)) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Another build is still registering its launch target.' }
    $receipt = @{ ExecutablePath = $snapshotEntry; Configuration = $Configuration; Kind = $Kind;
        BuildTimeUtc = [DateTimeOffset]::UtcNow.ToString('O'); SourceDirectory = $sourceDirectory }
    $pointer = Join-Path $stateDirectory 'latest-build.json'
    $temporaryPointer = Join-Path $stateDirectory ([Guid]::NewGuid().ToString('N') + '.json.tmp')
    [IO.File]::WriteAllText($temporaryPointer, ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    # The launcher shares this mutex: it sees a whole build and obtains its lease before cleanup.
    if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($temporaryPointer, $pointer, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporaryPointer, $pointer) }

    $rootPrefix = [IO.Path]::GetFullPath($snapshotRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $snapshotRoot -Directory | Where-Object {
        $_.Name -match '^[a-f0-9]{32}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName '.bisik-build'))
    } | Sort-Object CreationTimeUtc -Descending | Select-Object -Skip 5 | ForEach-Object {
        $obsolete = [IO.Path]::GetFullPath($_.FullName)
        if (!$obsolete.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
        try { $lease = [IO.FileStream]::new((Join-Path $obsolete '.in-use.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] { return } # A running app still owns this snapshot.
        $lease.Dispose()
        Remove-Item -LiteralPath $obsolete -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Output ('Latest Bisik build: ' + $Configuration + ' / ' + $Kind)
}
finally {
    if ($acquired) { $gate.ReleaseMutex() }
    $gate.Dispose()
    $identity.Dispose()
}
