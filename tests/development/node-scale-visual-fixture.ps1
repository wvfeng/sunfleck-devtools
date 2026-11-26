param(
    [string]$BuildDirectory = 'out/devtools-scrollbar',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build = (Resolve-Path (Join-Path $repository $BuildDirectory)).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $build ('external-' + (Get-Date -Format 'yyyyMMddTHHmmssfff'))
}
$run = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $run) { throw "Output directory already exists: $run" }
New-Item -ItemType Directory -Path $run | Out-Null
$runtime = Join-Path $run 'runtime'
New-Item -ItemType Directory -Path $runtime | Out-Null
foreach ($name in @('node_scale_test.exe', 'node_scale.fleck', 'node_scale.flkb')) {
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $runtime $name)
}

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $runtime 'node_scale_test.exe'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.ArgumentList.Add((Join-Path $runtime 'node_scale.fleck'))
$start.ArgumentList.Add((Join-Path $runtime 'node_scale.flkb'))
$start.ArgumentList.Add('--desktop')
$start.ArgumentList.Add($run)
$files = @('node_scale_test.exe', 'node_scale.fleck', 'node_scale.flkb') | ForEach-Object {
    Get-FileHash -LiteralPath (Join-Path $runtime $_) -Algorithm SHA256 | Select-Object Path, Hash
}
$sources = @('src/app.cpp', 'src/ui_tree.cpp', 'tests/node_scale.cpp') | ForEach-Object {
    $copy = Join-Path $run $_
    New-Item -ItemType Directory -Path (Split-Path $copy -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $_) -Destination $copy
    Get-FileHash -LiteralPath $copy -Algorithm SHA256 | Select-Object Path, Hash
}
$testProcess = [Diagnostics.Process]::Start($start)
@{
    ProcessId = $testProcess.Id
    Executable = $start.FileName
    Run = $run
    Input = 'external'
    Files = $files
    Sources = $sources
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'fixture.json')
Write-Output "Run=$run PID=$($testProcess.Id)"
$testProcess.WaitForExit()
@{
    ProcessId = $testProcess.Id
    Exited = $true
    ExitCode = $testProcess.ExitCode
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'exit.json')
Write-Output "ExitCode=$($testProcess.ExitCode)"
exit $testProcess.ExitCode
