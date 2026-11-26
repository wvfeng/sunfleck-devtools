param([Parameter(Mandatory = $true)][string]$Package)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead([System.IO.Path]::GetFullPath($Package))
try {
    $manifest = $archive.GetEntry('extension.vsixmanifest')
    if ($null -eq $manifest) {
        throw 'VSIX manifest is missing.'
    }
    $reader = New-Object System.IO.StreamReader($manifest.Open())
    try {
        [xml]$xml = $reader.ReadToEnd()
    } finally {
        $reader.Dispose()
    }
    $assets = @($xml.PackageManifest.Assets.Asset)
    if ($assets.Count -eq 0) {
        throw 'VSIX contains no assets.'
    }
    foreach ($asset in $assets) {
        $assetPath = $asset.Path.Replace('\', '/')
        if ($assetPath.Contains('|') -or $assetPath.Contains('%') -or $null -eq $archive.GetEntry($assetPath)) {
            throw "VSIX asset cannot be resolved: $assetPath"
        }
    }
    foreach ($required in @('server/Sunfleck.Development.dll', 'server/Sunfleck.Development.runtimeconfig.json', 'server/Sunfleck.Development.deps.json', 'server/Sunfleck.dll', 'server/sunfleck_native.dll', 'server/Sunfleck.ComponentGenerator.dll', 'server/Microsoft.CodeAnalysis.dll', 'server/Microsoft.CodeAnalysis.CSharp.dll', 'server/types/Elements.cs', 'server/types/Components.cs')) {
        if ($null -eq $archive.GetEntry($required)) {
            throw "VSIX is missing $required"
        }
    }
    $options = $archive.GetEntry('Sunfleck.VisualStudio.pkgdef')
    if ($null -eq $options) {
        throw 'VSIX is missing the Visual Studio options registration.'
    }
    $reader = New-Object System.IO.StreamReader($options.Open())
    try {
        $registration = $reader.ReadToEnd()
        if (!$registration.Contains('ToolsOptionsPages\Sunfleck\Fleck') -or
            !$registration.Contains('Sunfleck.VisualStudio.FleckFormattingPackage')) {
            throw 'Fleck formatting package or options page is not registered.'
        }
    } finally {
        $reader.Dispose()
    }
    Write-Output 'VSIX asset paths and bundled server verified.'
} finally {
    $archive.Dispose()
}
