param([string]$DotnetSdkVersion, [switch]$ConsoleSubsystem, [switch]$RequireCs2012)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$directory = Join-Path $root ('out/devtools/pdb-lock/run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N'))
$workspace = Join-Path $directory 'workspace'
New-Item -ItemType Directory -Path $workspace | Out-Null
Write-Output "Console PDB reproduction: $directory"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'watch-fixture/pdb-lock-console/LockConsole.csproj'), (Join-Path $PSScriptRoot 'watch-fixture/pdb-lock-console/Program.cs') -Destination $workspace
if ($DotnetSdkVersion) {
    @{ sdk = @{ version = $DotnetSdkVersion; rollForward = 'disable' } } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $workspace 'global.json') -Encoding utf8
}
$configuration = @{ Source = $workspace; TracePdbLocks = $true }
$watcher = $null
$owned = @()
. (Join-Path $PSScriptRoot 'pdb-lock-probe.ps1')
$result = [ordered]@{ Reproduced = $false; ConsoleSubsystem = [bool]$ConsoleSubsystem }
$marker = Join-Path $directory 'application.pid'
$executable = Join-Path $workspace 'bin/Debug/net10.0/HotReloadCounter.exe'
try {
    Push-Location -LiteralPath $workspace
    try { $result['SDK'] = (& dotnet --version).Trim(); if ($LASTEXITCODE -ne 0) { throw 'Could not select SDK.' } }
    finally { Pop-Location }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -CommandType Application | Select-Object -First 1).Source)
    $start.WorkingDirectory = $workspace
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['SUNFLECK_PDB_REPRO_PID'] = $marker
    $start.Environment['DOTNET_CLI_UI_LANGUAGE'] = 'en-US'
    if ($ConsoleSubsystem) { $start.Environment['SunfleckPdbReproConsole'] = 'true' }
    foreach ($argument in @('watch', '--non-interactive', '--project', (Join-Path $workspace 'LockConsole.csproj'), 'run', '--no-launch-profile')) { $start.ArgumentList.Add($argument) }
    $watcher = [Diagnostics.Process]::Start($start)
    $stdout = $watcher.StandardOutput.ReadToEndAsync()
    $stderr = $watcher.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while (!(Test-Path -LiteralPath $marker)) {
        if ($watcher.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Console application did not start.' }
        Start-Sleep -Milliseconds 100
    }
    $application = Get-Process -Id ([int](Get-Content -LiteralPath $marker -Raw))
    if ($application.Path -ne $executable) { throw 'Unexpected console application path.' }
    $application.EnableRaisingEvents = $true
    $owned += $application
    $result['ApplicationProcessId'] = $application.Id
    Save-PdbLockTrace 'before-edit'
    $source = Join-Path $workspace 'Program.cs'
    (Get-Content -LiteralPath $source -Raw).Replace('Caption', 'RestartCaption') | Set-Content -LiteralPath $source -Encoding utf8
    if (!$application.WaitForExit(30000)) { throw 'Runtime rude edit did not exit the original console process.' }
    Save-PdbLockTrace 'old-app-exited'
    Save-PdbLockLifetime
    Start-Sleep -Seconds 5
    Save-PdbLockTrace 'old-app-exited-plus5s'
    $replacementId = [int](Get-Content -LiteralPath $marker -Raw)
    if ($replacementId -ne $application.Id) {
        $replacement = Get-Process -Id $replacementId -ErrorAction SilentlyContinue
        if ($replacement) {
            if ($replacement.Path -ne $executable) { throw 'Unexpected replacement console application path.' }
            $replacement.EnableRaisingEvents = $true
            $owned += $replacement
            $result['ReplacementProcessId'] = $replacement.Id
        }
    }
} catch { $result['Error'] = $_.Exception.ToString() }
finally {
    if ($watcher) {
        try { Save-PdbLockTrace 'before-cleanup' } catch { $result['TraceError'] = $_.Exception.ToString() }
        if (!$watcher.HasExited) { $watcher.Kill($true); $watcher.WaitForExit(10000) | Out-Null }
        try { Save-PdbLockTrace 'after-cleanup' } catch { $result['TraceError'] = $_.Exception.ToString() }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $directory 'dotnet-watch.stdout.log') -Encoding utf8
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $directory 'dotnet-watch.stderr.log') -Encoding utf8
        $result['WatcherProcessId'] = $watcher.Id
        $result['WatcherExited'] = $watcher.HasExited
    }
    $result['OwnedProcesses'] = @($owned | ForEach-Object { @{ ProcessId = $_.Id; HasExited = $_.HasExited; ExitCode = $_.ExitCode } })
}
if (!$result.Error) {
    $result.Reproduced = (Get-Content -LiteralPath (Join-Path $directory 'dotnet-watch.stdout.log') -Raw) -match 'error CS2012:'
    $exitTrace = Get-Content -LiteralPath (Join-Path $directory 'pdb-lock-old-app-exited.json') -Raw | ConvertFrom-Json
    $result['HeldAfterApplicationExit'] = @($exitTrace.Probe.Holders).Count -gt 0
    # Rebuild the exact same source after watcher exit: no process termination beyond our own tree.
    Push-Location -LiteralPath $workspace
    try {
        & dotnet build --no-restore --nologo "-p:SunfleckPdbReproConsole=$([bool]$ConsoleSubsystem)" 2>&1 | Set-Content -LiteralPath (Join-Path $directory 'recovery-build.log') -Encoding utf8
        $result['RecoveryBuildExitCode'] = $LASTEXITCODE
    } finally { Pop-Location }
    Save-PdbLockTrace 'recovery-build'
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'result.json') -Encoding utf8
foreach ($process in $owned) { $process.Dispose() }
if ($watcher) { $watcher.Dispose() }
if ($result.Error) { throw $result.Error }
if (($RequireCs2012 -and !$result.Reproduced) -or $result.RecoveryBuildExitCode -ne 0) { throw "Expected CS2012 or recovery build result missing. See $directory" }
Write-Output "CS2012=$($result.Reproduced); held after original application exit=$($result.HeldAfterApplicationExit); recovery build succeeded."
