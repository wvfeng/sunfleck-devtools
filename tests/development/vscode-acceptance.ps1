param(
    [string]$Code = "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe",
    [string]$Package,
    [string]$BuildDirectory = 'out/devtools',
    [string]$CSharpModel,
    [string]$DemoRoot,
    [ValidateSet('base', 'diagnostics', 'formatting', 'model-switch', 'multi-root', 'folder-task', 'unicode', 'editing', 'components', 'demo-c', 'demo-cpp', 'demo-csharp')]
    [string[]]$Suite = @('base', 'diagnostics', 'formatting', 'model-switch', 'multi-root', 'folder-task'),
    [int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Package) { $version = (Get-Content (Join-Path $root 'editors/vscode/package.json') -Raw | ConvertFrom-Json).version; $Package = Join-Path $root "editors/vscode/sunfleck-$version.vsix" }
$packagePath = [IO.Path]::GetFullPath($Package)
$codePath = [IO.Path]::GetFullPath($Code)
if (!(Test-Path -LiteralPath $codePath -PathType Leaf)) { throw "VS Code host not found: $codePath" }
if (!(Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "VSIX not found: $packagePath" }
$nodePath = (Get-Command node -CommandType Application | Select-Object -First 1).Source
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
if (!$CSharpModel) { $CSharpModel = Join-Path $buildRoot 'examples/simple/csharp/fleck/CounterFleck.fleckmodel.json' }
$modelFiles = @((Join-Path $buildRoot 'c.fleckmodel.json'), (Join-Path $buildRoot 'cpp.fleckmodel.json'), [IO.Path]::GetFullPath($CSharpModel))
foreach ($modelFile in $modelFiles) {
    if (!(Test-Path -LiteralPath $modelFile -PathType Leaf)) { throw "Build the development examples first; model metadata missing: $modelFile" }
}
$run = Join-Path $root ('out/devtools/vscode-current/run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
Write-Output "VS Code host evidence: $run"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
$manifestReader = [IO.StreamReader]::new($archive.GetEntry('extension/package.json').Open())
try { $packageVersion = ($manifestReader.ReadToEnd() | ConvertFrom-Json).version } finally { $manifestReader.Dispose() }
$results = @()
try {
    foreach ($name in $Suite) {
        $directory = Join-Path $run $name
        $profile = Join-Path $directory 'profile'
        $extensions = Join-Path $directory 'extensions'
        $installed = Join-Path $extensions "sunfleck.sunfleck-$packageVersion"
        $user = Join-Path $profile 'User'
        New-Item -ItemType Directory -Path $installed, $user -Force | Out-Null
        # Extract only the packaged extension, without invoking any installer.
        foreach ($entry in $archive.Entries) {
            if (!$entry.FullName.StartsWith('extension/', [StringComparison]::Ordinal) -or $entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName.Substring('extension/'.Length)
            $destination = [IO.Path]::GetFullPath((Join-Path $installed $relative))
            if (!$destination.StartsWith($installed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "VSIX entry escapes isolated extension directory: $relative"
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination)
        }
        $settings = @{
            'security.workspace.trust.enabled' = $false
            'telemetry.telemetryLevel' = 'off'
            'update.mode' = 'none'
            'extensions.autoUpdate' = $false
            'extensions.autoCheckUpdates' = $false
            'workbench.startupEditor' = 'none'
            'window.restoreWindows' = 'none'
            'sunfleck.modelFiles' = $modelFiles
        }
        if ($name -eq 'formatting') {
            $settings['[fleck]'] = @{ 'editor.tabSize' = 2; 'editor.insertSpaces' = $true; 'editor.detectIndentation' = $false }
        }
        $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $user 'settings.json') -Encoding utf8
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $codePath
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.WorkingDirectory = $root
        # Do not inherit Electron's CLI mode or unrelated test switches.
        $start.Environment.Remove('ELECTRON_RUN_AS_NODE') | Out-Null
        foreach ($key in @($start.Environment.Keys)) {
            if ($key.StartsWith('SUNFLECK_TEST_')) { $start.Environment.Remove($key) | Out-Null }
        }
        $start.Environment['SUNFLECK_TEST_OUTPUT'] = $directory
        $arguments = @('--new-window', '--user-data-dir', $profile, '--extensions-dir', $extensions,
            '--shared-data-dir', (Join-Path $directory 'shared-data'),
            ('--extensionDevelopmentPath=' + (Join-Path $root 'tests/development/vscode-host')),
            ('--extensionTestsPath=' + (Join-Path $root 'tests/development/vscode-host/run.js')),
            '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust', '--disable-updates', '--disable-telemetry')
        switch ($name) {
            { $_ -in @('demo-c', 'demo-cpp', 'demo-csharp') } {
                $start.Environment['SUNFLECK_TEST_DEMO'] = $name.Substring(5)
                if (!$DemoRoot) { throw 'Specify -DemoRoot for the independently distributed demo projects.' }
                $start.Environment['SUNFLECK_TEST_DEMO_ROOT'] = [IO.Path]::GetFullPath($DemoRoot)
                $arguments += (Join-Path $DemoRoot $name.Substring(5))
            }
            'components' {
                $start.Environment['SUNFLECK_TEST_COMPONENTS'] = '1'
                $arguments += $root
            }
            'unicode' {
                $workspace = Join-Path $directory '中文 🌱 %20 workspace'
                New-Item -ItemType Directory -Path $workspace | Out-Null
                $declaration = Join-Path $workspace '模型 🌱 %20.cs'
                $sourceLines = @('class UnicodeModel {', '    /* 中文🌱 */ public string Caption => "";',
                    '    /* 中文🌱 */ public string ReadOnly => "";', '    /* 中文🌱 */ public object SyncCommand => null;', '}')
                [IO.File]::WriteAllText($declaration, ($sourceLines -join "`r`n"))
                # A literal percent sequence in a filesystem path must be escaped as data.
                $declarationUri = & $nodePath -e 'process.stdout.write(require("node:url").pathToFileURL(process.argv[1]).href)' $declaration
                if ($LASTEXITCODE -ne 0) { throw 'Could not encode Unicode fixture source URI.' }
                $properties = @()
                foreach ($propertyName in @('Caption', 'ReadOnly', 'SyncCommand')) {
                    $lineIndex = $properties.Count + 1
                    $property = @{ path = $propertyName; type = 'string'; writable = ($propertyName -eq 'Caption')
                        source = @{ uri = $declarationUri; line = $lineIndex + 1; column = $sourceLines[$lineIndex].IndexOf($propertyName) + 1 } }
                    if ($propertyName -eq 'SyncCommand') { $property.type = 'command'; $property.commandModes = @('Sync') }
                    $properties += $property
                }
                $metadata = Join-Path $workspace '中文 model.fleckmodel.json'
                @{ version = 1; models = @(@{ name = 'UnicodeModel'; properties = $properties }) } |
                    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadata -Encoding utf8
                $fleck = Join-Path $workspace '页面 🌱 %20.fleck'
                [IO.File]::WriteAllText($fleck, "<Window xmlns='urn:sunfleck:v1' Model='UnicodeModel'/>`n")
                $fixturePath = Join-Path $directory 'fixture.json'
                @{ Directory = $directory; Installed = $installed; Fleck = $fleck; Source = $declaration; Metadata = $metadata } |
                    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fixturePath -Encoding utf8
                $settings['sunfleck.modelFiles'] = @($metadata)
                $settings['editor.wordBasedSuggestions'] = 'off'
                $settings['editor.suggestSelection'] = 'first'
                $settings['[fleck]'] = @{ 'editor.formatOnType' = $false; 'editor.formatOnSave' = $false; 'editor.detectIndentation' = $false; 'editor.autoIndent' = 'none' }
                $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $user 'settings.json') -Encoding utf8
                $start.Environment['SUNFLECK_TEST_UNICODE'] = $fixturePath
                $arguments += $workspace
            }
            'editing' {
                $workspace = Join-Path $directory 'workspace'
                New-Item -ItemType Directory -Path $workspace | Out-Null
                $fleck = Join-Path $workspace '编辑 🌱.fleck'
                [IO.File]::WriteAllText($fleck, '')
                $fixturePath = Join-Path $directory 'fixture.json'
                @{ Directory = $directory; Installed = $installed; Fleck = $fleck } |
                    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fixturePath -Encoding utf8
                $settings['sunfleck.modelFiles'] = @()
                $settings['editor.wordBasedSuggestions'] = 'off'
                $settings['editor.suggestSelection'] = 'first'
                $settings['[fleck]'] = @{
                    'editor.formatOnType' = $true; 'editor.formatOnSave' = $false
                    'editor.autoIndent' = 'none'; 'editor.detectIndentation' = $false
                    'editor.autoClosingQuotes' = 'never'; 'editor.autoClosingBrackets' = 'never'
                    'editor.quickSuggestions' = $false; 'editor.suggestOnTriggerCharacters' = $false
                    'editor.semanticHighlighting.enabled' = $true
                }
                $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $user 'settings.json') -Encoding utf8
                $start.Environment['SUNFLECK_TEST_EDITING'] = $fixturePath
                $arguments += $workspace
            }
            'diagnostics' { $start.Environment['SUNFLECK_TEST_DIAGNOSTICS'] = '1' }
            'formatting' { $start.Environment['SUNFLECK_TEST_FORMATTING'] = '1' }
            'model-switch' { $start.Environment['SUNFLECK_TEST_MODEL_SWITCH'] = '1' }
            'multi-root' {
                $start.Environment['SUNFLECK_TEST_MULTI_ROOT'] = '1'
                $folders = @()
                foreach ($configuration in @('Debug', 'Release')) {
                    $folder = Join-Path $directory $configuration
                    New-Item -ItemType Directory -Path (Join-Path $folder '.vscode') -Force | Out-Null
                    $model = Get-Content -LiteralPath $modelFiles[0] -Raw | ConvertFrom-Json
                    $property = if ($configuration -eq 'Debug') { 'Counter.Value' } else { 'Counter.Other' }
                    $model.models[0].properties[0].path = $property
                    $model | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $folder 'model.fleckmodel.json') -Encoding utf8
                    "<Window xmlns='urn:sunfleck:v1' Model='CCounter'><TextBlock Text='{Binding $property}'/></Window>" | Set-Content -LiteralPath (Join-Path $folder 'counter.fleck') -Encoding utf8
                    @{ 'sunfleck.modelFiles' = @('model.fleckmodel.json') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder '.vscode/settings.json') -Encoding utf8
                    $folders += @{ path = $folder; name = $configuration }
                }
                $workspace = Join-Path $directory 'test.code-workspace'
                @{ folders = $folders } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $workspace -Encoding utf8
                $arguments += $workspace
            }
            'folder-task' {
                $start.Environment['SUNFLECK_TEST_FOLDER_TASK'] = '1'
                $folder = Join-Path $directory 'workspace'
                New-Item -ItemType Directory -Path (Join-Path $folder '.vscode') -Force | Out-Null
                $marker = Join-Path $folder 'task-ran.txt'
                $expression = "require('node:fs').writeFileSync(" + (ConvertTo-Json $marker -Compress) + ", 'isolated development task')"
                @{ version = '2.0.0'; tasks = @(@{ label = 'Sunfleck: Acceptance'; type = 'process'; command = $nodePath; args = @('-e', $expression); problemMatcher = @() }) } |
                    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $folder '.vscode/tasks.json') -Encoding utf8
                $arguments += $folder
            }
        }
        foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $directory 'stdout.log') -Encoding utf8
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $directory 'stderr.log') -Encoding utf8
        $resultFile = Join-Path $directory $(switch ($name) {
            'base' { 'vscode-result.json' }; 'diagnostics' { 'vscode-diagnostics-result.json' }
            'formatting' { 'vscode-formatting-result.json' }; 'model-switch' { 'vscode-model-switch-result.json' }
            'multi-root' { 'vscode-multi-root-result.json' }; 'folder-task' { 'vscode-folder-task-result.json' }
            'unicode' { 'vscode-unicode-result.json' }
            'editing' { 'vscode-editing-result.json' }
            'components' { 'vscode-components-result.json' }
            { $_ -in @('demo-c', 'demo-cpp', 'demo-csharp') } { 'vscode-components-result.json' }
        })
        $hostResult = if (Test-Path -LiteralPath $resultFile) { Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json } else { $null }
        $passed = !$timedOut -and $process.ExitCode -eq 0 -and $null -ne $hostResult -and $hostResult.errors.Count -eq 0
        if ($name -eq 'folder-task' -and !(Test-Path -LiteralPath (Join-Path $directory 'workspace/task-ran.txt'))) { $passed = $false }
        $results += [ordered]@{ Suite = $name; Passed = $passed; TimedOut = $timedOut; ExitCode = $process.ExitCode; Result = $hostResult }
        $process.Dispose()
        [ordered]@{ Package = $packagePath; PackageSHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash; Suites = $results } |
            ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
        Write-Output "$name : passed=$passed timeout=$timedOut"
        if (!$passed) { throw "VS Code suite failed: $name. Evidence: $directory" }
    }
} finally { $archive.Dispose() }
