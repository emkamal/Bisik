param(
    [Parameter(Mandatory)][string]$LauncherExecutable,
    [Parameter(Mandatory)][string]$FallbackExecutable,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Bisik'),
    [string]$StateDirectory = (Join-Path $env:APPDATA 'Bisik')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Bisik.BuildSupport.ps1')
$installDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$stateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$installed = Join-Path $installDirectory 'Bisik.exe'
$backup = Join-Path $stateDirectory ('launcher-backups\' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
if (Test-Path -LiteralPath $installed) { Copy-Item -LiteralPath $installed -Destination (Join-Path $backup 'Bisik.exe') }
$settings = Join-Path $stateDirectory 'settings.json'
$settingsHash = $null
if (Test-Path -LiteralPath $settings) {
    $settingsHash = Get-BisikFileHash $settings
    Copy-Item -LiteralPath $settings -Destination (Join-Path $backup 'settings.json')
}
if (!(Test-Path -LiteralPath (Join-Path $stateDirectory 'latest-build.json'))) { throw 'Build and register Bisik before installing its launcher.' }
New-Item -ItemType Directory -Path (Join-Path $installDirectory 'fallback') -Force | Out-Null
$fallback = Join-Path $installDirectory 'fallback\Bisik.exe'
Copy-Item -LiteralPath $FallbackExecutable -Destination $fallback -Force
if ((Get-BisikFileHash $fallback) -ne (Get-BisikFileHash $FallbackExecutable)) { throw 'Fallback verification failed.' }
$temporary = Join-Path $installDirectory 'Bisik.launcher-new.exe'
Copy-Item -LiteralPath $LauncherExecutable -Destination $temporary -Force
$launcherHash = Get-BisikFileHash $LauncherExecutable
if ((Get-BisikFileHash $temporary) -ne $launcherHash) { throw 'Launcher verification failed.' }
# Keep the exact installed filename: the existing Start pin and startup entry continue to work.
try {
    if (Test-Path -LiteralPath $installed) { [IO.File]::Replace($temporary, $installed, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $installed) }
}
catch [IO.IOException] {
    throw 'Windows could not replace the installed executable. Exit Bisik from its tray menu and retry; its previous executable and backup remain intact.'
}
if ((Get-BisikFileHash $installed) -ne $launcherHash) { throw 'Installed launcher verification failed.' }
$registration = Join-Path $stateDirectory 'launcher-path.txt'
$temporaryRegistration = Join-Path $stateDirectory ([Guid]::NewGuid().ToString('N') + '.txt.tmp')
[IO.File]::WriteAllText($temporaryRegistration, $installed, [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $registration) { [IO.File]::Replace($temporaryRegistration, $registration, [System.Management.Automation.Language.NullString]::Value) }
else { [IO.File]::Move($temporaryRegistration, $registration) }
if ($settingsHash -and (Get-BisikFileHash $settings) -ne $settingsHash) { throw 'Settings changed during launcher installation.' }
$receipt = @{ InstalledPath = $installed; LauncherSha256 = $launcherHash; FallbackPath = $fallback;
    BackupPath = $backup; SettingsPreserved = $true; LatestBuildPointer = (Join-Path $stateDirectory 'latest-build.json') }
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'installation.json') -Encoding UTF8
$receipt | ConvertTo-Json
