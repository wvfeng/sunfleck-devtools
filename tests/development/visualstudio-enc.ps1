param([Parameter(Mandatory=$true)][int]$ProcessId,
    [ValidateSet('start','break','apply','continue','stop')][string]$Action='start')
$ErrorActionPreference='Stop'
Add-Type -Path "$PSScriptRoot/IdeAutomation.cs" -ReferencedAssemblies System.Drawing
$filter=[SunfleckIdeAutomation]::RetryBusyCalls()
$root=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$info=Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
if($info.Name -ne 'devenv.exe' -or $info.CommandLine -notmatch '/RootSuffix\s+SunfleckAcceptance(?:\s|$)'){throw 'Expected the isolated acceptance host'}
$dte=[SunfleckIdeAutomation]::GetDte($ProcessId)
$dir=Join-Path $root 'out/devtools/ide-acceptance/enc'
switch($Action) {
 'start' {
    $dte.Solution.Open("$dir/EncCounter.sln")
    $dte.Solution.SolutionBuild.ActiveConfiguration.Activate()
    $dte.ItemOperations.OpenFile("$dir/main.fleck").Activate()
    $dte.Debugger.Go($false)
 }
 'break' { $dte.Debugger.Break($false) }
 'apply' {
    if([int]$dte.Debugger.CurrentMode -ne 2){throw 'Break before applying a method-body edit'}
    $dte.ItemOperations.OpenFile("$dir/main.cpp").Activate()
    $text=[IO.File]::ReadAllText("$dir/main.cpp")
    if(!$text.Contains('return value + 1;')){throw 'Expected the original method body'}
    $dte.ActiveDocument.Selection.SelectAll()
    $dte.ActiveDocument.Selection.Insert($text.Replace('return value + 1;','return value + 5;'))
    $dte.ActiveDocument.Save()
    $dte.ExecuteCommand('Debug.ApplyCodeChanges')
 }
 'continue' { $dte.Debugger.Go($false) }
 'stop' { $dte.Debugger.Stop($false) }
}
@{action=$Action;mode=[int]$dte.Debugger.CurrentMode;processes=@($dte.Debugger.DebuggedProcesses | ForEach-Object {@{id=$_.ProcessID;name=$_.Name}})} |
 ConvertTo-Json -Depth 5 | Set-Content "$dir/$Action.json"
Get-Content "$dir/$Action.json"
