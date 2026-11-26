param([Parameter(Mandatory)][string]$Run, [string]$HostRun)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runPath = [IO.Path]::GetFullPath($Run)
$directory = Join-Path $runPath 'editing'
function Read-Evidence([string]$Name) { Get-Content -LiteralPath (Join-Path $directory $Name) -Raw | ConvertFrom-Json }
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Entry-Hash($Entry) {
    $stream = $Entry.Open()
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}
function Offset([string]$Text, $Point) {
    $lines = $Text -split "`n"
    $value = 0
    for ($index = 0; $index -lt $Point.line; $index++) { $value += $lines[$index].Length + 1 }
    $value + $Point.character
}
$summary = Get-Content -LiteralPath (Join-Path $runPath 'summary.json') -Raw | ConvertFrom-Json
$fixture = Read-Evidence 'fixture.json'
$hostResult = Read-Evidence 'vscode-editing-result.json'
$matrix = Read-Evidence 'editing-matrix.json'
$protocol = @(Get-Content -LiteralPath (Join-Path $directory 'editing-protocol.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$actions = @(Get-Content -LiteralPath (Join-Path $directory 'editing-actions.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
Check ($summary.Suites.Count -eq 1 -and $summary.Suites[0].Suite -eq 'editing' -and $summary.Suites[0].Passed -and
    !$summary.Suites[0].TimedOut -and $summary.Suites[0].ExitCode -eq 0) 'Expected standalone passing editing suite.'
Check ($hostResult.errors.Count -eq 0 -and $hostResult.passed.Count -eq 5 -and $hostResult.workspaceTrusted -and
    $hostResult.extensionPath -eq $fixture.Installed) 'Host result or installed extension differs.'
Check ($matrix.clientInstances -eq 1 -and $matrix.servicePid -gt 0 -and
    $matrix.clientModule -eq (Join-Path $fixture.Installed 'node_modules/vscode-languageclient/lib/node/main.js')) 'Expected one actual installed client.'
Check ($matrix.protocolRecords -eq $protocol.Count -and $matrix.actions -eq $actions.Count -and
    @($protocol | Where-Object { $_.error -or $_.servicePid -ne $matrix.servicePid }).Count -eq 0) 'Protocol errors or service identity changed.'
$packageHash = Hash $summary.Package
Check ($packageHash -eq $summary.PackageSHA256) 'Candidate changed after acceptance.'
$provenance = Get-Content -LiteralPath ($summary.Package + '.json') -Raw | ConvertFrom-Json
Check ($provenance.PackageSHA256 -eq $packageHash -and (Hash $provenance.BasePackage) -eq $provenance.BaseSHA256) 'Candidate provenance differs.'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($summary.Package)
$baseline = [IO.Compression.ZipFile]::OpenRead($provenance.BasePackage)
$payload = @(); $changed = @()
try {
    Check ($archive.Entries.Count -eq $baseline.Entries.Count) 'Candidate added or removed package entries.'
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $previous = $baseline.GetEntry($entry.FullName)
        Check ($null -ne $previous) "New package entry: $($entry.FullName)"
        $hash = Entry-Hash $entry
        if ($hash -ne (Entry-Hash $previous)) {
            Check ($entry.FullName -in $provenance.Replacements.File) "Unrelated package payload changed: $($entry.FullName)"
            $changed += $entry.FullName
        }
    }
    foreach ($relative in @('extension.js', 'inspector.js', 'language.js', 'package.json', 'server/Sunfleck.Development.dll', 'server/Sunfleck.dll', 'server/sunfleck_native.dll')) {
        $hash = Entry-Hash ($archive.GetEntry('extension/' + $relative))
        $source = if ($relative.StartsWith('server/')) { Join-Path $provenance.ServerDirectory $relative.Substring(7) }
            else { Join-Path $root ('editors/vscode/' + $relative) }
        Check ($hash -eq (Hash (Join-Path $fixture.Installed $relative)) -and $hash -eq (Hash $source)) "Package/installed/current payload differs: $relative"
        $payload += @{ File = $relative; SHA256 = $hash }
    }
} finally { $archive.Dispose(); $baseline.Dispose() }
$settings = Get-Content -LiteralPath (Join-Path $directory 'profile/User/settings.json') -Raw | ConvertFrom-Json
Check (@($settings.'sunfleck.modelFiles').Count -eq 0 -and !$settings.'sunfleck.serverPath' -and
    $settings.'editor.wordBasedSuggestions' -eq 'off' -and $settings.'[fleck]'.'editor.formatOnType' -and
    !$settings.'[fleck]'.'editor.formatOnSave' -and $settings.'[fleck]'.'editor.semanticHighlighting.enabled') 'Fixture settings differ.'
$accepted = @(); $completionCount = 0; $closingCount = 0
foreach ($ending in @('lf', 'crlf')) {
    $eol = if ($ending -eq 'lf') { "`n" } else { "`r`n" }
    foreach ($documentRoot in @('Window', 'Theme')) {
        $name = $documentRoot.ToLowerInvariant() + '-' + $ending
        $record = Read-Evidence "$name.json"
        $lines = if ($documentRoot -eq 'Window') {
            @('<Window xmlns="urn:sunfleck:v1" Title="中文🌱" Width="640">', '<!--中文🌱-->', '<StackPanel>', '<Button IsEnabled="true"/>', '</StackPanel>', '</Window>')
        } else {
            @('<Theme xmlns="urn:sunfleck:v1" Base="Fluent2">', '<!--中文🌱-->', '<DockTemplate Part="Title">', '<Fill/>', '</DockTemplate>', '</Theme>')
        }
        $text = $lines -join $eol
        Check ($record.name -eq $name -and $record.text -ceq $text -and $record.ending -eq $ending -and
            $record.servicePid -eq $matrix.servicePid -and $record.serviceArguments.Count -eq 4 -and
            $record.serviceArguments[1] -eq (Join-Path $fixture.Installed 'server/Sunfleck.Development.dll') -and
            $record.serviceArguments[2] -eq 'lsp' -and $record.serviceArguments[3] -eq '--stdio') "Document/service differs: $name"
        Check ([Convert]::ToHexString([IO.File]::ReadAllBytes((Join-Path $directory "$name.fleck"))) -ceq
            [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($text))) "Saved UTF-8/EOL bytes differ: $name"
        Check ($record.malformed.text -ceq $text.Substring(0, $text.Length - 1) -and
            @($record.malformed.codes | Where-Object { $_ -like 'fleck_*' }).Count -gt 0 -and
            $record.recoveredDiagnostics.Count -eq 0) "Deletion/recovery differs: $name"
        $requests = @($protocol | Where-Object stage -eq $name)
        $steps = @($actions | Where-Object stage -eq $name)
        Check (@($steps | Where-Object { $_.command -eq 'deleteLeft' -and $_.before -ceq $text -and $_.after -ceq $record.malformed.text }).Count -eq 1 -and
            @($steps | Where-Object { $_.command -eq 'type' -and $_.args.text -eq '>' -and $_.before -ceq $record.malformed.text -and $_.after -ceq $text }).Count -eq 1) "Actual delete/type recovery actions missing: $name"
        foreach ($step in $steps | Where-Object command -eq 'type') {
            Check ($step.args.text -eq "`r`n" -or [Globalization.StringInfo]::new($step.args.text).LengthInTextElements -eq 1) "Non-incremental type input: $name"
        }
        Check ($record.completions.Count -eq 6 -and $record.closings.Count -eq 2) "Checkpoint count differs: $name"
        foreach ($completion in $record.completions) {
            $matches = @($requests | Where-Object { $_.method -eq 'textDocument/completion' -and
                $_.params.position.line -eq $completion.point.line -and $_.params.position.character -eq $completion.point.character })
            $edits = @($matches.response | Where-Object label -eq $completion.label)
            $applied = @($edits | Where-Object {
                $start = Offset $completion.before $_.textEdit.range.start
                $end = Offset $completion.before $_.textEdit.range.end
                $replacement = $_.textEdit.newText.Replace('$0', '')
                ($completion.before.Substring(0, $start) + $replacement + $completion.before.Substring($end)) -ceq $completion.expected
            })
            Check ($completion.label -in $completion.suggestions -and $applied.Count -gt 0 -and
                @($steps | Where-Object { $_.command -eq 'acceptSelectedSuggestion' -and $_.after -ceq $completion.expected }).Count -gt 0) "Actual suggestion edit/commit missing: $name/$($completion.label)"
            $completionCount++
        }
        foreach ($closing in $record.closings) {
            Check (@($requests | Where-Object { $_.method -eq 'textDocument/onTypeFormatting' -and $_.params.ch -eq '>' -and
                @($_.response | Where-Object newText -eq ('</' + $closing.name + '>')).Count -gt 0 }).Count -gt 0) "Actual auto close response missing: $name/$($closing.name)"
            $closingCount++
        }
        # Expected spans are fixed fixture facts, independent of the editor lexer.
        $specs = if ($documentRoot -eq 'Window') {
            @(@(0,1,'type','Window'), @(0,8,'property','xmlns'), @(0,15,'string','urn:sunfleck:v1'),
              @(0,32,'property','Title'), @(0,39,'string','中文🌱'), @(0,45,'property','Width'), @(0,52,'string','640'),
              @(2,1,'type','StackPanel'), @(3,1,'type','Button'), @(3,8,'property','IsEnabled'), @(3,19,'string','true'),
              @(4,2,'type','StackPanel'), @(5,2,'type','Window'))
        } else {
            @(@(0,1,'type','Theme'), @(0,7,'property','xmlns'), @(0,14,'string','urn:sunfleck:v1'),
              @(0,31,'property','Base'), @(0,37,'string','Fluent2'), @(2,1,'type','DockTemplate'),
              @(2,14,'property','Part'), @(2,20,'string','Title'), @(3,1,'type','Fill'), @(4,2,'type','DockTemplate'), @(5,2,'type','Theme'))
        }
        Check ($record.tokens.Count -eq $specs.Count -and $record.rawTokens.Count -eq $specs.Count * 5) "Token count differs: $name"
        $line = 0; $column = 0
        for ($index = 0; $index -lt $specs.Count; $index++) {
            $expected = $specs[$index]; $token = $record.tokens[$index]; $raw = $record.rawTokens[($index * 5)..($index * 5 + 4)]
            $line += $raw[0]; $column = if ($raw[0]) { $raw[1] } else { $column + $raw[1] }
            Check ($line -eq $expected[0] -and $column -eq $expected[1] -and $raw[2] -eq $expected[3].Length -and
                $record.legend.tokenTypes[$raw[3]] -eq $expected[2] -and $raw[4] -eq 0 -and
                $token.line -eq $line -and $token.column -eq $column -and $token.length -eq $raw[2] -and
                $token.type -eq $expected[2] -and $token.text -ceq $expected[3] -and
                $lines[$line].Substring($column, $raw[2]) -ceq $expected[3]) "UTF-16 semantic span differs: $name/$index"
        }
        Check (@($requests | Where-Object { $_.method -eq 'textDocument/semanticTokens/full' -and
            ($_.response.data -join ',') -eq ($record.rawTokens -join ',') }).Count -gt 0) "Actual semantic response missing: $name"
        $nested = if ($documentRoot -eq 'Window') { 'StackPanel' } else { 'DockTemplate' }
        $leaf = if ($documentRoot -eq 'Window') { 'Button' } else { 'Fill' }
        Check ($record.symbols.Count -eq 1 -and $record.symbols[0].name -eq $documentRoot -and
            $record.symbols[0].children.Count -eq 1 -and $record.symbols[0].children[0].name -eq $nested -and
            $record.symbols[0].children[0].children.Count -eq 1 -and $record.symbols[0].children[0].children[0].name -eq $leaf) "Symbol hierarchy differs: $name"
        Check (@($requests | Where-Object { $_.method -eq 'textDocument/documentSymbol' -and $_.response[0].name -eq $documentRoot -and
            $_.response[0].children[0].name -eq $nested -and $_.response[0].children[0].children[0].name -eq $leaf }).Count -gt 0 -and
            @($requests | Where-Object { $_.method -eq 'textDocument/didChange' -and
                @($_.params.contentChanges | Where-Object { !$_.range -and $_.text -ceq $text }).Count -gt 0 }).Count -gt 0) "Actual symbols or final full sync missing: $name"
        $accepted += $name
    }
}
Check (($matrix.cases -join ',') -eq ($accepted -join ',')) 'Matrix differs.'
$regression = @()
if ($HostRun) {
    $hostPath = [IO.Path]::GetFullPath($HostRun)
    $hostSummary = Get-Content -LiteralPath (Join-Path $hostPath 'summary.json') -Raw | ConvertFrom-Json
    Check ($hostSummary.PackageSHA256 -eq $packageHash -and $hostSummary.Suites.Count -eq 6) 'Default regression package or suite count differs.'
    foreach ($suite in $hostSummary.Suites) {
        Check ($suite.Suite -in @('base', 'diagnostics', 'formatting', 'model-switch', 'multi-root', 'folder-task') -and
            $suite.Passed -and !$suite.TimedOut -and $suite.ExitCode -eq 0 -and $suite.Result.errors.Count -eq 0 -and
            $suite.Result.editor -eq $hostResult.editor -and $suite.Result.extensionPath -eq (Join-Path $hostPath ($suite.Suite + '/extensions/sunfleck.sunfleck-0.1.0'))) "Default regression failed or installed path differs: $($suite.Suite)"
        foreach ($file in $payload) {
            Check ((Hash (Join-Path $suite.Result.extensionPath $file.File)) -eq $file.SHA256) "Default installed payload differs: $($suite.Suite)/$($file.File)"
        }
        $regression += @{ Suite = $suite.Suite; Installed = $suite.Result.extensionPath; PayloadChecks = $payload.Count; Passed = $true }
    }
    Check (@($regression.Suite | Sort-Object -Unique).Count -eq 6) 'Duplicate default regression suites.'
}
[ordered]@{ Passed = $true; Run = $runPath; Editor = $hostResult.editor; PackageSHA256 = $packageHash;
    BaseSHA256 = $provenance.BaseSHA256; ChangedPackageEntries = $changed; Payload = $payload; Cases = $accepted;
    Completions = $completionCount; AutoClosings = $closingCount; Actions = $actions.Count; ProtocolRecords = $protocol.Count;
    ClientInstances = $matrix.clientInstances; ServicePid = $matrix.servicePid;
    DefaultRegression = $regression;
    Scope = 'Window/Theme LF/CRLF, actual type/delete/suggestion commands, semantic token data and symbols; no physical input, IME, runtime app or rendered-color acceptance.'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runPath 'editing-audit.json') -Encoding utf8
Write-Output "Editing audit passed: $($accepted.Count) cases, $completionCount completions, $closingCount auto closings, $($actions.Count) actions, $($protocol.Count) protocol records."
