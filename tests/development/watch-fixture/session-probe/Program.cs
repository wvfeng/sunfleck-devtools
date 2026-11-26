using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Xml.Linq;

try { return await Run(args); }
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static async Task<int> Run(string[] args)
{
    if (args.Length == 1 && args[0] == "--inspect")
    {
        var inspected = Assembly.Load("Microsoft.CodeAnalysis.ExternalAccess.HotReload").GetType("Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService+RunningProjectInfo", true)!;
        Console.WriteLine(JsonSerializer.Serialize(new { Type = inspected.FullName, IsValueType = inspected.IsValueType,
            Constructors = inspected.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(value => value.ToString()),
            Fields = inspected.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(value => value.ToString()),
            Properties = inspected.GetProperties().Select(value => value.ToString()) }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length == 1 && args[0] == "--inspect-service")
    {
        var inspected = Assembly.Load("Microsoft.CodeAnalysis.ExternalAccess.HotReload").GetType("Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService", true)!;
        Console.WriteLine(JsonSerializer.Serialize(inspected.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(value => value.ToString()), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length == 1 && args[0] == "--inspect-workspace")
    {
        var inspected = Assembly.Load("Microsoft.CodeAnalysis.ExternalAccess.HotReload").GetType("Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadMSBuildWorkspace", true)!;
        Console.WriteLine(JsonSerializer.Serialize(inspected.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(value => value.ToString()), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length < 2 || args.Skip(2).Any(value => value is not ("--compile" or "--msbuild" or "--sdk-build" or "--rebuild" or "--shared" or "--large-pdb" or "--end-before-build" or "--patched-workspace" or "--require-cs2012")))
    { throw new ArgumentException("Provide a new output directory and SDK csc.dll path; choose --compile, --msbuild or --sdk-build, optionally --rebuild, --shared, --large-pdb, --end-before-build, --patched-workspace and --require-cs2012."); }
    if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("The file identity probe requires Windows."); }
    bool msbuild = args.Contains("--msbuild");
    bool sdkBuild = args.Contains("--sdk-build");
    bool rebuild = args.Contains("--rebuild");
    bool shared = args.Contains("--shared");
    bool largePdb = args.Contains("--large-pdb");
    bool endBeforeBuild = args.Contains("--end-before-build");
    bool patchedWorkspace = args.Contains("--patched-workspace");
    bool compile = args.Contains("--compile") || msbuild || sdkBuild;
    if (args.Count(value => value is "--compile" or "--msbuild" or "--sdk-build") > 1) { throw new ArgumentException("Choose one compiler mode."); }
    if (shared && !(msbuild || sdkBuild)) { throw new ArgumentException("--shared requires --msbuild or --sdk-build."); }
    if (rebuild && !sdkBuild) { throw new ArgumentException("--rebuild requires --sdk-build."); }
    if (endBeforeBuild && !compile) { throw new ArgumentException("--end-before-build requires a compiler mode."); }
    if (patchedWorkspace && !endBeforeBuild) { throw new ArgumentException("--patched-workspace requires --end-before-build."); }
    bool requireCs2012 = args.Contains("--require-cs2012");
    if (requireCs2012 && !compile) { throw new ArgumentException("--require-cs2012 requires --compile."); }
    if (compile && !string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    { throw new ArgumentException("Launch the compiler comparison with dotnet SessionProbe.dll."); }
    string compilerPath = Path.GetFullPath(args[1]);
    if (!File.Exists(compilerPath)) { throw new FileNotFoundException("SDK compiler missing.", compilerPath); }
    string directory = Path.GetFullPath(args[0]);
    if (Directory.Exists(directory)) { throw new ArgumentException("Probe output directory already exists."); }
    Directory.CreateDirectory(directory);
    string sourcePath = Path.Combine(directory, "Baseline.cs");
    string outputDirectory = sdkBuild ? Path.Combine(directory, "obj/Debug/net10.0") : directory;
    Directory.CreateDirectory(outputDirectory);
    string binaryPath = Path.Combine(outputDirectory, "Baseline.dll");
    string pdbPath = Path.Combine(outputDirectory, "Baseline.pdb");
    string buildProject = Path.Combine(directory, sdkBuild ? "Baseline.csproj" : "Compiler.proj");
    if (msbuild || sdkBuild)
    {
        string compilerDirectory = Path.GetDirectoryName(compilerPath)!;
        string roslynDirectory = Path.GetDirectoryName(compilerDirectory)!;
        string sdkDirectory = Path.GetDirectoryName(roslynDirectory)!;
        string taskAssembly = Path.Combine(roslynDirectory, "Microsoft.Build.Tasks.CodeAnalysis.dll");
        if (!File.Exists(taskAssembly)) { throw new FileNotFoundException("SDK Csc task missing.", taskAssembly); }
        File.WriteAllText(Path.Combine(directory, "global.json"), JsonSerializer.Serialize(new {
            sdk = new { version = Path.GetFileName(sdkDirectory), rollForward = "disable" } }));
        if (sdkBuild)
        {
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("DebugType", "portable"),
                    new XElement("GenerateAssemblyInfo", "false"), new XElement("EnableDefaultCompileItems", "false")),
                new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", sourcePath)))))
                .Save(buildProject);
        }
        else new XDocument(new XElement("Project",
            new XElement("UsingTask", new XAttribute("TaskName", "Microsoft.CodeAnalysis.BuildTasks.Csc"), new XAttribute("AssemblyFile", taskAssembly)),
            new XElement("Target", new XAttribute("Name", "Compile"),
                new XElement("Message", new XAttribute("Text", "Probe SDK: $(MSBuildBinPath)"), new XAttribute("Importance", "high")),
                new XElement("Csc", new XAttribute("Sources", sourcePath), new XAttribute("References", typeof(object).Assembly.Location),
                    new XAttribute("OutputAssembly", binaryPath), new XAttribute("PdbFile", pdbPath), new XAttribute("TargetType", "Library"),
                    new XAttribute("EmitDebugInformation", "true"), new XAttribute("DebugType", "portable"),
                    new XAttribute("NoStandardLib", "true"), new XAttribute("NoConfig", "true"),
                    new XAttribute("UseSharedCompilation", shared ? "true" : "false"),
                    shared ? null : new XAttribute("ToolPath", compilerDirectory)))))
            .Save(buildProject);
    }
    string source = "public class Baseline { public string Caption => \"before\"; }";
    if (largePdb)
    {
        source += "\npublic class Padding {\n" + string.Join("\n", Enumerable.Range(0, 1000).Select(index =>
            $"public int Value{index:D4}() {{ int number = {index}; return number; }}")) + "\n}";
    }
    File.WriteAllText(sourcePath, source);
    var host = MefHostServices.Create(MefHostServices.DefaultAssemblies.Concat(new[] {
        Assembly.Load("Microsoft.CodeAnalysis.CSharp.Workspaces"), Assembly.Load("Microsoft.CodeAnalysis.Features"),
        Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features") }));
    using var workspace = new AdhocWorkspace(host);
    var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Baseline", "Baseline", LanguageNames.CSharp,
        filePath: Path.Combine(directory, "Baseline.csproj"), outputFilePath: binaryPath, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
        parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
        metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }));
    var document = workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Baseline.cs", filePath: sourcePath,
        loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source, new UTF8Encoding(false)), VersionStamp.Create(), sourcePath))));
    var outputInfo = new CompilationOutputInfo().WithAssemblyPath(binaryPath).WithGeneratedFilesOutputDirectory(directory);
    var solution = document.Project.Solution.WithProjectCompilationOutputInfo(project.Id, outputInfo);
    var features = Assembly.Load("Microsoft.CodeAnalysis.Features");
    var supports = features.GetType("Microsoft.CodeAnalysis.EditAndContinue.Extensions")!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        .Single(method => method.Name == "SupportsEditAndContinue" && method.GetParameters()[0].ParameterType == typeof(Project));
    if (!(bool)supports.Invoke(null, new object?[] { solution.GetProject(project.Id), null })!)
    {
        var actual = solution.GetProject(project.Id)!;
        throw new InvalidOperationException($"Probe project does not support Edit and Continue: {actual.FilePath}; {actual.SupportsCompilation}; {JsonSerializer.Serialize(actual.CompilationOutputInfo)}");
    }
    var compilation = (await solution.GetProject(project.Id)!.GetCompilationAsync())!;
    using (var binary = File.Create(binaryPath))
    using (var pdb = File.Create(pdbPath))
    {
        var emission = compilation.Emit(binary, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!emission.Success) { throw new InvalidOperationException(string.Join("\n", emission.Diagnostics)); }
    }
    var serviceAssembly = Assembly.Load("Microsoft.CodeAnalysis.ExternalAccess.HotReload");
    var serviceType = serviceAssembly.GetType("Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService", throwOnError: true)!;
    var service = Activator.CreateInstance(serviceType, solution.Services,
        (Func<ValueTask<ImmutableArray<string>>>)(() => ValueTask.FromResult(ImmutableArray.Create("Baseline", "AddMethodToExistingType", "NewTypeDefinition"))))!;
    object? Invoke(string name, params object[] parameters) => serviceType.GetMethod(name)!.Invoke(service, parameters);
    Assembly? watchAssembly = null;
    Action? disposePatchedWorkspace = null;
    bool inactiveDisposePassed = false, repeatedDisposePassed = false;
    if (patchedWorkspace)
    {
        watchAssembly = Assembly.Load("Microsoft.DotNet.HotReload.Watch");
        var type = watchAssembly.GetType("Microsoft.DotNet.Watch.ManagedCodeWorkspace", true)!;
        const BindingFlags fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var active = type.GetField("_sunfleckVerificationSessionStarted", fields) ?? throw new InvalidOperationException("Expected patched watcher copy.");
        var workspaceType = type.GetField("_workspace", fields)!.FieldType;
        Workspace CreateEmptyWorkspace() => (Workspace)Activator.CreateInstance(workspaceType,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            (Func<string, (ImmutableArray<Microsoft.Build.Execution.ProjectInstance>, Microsoft.Build.Evaluation.Project)>)(_ =>
                throw new InvalidOperationException("This Dispose-only control must not load projects.")))!;
        object Create(Workspace suppliedWorkspace, bool started)
        {
            // Exercise the actual Dispose body with its real workspace field type.
            // The baseline service uses the independent AdhocWorkspace above;
            // this control does not exercise the watcher's constructor or async start.
            var owner = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("_workspace", fields)!.SetValue(owner, suppliedWorkspace);
            type.GetField("HotReloadService", fields)!.SetValue(owner, service);
            active.SetValue(owner, started);
            return owner;
        }
        void Dispose(object owner) => type.GetMethod("Dispose", fields)!.Invoke(owner, null);
        using var inactive = CreateEmptyWorkspace();
        var inactiveOwner = Create(inactive, false);
        Dispose(inactiveOwner);
        Dispose(inactiveOwner);
        inactiveDisposePassed = true;
        var disposableWorkspace = CreateEmptyWorkspace(); // Owned by the patched Dispose calls below.
        var activeOwner = Create(disposableWorkspace, true);
        disposePatchedWorkspace = () => {
            Dispose(activeOwner);
            Dispose(activeOwner);
            repeatedDisposePassed = !(bool)active.GetValue(activeOwner)!;
        };
    }
    var observations = new List<object>();
    var writable = new List<bool>();
    // Keep a read handle to the original file object. Sharing deletion permits csc
    // to replace the path, while file IDs distinguish replacement from lock release.
    using var originalPdb = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    long originalPdbLength = originalPdb.Length;
    string originalIdentity = FileIdentity.Read(originalPdb.SafeFileHandle);
    string? CurrentIdentity()
    {
        try
        {
            using var file = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FileIdentity.Read(file.SafeFileHandle);
        }
        catch (FileNotFoundException) { return null; }
    }
    object Capture(string stage)
    {
        string? identity = CurrentIdentity();
        string utc = DateTime.UtcNow.ToString("O");
        var originalWrite = FileIdentity.TryWriteOpen(originalPdb.SafeFileHandle);
        var mappings = FileIdentity.MappedViews(originalPdb.SafeFileHandle);
        var originalPath = FileIdentity.CurrentPath(originalPdb.SafeFileHandle);
        try
        {
            using var file = new FileStream(pdbPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writable.Add(true);
            return new { Stage = stage, Utc = utc, PathIdentity = identity, OriginalIdentity = FileIdentity.Read(originalPdb.SafeFileHandle),
                IsOriginalFile = identity == originalIdentity, OriginalWriteOpenSucceeded = originalWrite.Succeeded,
                OriginalWriteOpenError = originalWrite.Error, OriginalMappedViews = mappings, OriginalPath = originalPath,
                WriteOpenSucceeded = true, HResult = "00000000" };
        }
        catch (IOException error) { writable.Add(false); return new { Stage = stage, Utc = utc, PathIdentity = identity,
            OriginalIdentity = FileIdentity.Read(originalPdb.SafeFileHandle), IsOriginalFile = identity == originalIdentity,
            OriginalWriteOpenSucceeded = originalWrite.Succeeded, OriginalWriteOpenError = originalWrite.Error,
            OriginalMappedViews = mappings, OriginalPath = originalPath,
            WriteOpenSucceeded = false, HResult = error.HResult.ToString("X8") }; }
    }
    async Task<(int ExitCode, string Text)> RunTool(string stage, string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        // This probe is launched through dotnet, so Environment.ProcessPath is the actual dotnet host.
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
        string text = await stdout + await stderr;
        File.WriteAllText(Path.Combine(directory, stage + ".log"), text);
        return (child.ExitCode, text);
    }
    async Task<(int ExitCode, string Text)> Compile(string stage)
    {
        // Force CoreCompile on each SDK build without adding a clean step.
        if (sdkBuild) { File.WriteAllText(sourcePath, source + "\n// " + stage + "\n"); }
        var arguments = sdkBuild ? new[] { "build", buildProject, "--no-restore", "/t:" + (rebuild ? "Rebuild" : "Build"), "/nologo", "/v:normal", "/nr:false",
                "/p:UseSharedCompilation=" + (shared ? "true" : "false"), "/bl:" + Path.Combine(directory, stage + ".binlog") }
            : msbuild ? new[] { "msbuild", buildProject, "/t:Compile", "/nologo", "/v:normal", "/nr:false", "/bl:" + Path.Combine(directory, stage + ".binlog") }
            : new[] { compilerPath, "/nologo", "/target:library", "/debug:portable", "/out:" + binaryPath, "/pdb:" + pdbPath,
                "/reference:" + typeof(object).Assembly.Location, sourcePath };
        return await RunTool(stage, arguments);
    }
    if (sdkBuild)
    {
        var restore = await RunTool("sdk-restore", new[] { "restore", buildProject, "/nologo", "/nr:false" });
        if (restore.ExitCode != 0) { throw new InvalidOperationException("Isolated SDK restore failed: " + restore.Text); }
    }
    observations.Add(Capture("before-session"));
    await (Task)Invoke("StartSessionAsync", solution, CancellationToken.None)!;
    var updated = solution.WithDocumentText(document.Id, SourceText.From(source.Replace("before", "after"), new UTF8Encoding(false)));
    var runningType = serviceType.GetNestedType("RunningProjectInfo")!;
    var dictionaryType = typeof(ImmutableDictionary<,>).MakeGenericType(typeof(ProjectId), runningType);
    var running = dictionaryType.GetField("Empty")!.GetValue(null)!;
    running = dictionaryType.GetMethod("Add")!.Invoke(running, new[] { project.Id, Activator.CreateInstance(runningType) })!;
    var updateTask = (Task)Invoke("GetUpdatesAsync", updated, running, CancellationToken.None)!;
    await updateTask;
    var updates = updateTask.GetType().GetProperty("Result")!.GetValue(updateTask)!;
    string status = updates.GetType().GetProperty("Status")!.GetValue(updates)!.ToString()!;
    var projectUpdates = updates.GetType().GetProperty("ProjectUpdates")!.GetValue(updates)!;
    int updateCount = (int)projectUpdates.GetType().GetProperty("Length")!.GetValue(projectUpdates)!;
    if (status != "ReadyToApply" || updateCount != 1)
    {
        var diagnostics = new List<string>();
        foreach (var entry in (System.Collections.IEnumerable)updates.GetType().GetProperty("TransientDiagnostics")!.GetValue(updates)!)
        {
            var pair = (System.Runtime.CompilerServices.ITuple)entry;
            foreach (var diagnostic in (System.Collections.IEnumerable)pair[1]!) { diagnostics.Add(diagnostic.ToString()!); }
        }
        throw new InvalidOperationException($"No actual baseline update: {status}; {string.Join(";", diagnostics)}");
    }
    Invoke("CommitUpdate");
    observations.Add(Capture("after-update"));
    if (endBeforeBuild)
    {
        if (patchedWorkspace) { disposePatchedWorkspace!(); }
        else { Invoke("EndSession"); }
        observations.Add(Capture("after-early-end-session"));
    }
    (int ExitCode, string Text)? activeBuild = null, lockedBuild = null, releasedBuild = null;
    if (compile)
    {
        activeBuild = await Compile("active-session-build");
        observations.Add(Capture("after-active-build"));
    }
    bool pathReplaced = CurrentIdentity() is { } afterBuildIdentity && afterBuildIdentity != originalIdentity;
    workspace.Dispose();
    observations.Add(Capture("after-workspace-dispose"));
    if (compile)
    {
        lockedBuild = await Compile("workspace-dispose-build");
        observations.Add(Capture("after-workspace-build"));
    }
    if (!endBeforeBuild) { Invoke("EndSession"); }
    observations.Add(Capture("after-end-session"));
    if (compile)
    {
        releasedBuild = await Compile("end-session-build");
        observations.Add(Capture("after-end-session-build"));
    }
    GC.KeepAlive(service);
    bool reproduced = activeBuild is { ExitCode: not 0 } failed && failed.Text.Contains("error CS2012:", StringComparison.Ordinal);
    bool passed = writable[0] && !writable[1] && writable[^1] &&
        (compile ? releasedBuild?.ExitCode == 0 : !writable[2]) && (!requireCs2012 || reproduced) &&
        (!endBeforeBuild || (activeBuild?.ExitCode == 0 && lockedBuild?.ExitCode == 0)) &&
        (!patchedWorkspace || (inactiveDisposePassed && repeatedDisposePassed));
    var result = new { Directory = directory, Runtime = Environment.Version.ToString(),
        ServiceAssembly = serviceAssembly.Location,
        ServiceSHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(serviceAssembly.Location))),
        Compiler = compilerPath, CompilerSHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(compilerPath))),
        Passed = passed, Mode = sdkBuild ? "SdkBuildComparison" : msbuild ? "MSBuildComparison" : compile ? "CompilerComparison" : "SessionLifecycle",
        Rebuild = rebuild,
        LargePdb = largePdb, OriginalPdbLength = originalPdbLength,
        EndBeforeBuild = endBeforeBuild,
        PatchedWorkspace = patchedWorkspace, InactiveDisposePassed = inactiveDisposePassed, RepeatedDisposePassed = repeatedDisposePassed,
        WatchAssembly = watchAssembly?.Location,
        WatchAssemblySHA256 = watchAssembly is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(watchAssembly.Location))),
        SharedCompilation = shared, RequireCs2012 = requireCs2012,
        ReproducedCS2012 = reproduced, PathReplacedByActiveBuild = pathReplaced,
        UpdateCount = updateCount, Status = status, Observations = observations,
        ActiveSessionBuildExitCode = activeBuild?.ExitCode,
        WorkspaceDisposeBuildExitCode = lockedBuild?.ExitCode, EndSessionBuildExitCode = releasedBuild?.ExitCode };
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(directory, "result.json"), json);
    Console.WriteLine(json);
    return passed ? 0 : 1;
}

