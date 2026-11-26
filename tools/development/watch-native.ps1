param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string[]]$SourceDirectory,
    [string[]]$ApplicationArgument = @(),
    [string]$StateFile,
    [switch]$NoHotReload,
    [ValidateRange(100, 60000)][int]$StateSaveTimeoutMilliseconds = 5000,
    [switch]$Hidden,
    [ValidateRange(100, 60000)][int]$DebounceMilliseconds = 400,
    [switch]$Once
)
$ErrorActionPreference = 'Stop'
$BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
$Executable = [IO.Path]::GetFullPath($Executable)
$SourceDirectory = @($SourceDirectory | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if ($StateFile) {
    $StateFile = [IO.Path]::GetFullPath($StateFile)
}

function Get-SourceState {
    $entries = foreach ($directory in $SourceDirectory) {
        Get-ChildItem -LiteralPath $directory -Recurse -File |
            Where-Object { $_.Name -eq 'CMakeLists.txt' -or
                ($_.Extension -in '.c','.cc','.cpp','.h','.hpp','.inl','.cmake','.fleck','.cs','.csproj' -and
                    $_.FullName -notmatch '[\\/](bin|obj)[\\/]') } |
            ForEach-Object { '{0}|{1}|{2}' -f $_.FullName, $_.Length, $_.LastWriteTimeUtc.Ticks }
    }
    return (($entries | Sort-Object -Unique) -join "`n")
}

$application = $null
$attempted = $null
$observed = $null
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('sunfleck-watch-' + [guid]::NewGuid())
$stage = 0
function Remove-StagedApplication([int]$number) {
    $directory = Join-Path $stagingRoot $number
    Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $directory -ErrorAction SilentlyContinue
}
try {
    while ($true) {
        $state = Get-SourceState
        if ($null -eq $observed -or $state -ne $observed) {
            $observed = $state
            $changedAt = [DateTime]::UtcNow
        }
        if (($null -eq $attempted -or $state -ne $attempted) -and
            ($Once -or ([DateTime]::UtcNow - $changedAt).TotalMilliseconds -ge $DebounceMilliseconds)) {
            $attempted = $state
            & cmake --build $BuildDirectory --target $Target
            $buildResult = $LASTEXITCODE
            if ($buildResult -eq 0 -and (Test-Path -LiteralPath $Executable -PathType Leaf)) {
                if ($Once) {
                    exit 0
                }
                if ((Get-SourceState) -ne $state) {
                    continue
                }
                $stage++
                $launchDirectory = Join-Path $stagingRoot $stage
                New-Item -ItemType Directory -Path $launchDirectory -Force | Out-Null
                $launchExecutable = Join-Path $launchDirectory ([IO.Path]::GetFileName($Executable))
                Copy-Item -LiteralPath $Executable -Destination $launchExecutable
                $binDirectory = Split-Path -Parent $Executable
                Get-ChildItem -LiteralPath $binDirectory -Filter '*.dll' -File |
                    Copy-Item -Destination $launchDirectory
                $entryName = [IO.Path]::GetFileNameWithoutExtension($Executable)
                foreach ($suffix in '.deps.json', '.runtimeconfig.json') {
                    $sidecar = Join-Path $binDirectory ($entryName + $suffix)
                    if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
                        Copy-Item -LiteralPath $sidecar -Destination $launchDirectory
                    }
                }
                $launch = @{
                    FilePath = $launchExecutable
                    WorkingDirectory = $binDirectory
                    PassThru = $true
                }
                if ($ApplicationArgument.Count) {
                    $launch.ArgumentList = $ApplicationArgument
                }
                if ($Hidden) {
                    $launch.WindowStyle = 'Hidden'
                }
                if ($StateFile) {
                    if ($application -and -not $application.HasExited) {
                        if (!$application.CloseMainWindow() -or !$application.WaitForExit($StateSaveTimeoutMilliseconds)) {
                            Write-Warning 'The running application did not close for state handoff. Restart deferred until the next source change.'
                            Remove-StagedApplication $stage
                            continue
                        }
                        if ($application.ExitCode -ne 0) {
                            Write-Warning 'The previous application reported a failure while saving state. Restart deferred until the next source change.'
                            Remove-StagedApplication $stage
                            continue
                        }
                    }
                }
                $previousStateFile = $env:SUNFLECK_RESTART_STATE_FILE
                # A local debug Pipe has exclusive ownership. Retire the previous
                # generation before starting a replacement using that same name.
                # Build failures never reach this point, so the old app stays live.
                if ($env:SUNFLECK_DEBUG_PIPE -and $application -and -not $application.HasExited) {
                    if (!$application.CloseMainWindow() -or !$application.WaitForExit($StateSaveTimeoutMilliseconds)) {
                        $application.Kill()
                        $application.WaitForExit()
                    }
                }
                try {
                    if ($StateFile) {
                        $env:SUNFLECK_RESTART_STATE_FILE = $StateFile
                    }
                    $next = Start-Process @launch
                } catch {
                    if ($NoHotReload) { throw }
                    Write-Warning "The rebuilt application could not start: $($_.Exception.Message)"
                    Remove-StagedApplication $stage
                    continue
                } finally {
                    if ($StateFile) {
                        $env:SUNFLECK_RESTART_STATE_FILE = $previousStateFile
                    }
                }
                if ($application -and -not $application.HasExited) {
                    $application.Kill()
                    $application.WaitForExit()
                }
                if ($application) {
                    $application.Dispose()
                    Remove-StagedApplication $applicationStage
                }
                $application = $next
                $applicationStage = $stage
                if ($NoHotReload) {
                    $application.WaitForExit()
                    exit $application.ExitCode
                }
            } elseif ($Once -or $NoHotReload) {
                if ($buildResult -eq 0) {
                    throw "Build succeeded but executable was not found: $Executable"
                }
                exit $buildResult
            } else {
                Write-Warning "Build or executable check failed for $Target. The current application keeps running."
            }
        }
        Start-Sleep -Milliseconds 100
    }
} finally {
    if ($application) {
        if ($StateFile -and -not $application.HasExited -and $application.CloseMainWindow()) {
            $null = $application.WaitForExit($StateSaveTimeoutMilliseconds)
        }
        if (-not $application.HasExited) {
            $application.Kill()
            $application.WaitForExit()
        }
        $application.Dispose()
    }
    for ($index = 1; $index -le $stage; $index++) {
        Remove-StagedApplication $index
    }
    if ($stage) {
        Remove-Item -LiteralPath $stagingRoot -ErrorAction SilentlyContinue
    }
}
