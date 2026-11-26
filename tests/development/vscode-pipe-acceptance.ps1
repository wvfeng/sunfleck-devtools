param([int]$TimeoutSeconds = 120, [string]$ExtensionPath,
    [ValidateSet('debug-pipe', 'debug-lifecycle', 'extension-lifecycle', 'multi-instance', 'native-pipe', 'workspace-pipe')][string]$Suite = 'debug-pipe',
    [ValidateSet('integratedTerminal', 'internalConsole')][string]$Console = 'integratedTerminal',
    [ValidateRange(2, 10)][int]$Generations = 2,
    [string]$InstalledPackage,
    [switch]$DisableCycle)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$ExtensionPath) { $ExtensionPath = Join-Path $root 'editors/vscode' }
$output = Join-Path $root ('out/windows-dev/' + $Suite + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
Write-Output "VS Code validation: $output"
$workspace = Join-Path $output 'workspace'
$profile = Join-Path $output 'profile'
New-Item -ItemType Directory -Path "$workspace/.vscode", "$profile/User" -Force | Out-Null
$extensions = "$env:USERPROFILE/.vscode/extensions"
if ($InstalledPackage) {
    $extensions = Join-Path $output 'extensions'
    $ExtensionPath = Join-Path $extensions 'sunfleck.sunfleck'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($InstalledPackage))
    try {
        foreach ($entry in $archive.Entries) {
            if (!$entry.FullName.StartsWith('extension/') -or $entry.FullName.EndsWith('/')) { continue }
            $destination = [IO.Path]::GetFullPath((Join-Path $ExtensionPath $entry.FullName.Substring(10)))
            if (!$destination.StartsWith($ExtensionPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination)
        }
    } finally { $archive.Dispose() }
}
if ($DisableCycle -and (!$InstalledPackage -or $Suite -ne 'extension-lifecycle')) { throw 'DisableCycle requires an isolated InstalledPackage and extension-lifecycle suite.' }
$artifactPaths = @(
    (Join-Path $ExtensionPath 'extension.js'), (Join-Path $ExtensionPath 'inspector.js'),
    (Join-Path $root 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Counter.dll'),
    (Join-Path $root 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Sunfleck.dll'),
    (Join-Path $root 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/sunfleck_native.dll'),
    (Join-Path $PSScriptRoot "vscode-host/$Suite.js"))
if ($Suite -eq 'native-pipe') {
    $artifactPaths += @('c','cpp') | ForEach-Object { Join-Path $root "out/devtools/examples/simple/$_/fleck/counter_$($_)_fleck.exe" }
}
if ($Suite -eq 'workspace-pipe') {
    $artifactPaths += Join-Path $root 'tests/development/fixtures/legacy-demos/counter-cpp/bin/Debug/CounterCpp.exe'
}
$artifactPaths | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } |
    Select-Object Path, Hash | ConvertTo-Json | Set-Content "$output/artifacts.json" -Encoding utf8
@{
    'sunfleck.debugPipe' = 'sunfleck-' + $Suite + '-' + $PID
    'sunfleck.serverPath' = $(if ($ExtensionPath -eq (Join-Path $root 'editors/vscode')) { Join-Path $root 'out/windows-dev/service/Sunfleck.Development.dll' } else { '' })
    '[fleck]' = @{ 'editor.defaultFormatter' = 'sunfleck.sunfleck' }
    'sunfleck.modelFiles' = @("$root/tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Counter.fleckmodel.json")
} | ConvertTo-Json | Set-Content "$workspace/.vscode/settings.json" -Encoding utf8
@{
    'security.workspace.trust.enabled' = $false
    'telemetry.telemetryLevel' = 'off'
    'update.mode' = 'none'
    'extensions.autoUpdate' = $false
    'workbench.startupEditor' = 'none'
    'window.restoreWindows' = 'none'
} | ConvertTo-Json | Set-Content "$profile/User/settings.json" -Encoding utf8
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = "$env:LOCALAPPDATA/Programs/Microsoft VS Code/Code.exe"
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment.Remove('ELECTRON_RUN_AS_NODE') | Out-Null
$start.Environment['SUNFLECK_TEST_OUTPUT'] = $output
$start.Environment['SUNFLECK_TEST_CONSOLE'] = $Console
$start.Environment['SUNFLECK_TEST_GENERATIONS'] = [string]$Generations
$start.Environment['SUNFLECK_DISABLE_CYCLE'] = if ($DisableCycle) { '1' } else { '0' }
$workspaceArgument = $workspace
if ($Suite -eq 'workspace-pipe') {
    $workspaceArgument = Join-Path $output 'CounterCpp.code-workspace'
    @{folders=@(@{path='workspace'})} | ConvertTo-Json -Depth 3 | Set-Content $workspaceArgument -Encoding utf8
    Copy-Item (Join-Path $root 'tests/development/fixtures/legacy-demos/counter-cpp/main.cpp') "$workspace/main.cpp"
}
if ($Suite -eq 'extension-lifecycle') { $start.Environment['SUNFLECK_RELOAD_TEST'] = '1' }
else { $start.Environment.Remove('SUNFLECK_RELOAD_TEST') | Out-Null }
foreach ($arg in @('--new-window', '--user-data-dir', $profile, '--extensions-dir', $extensions,
    '--shared-data-dir', "$output/shared", "--extensionDevelopmentPath=$PSScriptRoot/vscode-host",
    '--skip-welcome', '--skip-release-notes',
    '--disable-workspace-trust', $workspaceArgument)) { $start.ArgumentList.Add($arg) }
if (!$InstalledPackage) { $start.ArgumentList.Add("--extensionDevelopmentPath=$ExtensionPath") }
if ($Suite -ne 'extension-lifecycle') { $start.ArgumentList.Add("--extensionTestsPath=$PSScriptRoot/vscode-host/$Suite.js") }
$runStarted = [DateTime]::Now
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
try {
    $completed = $process.WaitForExit($TimeoutSeconds * 1000)
    if (!$completed) { $process.Kill($true); $process.WaitForExit() }
    $stdout.GetAwaiter().GetResult() | Set-Content "$output/stdout.log"
    $stderr.GetAwaiter().GetResult() | Set-Content "$output/stderr.log"
    if (!$completed) { throw "Pipe acceptance host timed out. See $output." }
    if (!(Test-Path -LiteralPath "$output/result.json")) {
        throw "Test host produced no result (exit $($process.ExitCode)). See $output/stdout.log and stderr.log."
    }
    Get-Content "$output/result.json"
    $result = Get-Content "$output/result.json" -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $result.errors.Count) { throw "Pipe acceptance failed: $($process.ExitCode). See $output/result.json." }
} finally {
    # The reload fixture keeps its application outside the extension-host tree.
    $statePath = Join-Path $output 'reload-state.json'
    if ($Suite -eq 'extension-lifecycle' -and (Test-Path -LiteralPath $statePath)) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $fixture = Get-Process -Id $state.appPid -ErrorAction SilentlyContinue
        $expectedPath = Join-Path $root 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Counter.exe'
        if ($fixture -and $fixture.Path -eq $expectedPath -and $fixture.StartTime -ge $runStarted) {
            Stop-Process -Id $fixture.Id
        }
    }
    $process.Dispose()
}
