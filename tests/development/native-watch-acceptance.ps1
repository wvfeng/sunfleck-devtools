param([ValidateSet('c','cpp','csharp')][string[]]$Language = @('c','cpp','csharp'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$output = Join-Path $root ('out/windows-dev/native-watch-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Path $output | Out-Null
Write-Output "Watch evidence: $output"
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
Import-Module (Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$result = @{ passed=@(); errors=@(); generations=@() }
function Read-Snapshot([string]$Pipe) {
    $stream = [IO.Pipes.NamedPipeClientStream]::new('.', $Pipe, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $stream.Connect(200)
        $writer = [IO.StreamWriter]::new($stream); $writer.AutoFlush=$true
        $reader = [IO.StreamReader]::new($stream)
        $writer.WriteLine('{"method":"snapshot"}')
        $reading = $reader.ReadLineAsync()
        if (!$reading.Wait(1000)) { return $null }
        return ($reading.Result | ConvertFrom-Json).result
    } catch { return $null } finally { $stream.Dispose() }
}
function Await-Snapshot([string]$Pipe,[string]$Text) {
    $until=[DateTime]::UtcNow.AddSeconds(70)
    while ([DateTime]::UtcNow -lt $until) {
        $snapshot=Read-Snapshot $Pipe
        if ($snapshot -and @($snapshot.nodes | Where-Object text -eq $Text).Count) { return $snapshot }
        Start-Sleep -Milliseconds 150
    }
    throw "Snapshot did not reach $Text on $Pipe"
}
function Read-SharedLog([string]$Path) {
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $reader=[IO.StreamReader]::new($file)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
$native = @'
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <sunfleck/ui.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
int WINAPI WinMain(HINSTANCE a,HINSTANCE b,LPSTR c,int d) {
    (void)a;(void)b;(void)c;(void)d;
    const char* revision="A";
    const char* file=getenv("SUNFLECK_RESTART_STATE_FILE");
    int count=0; FILE* input=file?fopen(file,"r"):NULL;
    if(input) { if(fscanf(input,"%d",&count)!=1) count=0; fclose(input); }
    ++count;
    ui_app* app=NULL;
    ui_app_options options={sizeof(options),UI_API_VERSION,UI_SOFTWARE,UI_DEBUG_PIPE};
    if(ui_app_create(&options,&app)!=UI_OK) return 1;
    char page[512]; snprintf(page,sizeof(page),"<Window xmlns='urn:sunfleck:v1' Title='Native Watch Acceptance'><TextBlock Text='%s:%d'/></Window>",revision,count);
    if(ui_app_load_fleck(app,page,strlen(page),NULL)!=UI_OK || ui_app_open_window(app)!=UI_OK) return 2;
    const ui_result status=ui_app_run(app);
    ui_app_destroy(app);
    FILE* saved=file?fopen(file,"w"):NULL;
    if(saved) { fprintf(saved,"%d",count); fclose(saved); }
    return status==UI_EXITED?0:3;
}
'@
$managed = @'
using Sunfleck;
const string revision="A";
string file=Environment.GetEnvironmentVariable("SUNFLECK_RESTART_STATE_FILE")!;
int count=File.Exists(file)?int.Parse(File.ReadAllText(file)):0;
++count;
using(var app=new App(Renderer.Software)) {
    app.LoadFleck($"<Window xmlns='urn:sunfleck:v1' Title='Managed Watch Acceptance'><TextBlock Text='{revision}:{count}'/></Window>");
    app.OpenWindow(); app.Run();
}
File.WriteAllText(file,count.ToString());
'@
try {
    foreach ($kind in $Language) {
        $dir=Join-Path $output $kind
        New-Item -ItemType Directory -Path $dir | Out-Null
        $forward=$root.Replace('\','/')
        if ($kind -eq 'csharp') {
            $source=Join-Path $dir 'Program.cs'; $original=$managed
            @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Reference Include="Sunfleck"><HintPath>$forward/out/windows-dev/service/Sunfleck.dll</HintPath></Reference></ItemGroup></Project>
"@ | Set-Content "$dir/WatchApp.csproj"
            @"
cmake_minimum_required(VERSION 3.25)
project(Watch LANGUAGES NONE)
add_custom_target(watch_app COMMAND dotnet build "`${CMAKE_CURRENT_SOURCE_DIR}/WatchApp.csproj" -o "`${CMAKE_CURRENT_BINARY_DIR}" -v:q COMMAND "`${CMAKE_COMMAND}" -E copy_if_different "$forward/out/devtools/sunfleck_native.dll" "`${CMAKE_CURRENT_BINARY_DIR}/sunfleck_native.dll")
"@ | Set-Content "$dir/CMakeLists.txt"
            $executable="$dir/build/WatchApp.exe"
        } else {
            $extension=if($kind -eq 'c'){'c'}else{'cpp'}
            $source=Join-Path $dir "main.$extension"; $original=$native
            @"
cmake_minimum_required(VERSION 3.25)
project(Watch LANGUAGES C CXX)
add_executable(watch_app WIN32 main.$extension)
target_include_directories(watch_app PRIVATE "$forward/include")
target_link_libraries(watch_app PRIVATE "$forward/out/devtools/sunfleck.lib" user32 gdi32 imm32 dwmapi advapi32 d3d11)
"@ | Set-Content "$dir/CMakeLists.txt"
            $executable="$dir/build/watch_app.exe"
        }
        [IO.File]::WriteAllText($source,$original)
        cmake -S $dir -B "$dir/build" -G Ninja -DCMAKE_BUILD_TYPE=Debug *> "$dir/configure.log"
        if($LASTEXITCODE) { throw "Configure failed: $dir" }
        $pipe="sunfleck-watch-$kind-$PID"
        $env:SUNFLECK_DEBUG_PIPE=$pipe
        $watch=Start-Process pwsh -ArgumentList @('-NoProfile','-File',"`"$root/tools/development/watch-native.ps1`"",'-BuildDirectory',"`"$dir/build`"",'-Target','watch_app','-Executable',"`"$executable`"",'-SourceDirectory',"`"$dir`"",'-StateFile',"`"$dir/state.txt`"") -WindowStyle Hidden -PassThru -RedirectStandardOutput "$dir/watch.log" -RedirectStandardError "$dir/stderr.log"
        try {
            $first=Await-Snapshot $pipe 'A:1'
            # A compiler failure must leave this process and its business state alive.
            [IO.File]::WriteAllText($source,$original + "`nINVALID_BUILD_TOKEN`n")
            $until=[DateTime]::UtcNow.AddSeconds(30)
            while ((Read-SharedLog "$dir/watch.log") -notmatch 'keeps running') {
                if([DateTime]::UtcNow -gt $until) { throw 'Failed build was not observed' }
                Start-Sleep -Milliseconds 200
            }
            $failed=Await-Snapshot $pipe 'A:1'
            if($failed.processId -ne $first.processId) { throw 'Build failure replaced the running app' }
            [IO.File]::WriteAllText($source,$original.Replace('revision="A"','revision="B"'))
            $second=Await-Snapshot $pipe 'B:2'
            if($second.processId -eq $first.processId -or $second.debugSession -eq $first.debugSession) { throw 'Successful rebuild did not create a new session' }
            if([IO.File]::ReadAllText("$dir/state.txt") -ne '1') { throw 'Old business state was not saved' }
            $result.passed += "$kind-build-failure-preserves-app-success-reconnects-and-restores-state"
            $result.generations += @{language=$kind;before=$first.processId;after=$second.processId;session=$second.debugSession}
        } finally {
            if (!$watch.HasExited) { & taskkill /PID $watch.Id /T /F | Out-Null }
            $watch.Dispose()
            Remove-Item Env:SUNFLECK_DEBUG_PIPE -ErrorAction SilentlyContinue
        }
    }
} catch { $result.errors += $_.ToString(); throw }
finally { $result | ConvertTo-Json -Depth 8 | Set-Content "$output/result.json"; $result | ConvertTo-Json -Depth 4 }
