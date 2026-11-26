param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$Output
)
$ErrorActionPreference = 'Stop'
Add-Type -Path "$PSScriptRoot/IdeAutomation.cs" -ReferencedAssemblies System.Drawing
[SunfleckIdeAutomation]::Capture($ProcessId, [System.IO.Path]::GetFullPath($Output))
