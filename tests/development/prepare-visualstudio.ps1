param([string]$Package = 'editors/visualstudio/bin/Debug/net472/Sunfleck.VisualStudio.vsix', [switch]$SkipConfiguration, [switch]$DiscoverModels)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$installation = (& $vswhere -latest -products '*' -format json | ConvertFrom-Json)[0]
if (Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object CommandLine -Match '/RootSuffix\s+SunfleckAcceptance(?:\s|$)') {
    throw 'Close the isolated SunfleckAcceptance host before replacing its extension.'
}
$profile = Join-Path $env:LOCALAPPDATA ("Microsoft/VisualStudio/18.0_" + $installation.instanceId + 'SunfleckAcceptance')
$extension = Join-Path $profile 'Extensions/Sunfleck.Current'
$existing = Get-ChildItem "$profile/Extensions" -Filter extension.vsixmanifest -Recurse -ErrorAction SilentlyContinue |
    Where-Object { [IO.File]::ReadAllText($_.FullName) -match 'Id="Sunfleck.Fleck.LanguageService"' }
if (@($existing).Count -gt 1) { throw 'Multiple Sunfleck acceptance extensions; choose one before updating.' }
if ($existing) { $extension = $existing.DirectoryName }
$output = Join-Path $root 'out/devtools/ide-acceptance'
New-Item -ItemType Directory -Force -Path $output,$extension | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath((Join-Path $root $Package)))
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $destination = [IO.Path]::GetFullPath((Join-Path $extension $entry.FullName))
        if (!$destination.StartsWith($extension + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$destination,$true)
    }
} finally { $archive.Dispose() }
Get-FileHash (Join-Path $root $Package),"$extension/Sunfleck.VisualStudio.dll","$extension/server/Sunfleck.Development.dll" |
    Select-Object Path,Hash | ConvertTo-Json | Set-Content "$output/artifacts.json"
$exe = Join-Path $installation.installationPath 'Common7/IDE/devenv.exe'
if (!$SkipConfiguration) {
    $update = Start-Process $exe -ArgumentList '/RootSuffix SunfleckAcceptance /UpdateConfiguration' -WindowStyle Hidden -PassThru
    $until = [DateTime]::UtcNow.AddSeconds(180)
    while (!$update.WaitForExit(1000)) { if ([DateTime]::UtcNow -gt $until) { throw 'Acceptance configuration update is still running.' } }
    if ($update.ExitCode) { throw "Acceptance configuration update failed: $($update.ExitCode)" }
}
$page = @"
<Window xmlns='urn:sunfleck:v1' Model='CCounter'>
  <StackPanel>
    <TextBlock Text='{Binding Missing}'/>
    <TextBlock Text='{Binding Counter.Value}'/>
  </StackPanel>
</Window>
"@
[IO.File]::WriteAllText("$output/acceptance.fleck", ($page -replace '\r?\n', "`r`n"))
$env:SUNFLECK_MODEL_FILES = "$root/out/devtools/c.fleckmodel.json;$root/out/devtools/cpp.fleckmodel.json;$root/out/devtools/examples/simple/csharp/fleck/CounterFleck.fleckmodel.json"
if ($DiscoverModels) { Remove-Item Env:SUNFLECK_MODEL_FILES -ErrorAction SilentlyContinue }
$env:SUNFLECK_DEBUG_PIPE = 'sunfleck-vs-acceptance'
$process = Start-Process $exe -ArgumentList "/RootSuffix SunfleckAcceptance /Log `"$output/ActivityLog.xml`" /Edit `"$output/acceptance.fleck`"" -WindowStyle Hidden -PassThru
Copy-Item "$output/acceptance.fleck" "$output/vs-$($process.Id).fleck"
@{ProcessId=$process.Id; Extension=$extension; Executable=$exe; Output=$output} | ConvertTo-Json | Set-Content "$output/host.json"
Get-Content "$output/host.json"
