param(
    [ValidateSet('Prepare', 'Open', 'Build', 'Run', 'Watch')][string]$Action = 'Open',
    [string]$SdkRoot,
    [string]$SamplesRoot
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This development entry currently supports Windows only.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $root 'out/windows-dev'
if (!$SdkRoot) { $SdkRoot = Join-Path $root '../sdk/local/windows-x64' }
if (!$SamplesRoot) { $SamplesRoot = Join-Path $root '../samples' }
$SdkRoot = (Resolve-Path -LiteralPath $SdkRoot).Path
$project = (Resolve-Path -LiteralPath (Join-Path $SamplesRoot 'counter/csharp/fleck/CounterFleck.csproj')).Path
$page = Join-Path (Split-Path $project) 'counter.fleck'
$service = Join-Path $output 'service/Sunfleck.Development.dll'
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE." }
}
function Prepare-Development {
    Invoke-Checked dotnet @('build', (Join-Path $root 'tools/development/Sunfleck.Development.csproj'),
        "-p:SunfleckSdkRoot=$SdkRoot", '--nologo', '-o', (Join-Path $output 'service'))
    if ($Action -eq 'Open' -and !(Test-Path -LiteralPath (Join-Path $root 'editors/vscode/node_modules/vscode-languageclient/node.js'))) {
        Invoke-Checked npm.cmd @('ci', '--ignore-scripts', '--prefix', (Join-Path $root 'editors/vscode'))
    }
}
$saved = @{}
foreach ($name in @('SunfleckSdkRoot', 'SunfleckDevelopmentTool', 'SunfleckDevelopmentTargets', 'SUNFLECK_DEBUG_PIPE')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location -LiteralPath $root
try {
    if ($Action -in @('Prepare', 'Open') -or !(Test-Path -LiteralPath $service)) { Prepare-Development }
    $env:SunfleckSdkRoot = $SdkRoot
    $env:SunfleckDevelopmentTool = $service
    $env:SunfleckDevelopmentTargets = Join-Path $root 'build/Sunfleck.Development.targets'
    $env:SUNFLECK_DEBUG_PIPE = 'sunfleck-example'
    Invoke-Checked dotnet @('build', $project, '--nologo', '-o', (Join-Path $output 'app'))
    if ($Action -eq 'Open') {
        $code = Join-Path $env:LOCALAPPDATA 'Programs/Microsoft VS Code/Code.exe'
        if (!(Test-Path -LiteralPath $code)) { throw 'VS Code was not found. Open Sunfleck.Windows.code-workspace with the Sunfleck extension loaded.' }
        # Load the current extension without an installer or changing global settings.
        $start = [Diagnostics.ProcessStartInfo]::new($code)
        $start.UseShellExecute = $false
        $start.Environment.Remove('ELECTRON_RUN_AS_NODE') | Out-Null
        foreach ($argument in @('--new-window', ('--extensionDevelopmentPath=' + (Join-Path $root 'editors/vscode')),
            (Join-Path $root 'Sunfleck.Windows.code-workspace'), $page)) { $start.ArgumentList.Add($argument) }
        [Diagnostics.Process]::Start($start) | Out-Null
    } elseif ($Action -eq 'Run') {
        Invoke-Checked dotnet @((Join-Path $output 'app/CounterFleck.dll'), '--watch-fleck', $page,
            '--live-model-file', (Join-Path $output 'app/CounterFleck.fleckmodel.json'))
    } elseif ($Action -eq 'Watch') {
        Write-Host 'Fleck: save to update the running window. C#: compatible edits use Hot Reload. This optional watch profile uses embedded symbols; F5 keeps portable symbols.'
        Invoke-Checked dotnet @('watch', '--project', $project, 'run', '--no-launch-profile', '-p:DebugType=embedded', '--', '--watch-fleck', $page,
            '--live-model-file', (Join-Path $output 'app/CounterFleck.fleckmodel.json'))
    }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    Pop-Location
}
