param([Parameter(Mandatory=$true)][int]$ProcessId, [switch]$VerifySavedSettings)
$ErrorActionPreference='Stop'
Add-Type -Path "$PSScriptRoot/IdeAutomation.cs" -ReferencedAssemblies System.Drawing
$filter=[SunfleckIdeAutomation]::RetryBusyCalls()
$root=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$output=Join-Path $root 'out/devtools/ide-acceptance'
$info=Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
if($info.Name -ne 'devenv.exe' -or $info.CommandLine -notmatch '/RootSuffix\s+SunfleckAcceptance(?:\s|$)'){throw 'Expected the isolated acceptance host'}
$dte=[SunfleckIdeAutomation]::GetDte($ProcessId)
$dte.MainWindow.Visible=$true
$result=@{passed=@();errors=@();processId=$ProcessId}
$dir=Join-Path $output 'enc'
try {
    $settings=$dte.Properties('Sunfleck','Fleck')
    if ($VerifySavedSettings) {
        if(!$settings.Item('FormatOnSave').Value){throw 'FormatOnSave did not survive the host restart'}
        $result.passed+='format-on-save-persisted-after-restart'
    }
    $settings.Item('ModelFiles').Value=''
    foreach($config in @('Debug','Release')) {
        New-Item -ItemType Directory -Force "$dir/$config" | Out-Null
        [IO.File]::WriteAllText("$dir/$config/model.h","// $config definition`r`nint Value;")
        @{version=1;models=@(@{version=1;id='1';name='ConfigModel';properties=@(@{path='Value';type='integer';writable=$true;source=@{uri="$dir/$config/model.h";line=2;column=1}})})} |
            ConvertTo-Json -Depth 10 | Set-Content "$dir/$config/config.fleckmodel.json" -Encoding UTF8
    }
    [IO.File]::WriteAllText("$dir/config.fleck", "<Window xmlns='urn:sunfleck:v1' Model='ConfigModel'><TextBlock Text='{Binding Value}'/></Window>")
    $dte.Solution.Open("$dir/EncCounter.sln")
    foreach($config in @('Debug','Release','Debug')) {
        $configuration=@($dte.Solution.SolutionBuild.SolutionConfigurations | Where-Object Name -eq $config)[0]
        if (!$configuration) { throw "Missing $config solution configuration" }
        $configuration.Activate()
        Start-Sleep -Seconds 4
        $window=$dte.ItemOperations.OpenFile("$dir/config.fleck");$window.Activate()
        $line=[IO.File]::ReadAllText("$dir/config.fleck")
        $dte.ActiveDocument.Selection.MoveToLineAndOffset(1,$line.IndexOf('Value}')+2)
        $dte.ExecuteCommand('Edit.GoToDefinition')
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while([DateTime]::UtcNow -lt $deadline -and $dte.ActiveDocument.FullName -ne "$dir\$config\model.h") {Start-Sleep -Milliseconds 200}
        if($dte.ActiveDocument.FullName -ne "$dir\$config\model.h"){throw "Expected $config model definition, got $($dte.ActiveDocument.FullName)"}
        $result.passed+="$config-active-project-definition"
    }
    $window=$dte.ItemOperations.OpenFile("$dir/config.fleck");$window.Activate()
    [SunfleckIdeAutomation]::Capture($ProcessId,"$output/visualstudio-configuration.png")
    $alternate=Join-Path $output 'alternate-model-project'
    New-Item -ItemType Directory -Force $alternate|Out-Null
    Copy-Item "$dir/EncCounter.sln","$dir/EncCounter.vcxproj","$dir/config.fleck","$dir/main.cpp","$dir/main.fleck" $alternate
    [IO.File]::WriteAllText("$alternate/model.h","// alternate project`r`nint Value;")
    @{version=1;models=@(@{version=1;id='1';name='ConfigModel';properties=@(@{path='Value';type='integer';writable=$true;source=@{uri="$alternate/model.h";line=2;column=1}})})} |
        ConvertTo-Json -Depth 10|Set-Content "$alternate/model.fleckmodel.json" -Encoding UTF8
    $dte.Solution.Open("$alternate/EncCounter.sln")
    Start-Sleep -Seconds 4
    $dte.ItemOperations.OpenFile("$alternate/config.fleck").Activate()
    $ready=[DateTime]::UtcNow.AddSeconds(20)
    while (!$dte.Commands.Item('Edit.GoToDefinition').IsAvailable -and [DateTime]::UtcNow -lt $ready) {Start-Sleep -Milliseconds 200}
    $line=[IO.File]::ReadAllText("$alternate/config.fleck")
    $dte.ActiveDocument.Selection.MoveToLineAndOffset(1,$line.IndexOf('Value}')+2)
    $dte.ExecuteCommand('Edit.GoToDefinition')
    Start-Sleep -Seconds 2
    if($dte.ActiveDocument.FullName -ne "$alternate\model.h"){throw 'Project switch retained the previous model source'}
    $result.passed+='solution-project-switch-definition'
    $dte.Solution.Open("$dir/EncCounter.sln")
} catch {$result.errors+=$_.ToString();throw} finally {
    $result|ConvertTo-Json -Depth 6|Set-Content "$output/configuration-result.json"
}
Get-Content "$output/configuration-result.json"
