param([Parameter(Mandatory = $true)][string]$Run, [string]$ServerDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$serverRoot = if ($ServerDirectory) { [IO.Path]::GetFullPath($ServerDirectory) } else { Join-Path $root 'out/devtools/tools/development' }
$runPath = [IO.Path]::GetFullPath($Run)
$directory = Join-Path $runPath 'unicode'
function Read-Evidence([string]$Name) { Get-Content -LiteralPath (Join-Path $directory $Name) -Raw | ConvertFrom-Json }
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Local-Path([string]$Uri) {
    $value = ([Uri]$Uri).LocalPath
    if ($IsWindows -and $value -match '^/[A-Za-z]:[/\\]') { $value = $value.Substring(1) }
    [IO.Path]::GetFullPath($value)
}
function Point($Actual, [int]$Line, [int]$Character) {
    $null -ne $Actual -and $Actual.line -eq $Line -and $Actual.character -eq $Character
}
function Span($Actual, [int]$Line, [int]$Start, [int]$Length) {
    (Point $Actual.start $Line $Start) -and (Point $Actual.end $Line ($Start + $Length))
}
$summary = Get-Content -LiteralPath (Join-Path $runPath 'summary.json') -Raw | ConvertFrom-Json
$fixture = Read-Evidence 'fixture.json'
$hostResult = Read-Evidence 'vscode-unicode-result.json'
$matrix = Read-Evidence 'unicode-matrix.json'
$protocol = @(Get-Content -LiteralPath (Join-Path $directory 'unicode-protocol.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
Check ($summary.Suites.Count -eq 1 -and $summary.Suites[0].Suite -eq 'unicode' -and $summary.Suites[0].Passed -and
    !$summary.Suites[0].TimedOut -and $summary.Suites[0].ExitCode -eq 0) 'Expected a passing standalone unicode suite.'
Check ($hostResult.errors.Count -eq 0 -and $hostResult.passed.Count -eq 7 -and $hostResult.workspaceTrusted -and
    $hostResult.extensionPath -eq $fixture.Installed) 'Host result or package identity differs.'
Check ($matrix.clientInstances -eq 1 -and $matrix.servicePid -gt 0 -and
    $matrix.clientModule -eq (Join-Path $fixture.Installed 'node_modules/vscode-languageclient/lib/node/main.js') -and
    $matrix.source -eq $fixture.Source -and $matrix.fleck -eq $fixture.Fleck) 'Actual installed client/source identity differs.'
Check ($protocol.Count -eq $matrix.protocolRecords -and @($protocol | Where-Object { $_.error -or $_.servicePid -ne $matrix.servicePid }).Count -eq 0) 'Protocol error or service PID changed.'
$settings = Get-Content -LiteralPath (Join-Path $directory 'profile/User/settings.json') -Raw | ConvertFrom-Json
Check (@($settings.'sunfleck.modelFiles').Count -eq 1 -and $settings.'sunfleck.modelFiles'[0] -eq $fixture.Metadata -and
    !$settings.'sunfleck.serverPath' -and $settings.'editor.wordBasedSuggestions' -eq 'off' -and
    $settings.'[fleck]'.'editor.autoIndent' -eq 'none' -and !$settings.'[fleck]'.'editor.formatOnSave' -and
    !$settings.'[fleck]'.'editor.formatOnType') 'Fixture isolation settings differ.'

$packageHash = Hash $summary.Package
Check ($packageHash -eq $summary.PackageSHA256) 'Package changed after acceptance.'
$payload = @()
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($summary.Package)
try {
    foreach ($relative in @('extension.js', 'inspector.js', 'language.js', 'package.json', 'server/Sunfleck.Development.dll', 'server/Sunfleck.dll', 'server/sunfleck_native.dll')) {
        $entry = $archive.GetEntry('extension/' + $relative)
        Check ($null -ne $entry) "Missing package payload: $relative"
        $stream = $entry.Open()
        try { $zipHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $source = if ($relative.StartsWith('server/')) { Join-Path $serverRoot $relative.Substring(7) } else { Join-Path $root ('editors/vscode/' + $relative) }
        Check ($zipHash -eq (Hash (Join-Path $fixture.Installed $relative)) -and $zipHash -eq (Hash $source)) "Package/installed/current payload differs: $relative"
        $payload += @{ File = $relative; SHA256 = $zipHash }
    }
} finally { $archive.Dispose() }

$sourceLines = [IO.File]::ReadAllLines($fixture.Source)
$metadata = Get-Content -LiteralPath $fixture.Metadata -Raw | ConvertFrom-Json
Check ($metadata.version -eq 1 -and $metadata.models.Count -eq 1 -and $metadata.models[0].name -eq 'UnicodeModel' -and
    $metadata.models[0].properties.Count -eq 3) 'Unexpected static model fixture.'
$properties = $metadata.models[0].properties
foreach ($index in 0..2) {
    $propertyName = @('Caption', 'ReadOnly', 'SyncCommand')[$index]
    $property = $properties[$index]
    $column = $sourceLines[$index + 1].IndexOf($propertyName)
    Check ($column -gt 0 -and $property.path -eq $propertyName -and $property.source.line -eq $index + 2 -and
        $property.source.column -eq $column + 1 -and (Local-Path $property.source.uri) -eq $fixture.Source -and
        $property.source.uri.Contains('%2520') -and $property.writable -eq ($index -eq 0)) "Static source position/path differs: $propertyName"
}
Check ($properties[0].type -eq 'string' -and $properties[1].type -eq 'string' -and $properties[2].type -eq 'command' -and
    $properties[2].commandModes.Count -eq 1 -and $properties[2].commandModes[0] -eq 'Sync') 'Static model capabilities differ.'
$declarationColumn = $sourceLines[1].IndexOf('Caption')
Check ($sourceLines[1].Contains('中文🌱') -and $fixture.Source.Contains('🌱 %20') -and $fixture.Fleck.Contains('🌱 %20')) 'Unicode source or literal percent path missing.'

$accepted = @()
foreach ($ending in @('lf', 'crlf')) {
    $eol = if ($ending -eq 'lf') { "`n" } else { "`r`n" }
    foreach ($variant in @(@{ Name = 'ascii'; Prefix = 'prefix' }, @{ Name = 'cjk'; Prefix = '中文前缀' }, @{ Name = 'astral'; Prefix = '中文🌱前缀' })) {
        $name = $variant.Name + '-' + $ending
        $record = Read-Evidence "$name.json"
        $tag = '<!--' + $variant.Prefix + '-->'
        $lines = @("<Window xmlns='urn:sunfleck:v1' Model='UnicodeModel'>", '  <StackPanel>',
            "    ${tag}<TextBlock Text='{Binding Cap}'/>", "    ${tag}<TextBox Text='{Binding ReadOnly}'/>",
            "    ${tag}<Button Command='{Binding SyncCommand}' CommandMode='Async'/>",
            "    ${tag}<TextBlock Text='{Binding MissingPath}'/>", '  </StackPanel>', '</Window>')
        $typed = $lines -join $eol
        $committed = $typed.Replace('{Binding Cap}', '{Binding Caption}')
        $captionStart = $lines[2].IndexOf('Cap}')
        $completionColumn = $captionStart + 3
        Check ($record.name -eq $name -and $record.prefix -eq $variant.Prefix -and $record.ending -eq $ending -and
            $record.typed -ceq $typed -and $record.committedViaCommand -and 'Caption' -in $record.completions -and
            (Point $record.completionPoint 2 $completionColumn) -and (Point $record.declaration 1 $declarationColumn)) "Typed/completion/declaration evidence differs: $name"
        $bytes = [IO.File]::ReadAllBytes((Join-Path $directory "$name.fleck"))
        Check ([Convert]::ToHexString($bytes) -ceq [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($committed))) "UTF-8 bytes/line endings differ: $name"
        Check ((Local-Path $record.uri) -eq $fixture.Fleck -and (Local-Path $record.sourceUri) -eq $fixture.Source -and
            $record.uri.Contains('%2520') -and $record.sourceUri.Contains('%2520') -and $record.servicePid -eq $matrix.servicePid -and
            $record.serviceArguments.Count -eq 4 -and $record.serviceArguments[0] -eq 'dotnet' -and
            $record.serviceArguments[1] -eq (Join-Path $fixture.Installed 'server/Sunfleck.Development.dll') -and
            $record.serviceArguments[2] -eq 'lsp' -and $record.serviceArguments[3] -eq '--stdio') "URI or actual service identity differs: $name"
        Check (@($record.definitions | Where-Object { (Local-Path $_.uri) -eq $fixture.Source -and (Span $_.range 1 $declarationColumn 0) }).Count -eq 1) "Definition differs: $name"
        Check (@($record.hovers | Where-Object { (Span $_.range 2 $captionStart 7) -and ($_.contents -join '').Contains('UnicodeModel.Caption') -and ($_.contents -join '').Contains('string') }).Count -eq 1) "Hover differs: $name"
        Check ($record.diagnostics.Count -eq 3) "Diagnostic count differs: $name"
        foreach ($diagnostic in @(@{ Code = 'read_only'; Text = 'ReadOnly'; Line = 3 }, @{ Code = 'command_mismatch'; Text = 'SyncCommand'; Line = 4 }, @{ Code = 'path_missing'; Text = 'MissingPath'; Line = 5 })) {
            $column = $lines[$diagnostic.Line].IndexOf($diagnostic.Text)
            Check (@($record.diagnostics | Where-Object { $_.code -eq $diagnostic.Code -and $_.text -ceq $diagnostic.Text -and
                (Span $_.range $diagnostic.Line $column $diagnostic.Text.Length) }).Count -eq 1) "Diagnostic range differs: $name/$($diagnostic.Code)"
        }
        $requests = @($protocol | Where-Object stage -eq $name)
        Check (@($requests | Where-Object { (Local-Path $_.params.textDocument.uri) -ne $fixture.Fleck }).Count -eq 0) "Protocol source differs: $name"
        Check (@($requests | Where-Object { $_.method -eq 'textDocument/completion' -and (Point $_.params.position 2 $completionColumn) -and
            @($_.response | Where-Object { $_.label -eq 'Caption' -and $_.textEdit.newText -eq 'Caption' -and (Span $_.textEdit.range 2 $captionStart 3) }).Count -gt 0 }).Count -gt 0) "Actual completion edit differs: $name"
        Check (@($requests | Where-Object { $_.method -eq 'textDocument/definition' -and (Point $_.params.position 2 ($captionStart + 2)) -and
            (Local-Path $_.response.uri) -eq $fixture.Source -and (Span $_.response.range 1 $declarationColumn 0) }).Count -gt 0) "Actual definition response differs: $name"
        Check (@($requests | Where-Object { $_.method -eq 'textDocument/hover' -and (Point $_.params.position 2 ($captionStart + 2)) -and
            (Span $_.response.range 2 $captionStart 7) -and $_.response.contents.value.Contains('UnicodeModel.Caption') }).Count -gt 0) "Actual hover response differs: $name"
        Check (@($requests | Where-Object { $_.method -eq 'textDocument/didChange' -and
            @($_.params.contentChanges | Where-Object { !$_.range -and $_.text -ceq $committed }).Count -gt 0 }).Count -gt 0) "Actual committed document sync missing: $name"
        Check (@($matrix.cases | Where-Object { $_.name -eq $name -and $_.ending -eq $ending -and $_.prefix -eq $variant.Prefix -and $_.servicePid -eq $matrix.servicePid }).Count -eq 1) "Matrix differs: $name"
        $accepted += $name
    }
}
Check ($matrix.cases.Count -eq 6 -and [IO.File]::ReadAllText($fixture.Fleck) -ceq $committed) 'Matrix count or final saved document differs.'
$report = [ordered]@{ Passed = $true; Run = $runPath; Editor = $hostResult.editor; PackageSHA256 = $packageHash;
    Payload = $payload; ServerDirectory = $serverRoot; Cases = $accepted; ClientInstances = $matrix.clientInstances; ServicePid = $matrix.servicePid;
    ProtocolRecords = $protocol.Count; Scope = 'Static metadata, actual VS Code type/suggestion commands, language APIs and full document sync; no runtime app, exporter, physical keyboard or IME acceptance.' }
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runPath 'unicode-audit.json') -Encoding utf8
Write-Output "Unicode audit passed: $($accepted.Count) cases, $($protocol.Count) protocol records, 7 package/installed/current payload checks."
