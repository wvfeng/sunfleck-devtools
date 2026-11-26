param(
    [Parameter(Mandatory = $true)][string]$Project,
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $root ('out/devtools/csharp-navigation-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
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
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment.Remove('ELECTRON_RUN_AS_NODE') | Out-Null
$start.Environment['SUNFLECK_TEST_OUTPUT'] = $output
$start.Environment['SUNFLECK_TEST_CSHARP_PROJECT'] = [IO.Path]::GetFullPath($Project)
foreach ($argument in @('--new-window', '--user-data-dir', $profile,
    '--extensions-dir', "$env:USERPROFILE/.vscode/extensions",
    '--shared-data-dir', (Join-Path $output 'shared-data'),
    ('--extensionDevelopmentPath=' + (Join-Path $PSScriptRoot 'vscode-host')),
    ('--extensionTestsPath=' + (Join-Path $PSScriptRoot 'vscode-host/csharp-sdk-navigation.js')),
    '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust', '--disable-updates',
    [IO.Path]::GetFullPath($Project))) { $start.ArgumentList.Add($argument) }
Write-Output "C# editor evidence: $output"
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
$stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath "$output/stdout.log"
$stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath "$output/stderr.log"
if ($timedOut -or $process.ExitCode -ne 0) { throw "C# SDK navigation failed; see $output" }
Get-Content -LiteralPath "$output/csharp-navigation.json" -Raw
