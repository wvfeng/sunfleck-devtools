$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run = Join-Path $root ('out/windows-dev/check-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))
New-Item -ItemType Directory -Path (Join-Path $run 'profile/User') -Force | Out-Null
foreach ($file in @('Program.cs', 'counter.fleck')) {
    Copy-Item -LiteralPath (Join-Path $root "examples/simple/csharp/fleck/$file") -Destination $run
}
$binding = [Security.SecurityElement]::Escape((Join-Path $root 'out/windows-dev/app/Sunfleck.dll'))
$native = [Security.SecurityElement]::Escape((Join-Path $root 'out/windows-dev/native/sunfleck_native.dll'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>WinExe</OutputType><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><DebugType>embedded</DebugType><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs"/><Reference Include="Sunfleck"><HintPath>$binding</HintPath></Reference><None Include="$native" Link="sunfleck_native.dll" CopyToOutputDirectory="PreserveNewest"/><None Include="counter.fleck" Watch="false"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $run 'Workflow.csproj') -Encoding utf8
@{
    'security.workspace.trust.enabled' = $false
    'telemetry.telemetryLevel' = 'off'
    'update.mode' = 'none'
    'workbench.startupEditor' = 'none'
    'sunfleck.serverPath' = (Join-Path $root 'out/windows-dev/service/Sunfleck.Development.dll')
    'sunfleck.modelFiles' = @((Join-Path $root 'out/windows-dev/app/CounterFleck.fleckmodel.json'))
    '[fleck]' = @{ 'editor.defaultFormatter' = 'sunfleck.sunfleck'; 'editor.formatOnSave' = $true; 'editor.tabSize' = 2; 'editor.insertSpaces' = $true }
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'profile/User/settings.json') -Encoding utf8
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:LOCALAPPDATA 'Programs/Microsoft VS Code/Code.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment.Remove('ELECTRON_RUN_AS_NODE') | Out-Null
$start.Environment['SUNFLECK_WINDOWS_ROOT'] = $root
$start.Environment['SUNFLECK_WINDOWS_CHECK'] = $run
foreach ($argument in @('--new-window','--user-data-dir',(Join-Path $run 'profile'), '--extensions-dir',(Join-Path $env:USERPROFILE '.vscode/extensions'),
    '--shared-data-dir',(Join-Path $run 'shared-data'), '--skip-welcome','--skip-release-notes','--disable-workspace-trust',
    ('--extensionDevelopmentPath=' + (Join-Path $root 'editors/vscode')),
    ('--extensionDevelopmentPath=' + (Join-Path $root 'tests/development/vscode-host')),
    ('--extensionTestsPath=' + (Join-Path $root 'tests/development/vscode-host/windows-workflow.js')), $run)) { $start.ArgumentList.Add($argument) }
Write-Output "Windows workflow validation: $run"
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
if (!$process.WaitForExit(240000)) { $process.Kill($true); $process.WaitForExit(); throw "Workflow timed out: $run" }
$stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'host.stdout.log') -Encoding utf8
$stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'host.stderr.log') -Encoding utf8
$result = Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or $result.errors.Count) { throw ($result | ConvertTo-Json -Depth 5) }
$result | ConvertTo-Json -Depth 5
$process.Dispose()
