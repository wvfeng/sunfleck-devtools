param([Parameter(Mandatory = $true)][int]$ProcessId, [switch]$Close, [switch]$VerifyFeatures)
$ErrorActionPreference = 'Stop'
Add-Type -Path "$PSScriptRoot/IdeAutomation.cs" -ReferencedAssemblies System.Drawing
$messageFilter = [SunfleckIdeAutomation]::RetryBusyCalls()
$root = [System.IO.Path]::GetFullPath("$PSScriptRoot/../..")
$output = Join-Path $root 'out/devtools/ide-acceptance'
$processInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
if ($processInfo.Name -ne 'devenv.exe' -or $processInfo.CommandLine -notmatch '/RootSuffix\s+SunfleckAcceptance(?:\s|$)') {
    throw 'Only the temporary SunfleckAcceptance process may be inspected or closed.'
}
$dte = [SunfleckIdeAutomation]::GetDte($ProcessId)
if ($Close) {
    $dte.Quit()
    exit 0
}
$dte.MainWindow.Visible = $true
$window = $dte.ItemOperations.OpenFile("$output/vs-$ProcessId.fleck")
if ($null -eq $window) { throw 'Visual Studio did not create the requested Fleck editor window.' }
$window.Activate()
Write-Output "Active document: $($dte.ActiveDocument.FullName)"
foreach ($document in $dte.Documents) {
    Write-Output "Document: $($document.FullName); language: $($document.Language)"
}
foreach ($pane in $dte.ToolWindows.OutputWindow.OutputWindowPanes) {
    Write-Output "Output pane: $($pane.Name)"
    $textDocument = $pane.TextDocument
    Write-Output $textDocument.StartPoint.CreateEditPoint().GetText($textDocument.EndPoint)
}
if ($VerifyFeatures) {
    $testDocument = $dte.ActiveDocument
    $dte.ActiveDocument.Selection.SelectAll()
    $dte.ActiveDocument.Selection.Insert([IO.File]::ReadAllText("$output/acceptance.fleck"))
    Start-Sleep -Seconds 1
    $result = [ordered]@{ processId = $ProcessId; editor = $dte.Version; passed = @(); errors = @() }
    try {
        $servers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" | Where-Object CommandLine -Like '*Sunfleck.Development.dll* lsp*')
        if ($servers.Count -ne 1) {
            throw 'Expected one bundled Fleck language server child process.'
        }
        $result.passed += 'installed-server-activation'
        $result.server = $servers[0].CommandLine
        $dte.ExecuteCommand('View.ErrorList')
        # VS's modern LSP table is not always exposed through the legacy DTE ErrorItems collection.
        # Preserve the PID screenshot for visual verification when that collection is empty.
        Start-Sleep -Seconds 2
        $errorItems = $dte.ToolWindows.ErrorList.ErrorItems
        $errors = @(for ($index = 1; $index -le $errorItems.Count; $index++) { $errorItems.Item($index).Description })
        $result.diagnostics = $errors
        if (@($errors | Where-Object { $_ -like '*Missing*' }).Count -gt 0) {
            $result.passed += 'binding-diagnostics'
        }
        Write-Output ('Diagnostics: ' + ($errors -join '; '))
        [SunfleckIdeAutomation]::Capture($ProcessId, "$output/visualstudio-diagnostics.png")
        $window.Activate()
        $line = (Get-Content "$output/vs-$ProcessId.fleck")[3]
        $dte.ActiveDocument.Selection.MoveToLineAndOffset(4, $line.IndexOf('Counter.Value') + 4)
        $dte.ExecuteCommand('Edit.GoToDefinition')
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $deadline -and $dte.ActiveDocument.FullName -notlike '*model.h') {
            Start-Sleep -Milliseconds 200
        }
        if ($dte.ActiveDocument.FullName -notlike '*model.h') {
            throw 'Model definition did not navigate to model.h.'
        }
        $result.passed += 'model-definition'
        $result.definition = $dte.ActiveDocument.FullName
        [SunfleckIdeAutomation]::Capture($ProcessId, "$output/visualstudio-definition.png")
        $window.Activate()
        $selection = $dte.ActiveDocument.Selection
        $selection.SelectAll()
        $selection.Insert("<Window xmlns='urn:sunfleck:v1' Model='CCounter'><TextBlock Text='{Binding Counter.")
        $selection.EndOfDocument()
        Write-Output "Completion caret: $($selection.ActivePoint.Line):$($selection.ActivePoint.LineCharOffset)"
        Start-Sleep -Seconds 1
        try {
            $dte.ExecuteCommand('Edit.CompleteWord')
        } catch [System.Runtime.InteropServices.COMException] {
            # Some remote editor command routes report E_FAIL after applying a single completion.
            # Only the resulting document text below can establish success.
            $result.completionCommandHresult = $_.Exception.HResult
        }
        Start-Sleep -Seconds 2
        $textDocument = $testDocument.Object('TextDocument')
        $completed = $textDocument.StartPoint.CreateEditPoint().GetText($textDocument.EndPoint)
        if (-not $completed.EndsWith('Counter.Value')) {
            throw "Binding completion was not applied: $completed"
        }
        [SunfleckIdeAutomation]::Capture($ProcessId, "$output/visualstudio-completion.png")
        $result.passed += 'binding-completion', 'incomplete-syntax'
        $result.completedText = $completed
        foreach ($sample in @(
            @{ Model = 'CppCounter'; Binding = 'Counter.Value'; Prefix = 'Counter.'; Declaration = '*model.hpp' },
            @{ Model = 'CounterModel'; Binding = 'Count'; Prefix = 'Co'; Declaration = '*examples\simple\csharp\fleck\Program.cs' }
        )) {
            $testDocument.Activate()
            $sampleText = "<Window xmlns='urn:sunfleck:v1' Model='$($sample.Model)'><TextBlock Text='{Binding $($sample.Binding)}'/></Window>"
            $testDocument.Selection.SelectAll()
            $testDocument.Selection.Insert($sampleText)
            Start-Sleep -Seconds 1
            $testDocument.Selection.MoveToLineAndOffset(1, $sampleText.LastIndexOf($sample.Binding) + 2)
            $dte.ExecuteCommand('Edit.GoToDefinition')
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while ([DateTime]::UtcNow -lt $deadline -and $dte.ActiveDocument.FullName -notlike $sample.Declaration) {
                Start-Sleep -Milliseconds 200
            }
            if ($dte.ActiveDocument.FullName -notlike $sample.Declaration) {
                throw "$($sample.Model) definition did not navigate to its declaration."
            }
            $result.passed += "$($sample.Model)-definition"
            [SunfleckIdeAutomation]::Capture($ProcessId, "$output/visualstudio-$($sample.Model)-definition.png")
            $testDocument.Activate()
            $testDocument.Selection.SelectAll()
            $testDocument.Selection.Insert("<Window xmlns='urn:sunfleck:v1' Model='$($sample.Model)'><TextBlock Text='{Binding $($sample.Prefix)")
            $testDocument.Selection.EndOfDocument()
            Start-Sleep -Seconds 1
            try {
                $dte.ExecuteCommand('Edit.CompleteWord')
            } catch [System.Runtime.InteropServices.COMException] {
                Write-Verbose $_.Exception.Message
            }
            Start-Sleep -Seconds 2
            $textDocument = $testDocument.Object('TextDocument')
            $completed = $textDocument.StartPoint.CreateEditPoint().GetText($textDocument.EndPoint)
            if (-not $completed.EndsWith($sample.Binding)) {
                throw "$($sample.Model) completion was not applied: $completed"
            }
            $result.passed += "$($sample.Model)-completion"
        }
    } catch {
        $result.errors += $_.Exception.Message
        throw
    } finally {
        $testDocument.Activate()
        $testDocument.Selection.SelectAll()
        $testDocument.Selection.Insert([IO.File]::ReadAllText("$output/acceptance.fleck"))
        $testDocument.Save()
        $result | ConvertTo-Json -Depth 5 | Set-Content "$output/visualstudio-result.json" -Encoding UTF8
    }
}
Start-Sleep -Seconds 2
[SunfleckIdeAutomation]::Capture($ProcessId, "$output/visualstudio.png")
