$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$dir=Join-Path $root 'out/devtools/ide-acceptance/enc'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item "$PSScriptRoot/enc-fixture/main.cpp" "$dir/main.cpp"
$page="<Window xmlns='urn:sunfleck:v1' Title='Sunfleck EnC Acceptance' Width='420' Height='260'><StackPanel><TextBlock Id='count' Text='Count: 0'/><Button Id='increment' Content='Increment'/></StackPanel></Window>"
[IO.File]::WriteAllText("$dir/main.fleck",$page)
$forward=$root.Replace('\','/')
$pagePath=([Uri]"$dir/main.fleck").AbsoluteUri
$project=@"
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
 <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Debug|x64"><Configuration>Debug</Configuration><Platform>x64</Platform></ProjectConfiguration><ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
 <PropertyGroup Label="Globals"><ProjectGuid>{DCC22158-C901-444C-928D-B7376D4708F2}</ProjectGuid><Keyword>Win32Proj</Keyword><WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion></PropertyGroup>
 <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.Default.props"/>
 <PropertyGroup Label="Configuration"><ConfigurationType>Application</ConfigurationType><UseDebugLibraries>true</UseDebugLibraries><PlatformToolset>v145</PlatformToolset></PropertyGroup>
 <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.props"/>
 <PropertyGroup><OutDir>`$(ProjectDir)bin\`$(Configuration)\</OutDir><IntDir>`$(ProjectDir)obj\`$(Configuration)\</IntDir><TargetName>EncCounter</TargetName><LinkIncremental>true</LinkIncremental><LocalDebuggerEnvironment>SUNFLECK_DEBUG_PIPE=sunfleck-vs-acceptance</LocalDebuggerEnvironment><DebuggerFlavor>WindowsLocalDebugger</DebuggerFlavor></PropertyGroup>
 <ItemDefinitionGroup><ClCompile><AdditionalIncludeDirectories>$forward/include;%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories><LanguageStandard>stdcpp20</LanguageStandard><DebugInformationFormat>EditAndContinue</DebugInformationFormat><Optimization>Disabled</Optimization><RuntimeLibrary>MultiThreadedDebugDLL</RuntimeLibrary><PreprocessorDefinitions>SUNFLECK_ENC_PAGE=&quot;$pagePath&quot;;%(PreprocessorDefinitions)</PreprocessorDefinitions><AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions></ClCompile><Link><SubSystem>Windows</SubSystem><GenerateDebugInformation>DebugFull</GenerateDebugInformation><AdditionalDependencies>$forward/out/devtools/sunfleck.lib;user32.lib;gdi32.lib;imm32.lib;dwmapi.lib;advapi32.lib;d3d11.lib;%(AdditionalDependencies)</AdditionalDependencies></Link></ItemDefinitionGroup>
 <ItemGroup><ClCompile Include="main.cpp"/><None Include="main.fleck"/></ItemGroup>
 <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.targets"/>
</Project>
"@
[IO.File]::WriteAllText("$dir/EncCounter.vcxproj",($project -replace '\r?\n',"`r`n"))
$solution=@'
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 18
VisualStudioVersion = 18.0.0.0
MinimumVisualStudioVersion = 10.0.40219.1
Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "EncCounter", "EncCounter.vcxproj", "{DCC22158-C901-444C-928D-B7376D4708F2}"
EndProject
Global
 GlobalSection(SolutionConfigurationPlatforms) = preSolution
  Debug|x64 = Debug|x64
  Release|x64 = Release|x64
 EndGlobalSection
 GlobalSection(ProjectConfigurationPlatforms) = postSolution
  {DCC22158-C901-444C-928D-B7376D4708F2}.Debug|x64.ActiveCfg = Debug|x64
  {DCC22158-C901-444C-928D-B7376D4708F2}.Debug|x64.Build.0 = Debug|x64
  {DCC22158-C901-444C-928D-B7376D4708F2}.Release|x64.ActiveCfg = Release|x64
  {DCC22158-C901-444C-928D-B7376D4708F2}.Release|x64.Build.0 = Release|x64
 EndGlobalSection
EndGlobal
'@
[IO.File]::WriteAllText("$dir/EncCounter.sln",($solution -replace '\r?\n',"`r`n"))
$vswhere="${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$installation=& $vswhere -latest -products '*' -property installationPath
& "$installation/MSBuild/Current/Bin/MSBuild.exe" "$dir/EncCounter.vcxproj" /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo
exit $LASTEXITCODE
