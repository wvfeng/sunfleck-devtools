param([Parameter(Mandatory)][string]$Run)
$ErrorActionPreference = 'Stop'
$Run = [IO.Path]::GetFullPath($Run)
$fixture = Get-Content -LiteralPath (Join-Path $Run 'fixture.json') -Raw | ConvertFrom-Json
$exit = Get-Content -LiteralPath (Join-Path $Run 'exit.json') -Raw | ConvertFrom-Json
if ($fixture.Input -ne 'external' -or !$exit.Exited -or $exit.ExitCode -ne 0 -or
    $exit.ProcessId -ne $fixture.ProcessId) { throw 'External input run did not exit successfully.' }
foreach ($file in @($fixture.Files) + @($fixture.Sources)) {
    if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $file.Hash) {
        throw "File changed: $($file.Path)"
    }
}
$log = Get-Content -LiteralPath (Join-Path $Run 'result.log')
if ($log[0] -ne 'input,external') { throw 'Expected external input, not the native message driver.' }
$observations = Get-Content -LiteralPath (Join-Path $Run 'ui-observations.json') -Raw | ConvertFrom-Json
foreach ($path in @('xml', 'binary', 'generated')) {
    foreach ($stage in @('initial', 'selected', 'left', 'wheel', 'final')) {
        if (@($observations | Where-Object label -eq "$path-$stage").Count -ne 1) {
            throw "Missing or duplicate screenshot stage: $path-$stage"
        }
    }
}
$screenshots = @($observations | ForEach-Object {
    $path = Join-Path $Run ($_.label + '.jpg')
    @{ Label = $_.label; File = [IO.Path]::GetFileName($path); SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
})
$modes = @()
for ($mode = 0; $mode -lt 3; ++$mode) {
    $snapshot = Get-Content -LiteralPath (Join-Path $Run "final-$mode.json") -Raw | ConvertFrom-Json
    $list = @($snapshot.nodes | Where-Object fleckId -eq 'list')
    if ($list.Count -ne 1 -or $list[0].value -ne 4095 -or
        @($snapshot.nodes | Where-Object parent -eq $list[0].id).Count -ne 4096) {
        throw "Unexpected final list for mode $mode."
    }
    $values = @($log | Where-Object { $_ -match "^$mode,selected," } | ForEach-Object { [int]($_ -split ',')[2] })
    if ($values.Count -lt 3 -or $values[0] -ne 1995 -or $values[1] -ne 1994 -or
        $values[-1] -ne 4095 -or "$mode,passed" -notin $log) { throw "Selection sequence failed for mode $mode." }
    $input = Import-Csv -LiteralPath (Join-Path $Run "input-$mode.csv")
    $left = @($input | Where-Object { $_.phase -eq 'after' -and $_.message -eq '257' -and $_.wparam -eq '37' -and $_.value -eq '1994' })
    $wheelChanged = $false
    $beforeWheel = $null
    foreach ($event in $input) {
        if ($event.message -ne '522') { continue }
        if ($event.phase -eq 'before') { $beforeWheel = [int]$event.offset }
        if ($event.phase -eq 'after' -and $null -ne $beforeWheel -and [int]$event.offset -gt $beforeWheel) {
            $wheelChanged = $true
        }
    }
    $close = @($input | Where-Object { $_.phase -eq 'after' -and $_.message -eq '16' })
    if (!$left.Count -or !$wheelChanged -or !$close.Count) { throw "Missing wheel/key/close evidence for mode $mode." }
    $modes += @{ Mode = $mode; Children = 4096; Selected = $values; LeftReceived = $true; WheelReceived = $true; Closed = $true }
}
$report = @{
    Passed = $true
    Scope = 'Local Windows software rendering; three loading paths with externally injected GUI input. Screenshots were reviewed during the run; this audit checks their identity and runtime evidence. No physical input, IME or other-platform claim.'
    Modes = $modes
    Files = @($fixture.Files) + @($fixture.Sources)
    Screenshots = $screenshots
    Exit = $exit
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Run 'visual-audit.json')
Write-Output "External input 3/3 passed; $($screenshots.Count) screenshots and $($report.Files.Count) files checked."
