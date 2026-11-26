param(
    [Parameter(Mandatory = $true)][string]$Project,
    [int]$TimeoutSeconds = 240,
    [int]$HoverSeconds = 0
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $root ('out/devtools/native-launch-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
$profile = Join-Path $output 'profile'
New-Item -ItemType Directory -Path "$profile/User" -Force | Out-Null
@{
    'security.workspace.trust.enabled' = $false
    'telemetry.telemetryLevel' = 'off'
    'update.mode' = 'none'
    'extensions.autoUpdate' = $false
    'extensions.autoCheckUpdates' = $false
    'workbench.startupEditor' = 'none'
    'window.restoreWindows' = 'none'
} | ConvertTo-Json | Set-Content -LiteralPath "$profile/User/settings.json" -Encoding utf8
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = "$env:LOCALAPPDATA/Programs/Microsoft VS Code/Code.exe"
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
if ($HoverSeconds -gt 0) { $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal }
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment.Remove('ELECTRON_RUN_AS_NODE') | Out-Null
$start.Environment['SUNFLECK_TEST_OUTPUT'] = $output
$start.Environment['SUNFLECK_TEST_NATIVE_PROJECT'] = [IO.Path]::GetFullPath($Project)
$start.Environment['SUNFLECK_TEST_HOVER_SECONDS'] = [string]$HoverSeconds
foreach ($argument in @('--new-window', '--user-data-dir', $profile,
    '--extensions-dir', "$env:USERPROFILE/.vscode/extensions",
    '--shared-data-dir', (Join-Path $output 'shared-data'),
    ('--extensionDevelopmentPath=' + (Join-Path $PSScriptRoot 'vscode-host')),
    ('--extensionTestsPath=' + (Join-Path $PSScriptRoot 'vscode-host/native-demo-launch.js')),
    '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust', '--disable-updates',
    [IO.Path]::GetFullPath($Project))) { $start.ArgumentList.Add($argument) }
Write-Output "Native editor evidence: $output"
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
$stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath "$output/stdout.log"
$stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath "$output/stderr.log"
if ($timedOut -or $process.ExitCode -ne 0) { throw "Native demo launch failed; see $output" }
$result = Get-Content -LiteralPath "$output/native-launch.json" -Raw | ConvertFrom-Json
$result | Select-Object passed, tasks, inspector, errors | ConvertTo-Json -Depth 5