static class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public UIntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(UIntPtr address, out MemoryInformation information, UIntPtr length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetMappedFileNameW(IntPtr process, UIntPtr address, StringBuilder path, uint length);
    public static string[] MappedViews(SafeFileHandle original)
    {
        if (IntPtr.Size != 8) { throw new PlatformNotSupportedException("Mapping inspection requires a 64-bit process."); }
        var path = new StringBuilder(32768);
        string expected = CurrentPath(original, 2); // VOLUME_NAME_NT
        var views = new HashSet<ulong>();
        ulong address = 0;
        while (VirtualQuery((UIntPtr)address, out var memory, (UIntPtr)Marshal.SizeOf<MemoryInformation>()) != UIntPtr.Zero)
        {
            if (memory.State == 0x1000 && memory.Type == 0x40000) // MEM_COMMIT, MEM_MAPPED
            {
                path.Clear();
                uint length = GetMappedFileNameW(new IntPtr(-1), memory.BaseAddress, path, (uint)path.Capacity);
                if (length > 0 && length < path.Capacity && string.Equals(expected, path.ToString(), StringComparison.OrdinalIgnoreCase))
                { views.Add(memory.AllocationBase.ToUInt64()); }
            }
            ulong next = memory.BaseAddress.ToUInt64() + memory.RegionSize.ToUInt64();
            if (next <= address) { break; }
            address = next;
        }
        return views.Select(value => $"0x{value:X}").OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
    public static string CurrentPath(SafeFileHandle original, uint flags = 0)
    {
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(original, path, (uint)path.Capacity, flags);
        if (length == 0 || length >= path.Capacity) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        string value = path.ToString();
        return flags == 0 && value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }
    public static (bool Succeeded, int Error) TryWriteOpen(SafeFileHandle original)
    {
        // Open the original object, even after its path was replaced; never write.
        using var reopened = ReOpenFile(original, 0x40000000, 7, 0);
        return reopened.IsInvalid ? (false, Marshal.GetLastWin32Error()) : (true, 0);
    }
    public static string Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        return $"{information.VolumeSerial:X8}:{information.IndexHigh:X8}{information.IndexLow:X8}";
    }
}
