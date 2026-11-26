param([Parameter(Mandatory=$true)][int]$ProcessId,
    [ValidateSet('c','cpp','csharp','csharp-xml','stop')][string]$Language='c')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$output=Join-Path $root 'out/devtools/ide-acceptance'
Add-Type -Path "$PSScriptRoot/IdeAutomation.cs" -ReferencedAssemblies System.Drawing
$filter=[SunfleckIdeAutomation]::RetryBusyCalls()
$info=Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
if($info.Name -ne 'devenv.exe' -or $info.CommandLine -notmatch '/RootSuffix\s+SunfleckAcceptance(?:\s|$)'){throw 'Expected the isolated acceptance host'}
$dte=[SunfleckIdeAutomation]::GetDte($ProcessId)
$state="$output/runtime-process.json"
if(Test-Path $state) {
    $previous=Get-Content $state -Raw|ConvertFrom-Json
    $process=Get-Process -Id $previous.id -ErrorAction SilentlyContinue
    if($process -and $process.Path -eq $previous.path -and $process.StartTime.ToUniversalTime().ToString('o') -eq $previous.started) {
        $process.CloseMainWindow()|Out-Null
        if(!$process.WaitForExit(2000)){$process.Kill();$process.WaitForExit()}
    }
}
if($Language -eq 'stop'){exit 0}
if($Language -like 'csharp*') {
    $exe="$root/out/devtools/examples/simple/csharp/fleck/CounterFleck.exe"
} else {$exe="$root/out/devtools/examples/simple/$Language/fleck/counter_$($Language)_fleck.exe"}
$env:SUNFLECK_DEBUG_PIPE='sunfleck-vs-acceptance'
$arguments=@()
if($Language -eq 'csharp-xml') {$arguments=@('--watch-fleck',"`"$root/examples/simple/csharp/fleck/counter.fleck`"")}
$start=@{FilePath=$exe;WorkingDirectory=[IO.Path]::GetDirectoryName($exe);WindowStyle='Hidden';PassThru=$true}
if($arguments.Count){$start.ArgumentList=$arguments}
$process=Start-Process @start
@{id=$process.Id;path=$exe.Replace('/','\');started=$process.StartTime.ToUniversalTime().ToString('o')}|ConvertTo-Json|Set-Content $state
$snapshot=$null
$deadline=[DateTime]::UtcNow.AddSeconds(25)
while([DateTime]::UtcNow -lt $deadline) {
    $stream=[IO.Pipes.NamedPipeClientStream]::new('.',$env:SUNFLECK_DEBUG_PIPE,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $stream.Connect(200)
        $writer=[IO.StreamWriter]::new($stream);$writer.AutoFlush=$true
        $reader=[IO.StreamReader]::new($stream)
        $writer.WriteLine('{"method":"snapshot"}')
        $read=$reader.ReadLineAsync()
        if($read.Wait(1000)) {
            $candidate=($read.Result|ConvertFrom-Json).result
            if($candidate.processId -eq $process.Id){$snapshot=$candidate;break}
        }
    } catch {} finally {$stream.Dispose()}
    Start-Sleep -Milliseconds 150
}
if(!$snapshot){throw "No snapshot from $Language PID $($process.Id)"}
$snapshot|ConvertTo-Json -Depth 40|Set-Content "$output/runtime-$Language.json"
$node=@($snapshot.nodes|Where-Object {$_.type -eq 'Button' -and $_.source.uri})[0]
if(!$node){throw 'No mapped runtime button'}
$uri=[Uri]$node.source.uri
$file=if($uri.IsAbsoluteUri -and $uri.IsFile){$uri.LocalPath}else{$node.source.uri}
$window=$dte.ItemOperations.OpenFile($file)
if(!$window){throw 'Source editor is blocked by a host dialog; dismiss it without editing the source and rerun.'}
$window.Activate()
$dte.ActiveDocument.Selection.MoveToLineAndOffset($node.source.range.line,$node.source.range.column)
Start-Sleep -Seconds 2
[SunfleckIdeAutomation]::Capture($ProcessId,"$output/runtime-$Language.png")
@{language=$Language;processId=$process.Id;session=$snapshot.debugSession;source=$file}|ConvertTo-Json
