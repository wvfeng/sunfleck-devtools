// Version-specific, local verification of an SDK watcher cleanup fix.
// The installed SDK and Sunfleck product packages are never written.
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

try { return Run(args); }
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static int Run(string[] args)
{
    if (args.Length != 2) { throw new ArgumentException("Provide the installed SDK 11 watcher directory and a new directory under out/devtools."); }
    string source = Path.GetFullPath(args[0]);
    string destination = Path.GetFullPath(args[1]);
    string allowed = Path.GetFullPath("out/devtools") + Path.DirectorySeparatorChar;
    if (!destination.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(destination))
    { throw new ArgumentException("Destination must be a new local out/devtools directory."); }
    const string filename = "Microsoft.DotNet.HotReload.Watch.dll";
    string original = Path.Combine(source, filename);
    string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    const string supported = "162F3EE7AC1C209040E0D462511880FF42D920B39F98CA50B6F19E97BD71DDBB";
    if (Hash(original) != supported) { throw new InvalidOperationException("Unsupported SDK watcher bytes; inspect this version before preparing a fix."); }
    using var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(source);
    using var assembly = AssemblyDefinition.ReadAssembly(original, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
    var module = assembly.MainModule;
    // The shipped watcher computes MSBuild's location by walking six parents.
    // A relocated copy must point back to the same installed SDK for read-only resolution.
    string sdkRoot = Path.GetFullPath(Path.Combine(source, "../../../../../.."));
    if (!File.Exists(Path.Combine(sdkRoot, "MSBuild.dll"))) { throw new InvalidOperationException("Installed SDK root not found."); }
    var sdkGetter = module.GetType("Microsoft.DotNet.Watch.EnvironmentVariables").Methods.Single(method => method.Name == "get_SdkRootDirectory");
    if (sdkGetter.Body.Instructions.Count != 2 || sdkGetter.Body.Instructions[0].OpCode != OpCodes.Ldstr ||
        (string)sdkGetter.Body.Instructions[0].Operand != "") { throw new InvalidOperationException("Unexpected SDK root getter."); }
    sdkGetter.Body.Instructions[0].Operand = sdkRoot;
    var workspace = module.GetType("Microsoft.DotNet.Watch.ManagedCodeWorkspace");
    var service = workspace.Fields.Single(field => field.Name == "HotReloadService");
    var workspaceField = workspace.Fields.Single(field => field.Name == "_workspace");
    var dispose = workspace.Methods.Single(method => method.Name == "Dispose" && method.Parameters.Count == 0);
    var originalDispose = dispose.Body.Instructions.ToArray();
    if (originalDispose.Length != 4 || originalDispose[0].OpCode != OpCodes.Ldarg_0 ||
        originalDispose[1].Operand is not FieldReference oldField || oldField.Name != "_workspace" ||
        originalDispose[2].Operand is not MethodReference workspaceDispose || workspaceDispose.FullName != "System.Void Microsoft.CodeAnalysis.Workspace::Dispose()" ||
        originalDispose[3].OpCode != OpCodes.Ret)
    { throw new InvalidOperationException("Unexpected original Dispose body."); }
    var endSession = service.FieldType.Resolve().Methods.Single(method => method.Name == "EndSession" && method.Parameters.Count == 0);
    var startMachine = workspace.NestedTypes.Single(type => type.Name.StartsWith("<StartSessionAsync>", StringComparison.Ordinal));
    var moveNext = startMachine.Methods.Single(method => method.Name == "MoveNext");
    var started = moveNext.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method &&
        method.Name == "GetResult" && method.DeclaringType.FullName == "System.Runtime.CompilerServices.TaskAwaiter");
    if (moveNext.Body.Variables[1].VariableType.FullName != workspace.FullName)
    { throw new InvalidOperationException("Unexpected async workspace local."); }
    var active = new FieldDefinition("_sunfleckVerificationSessionStarted", FieldAttributes.Private, module.TypeSystem.Boolean);
    workspace.Fields.Add(active);
    var startIL = moveNext.Body.GetILProcessor();
    var load = Instruction.Create(OpCodes.Ldloc_1);
    var value = Instruction.Create(OpCodes.Ldc_I4_1);
    var store = Instruction.Create(OpCodes.Stfld, active);
    startIL.InsertAfter(started, load);
    startIL.InsertAfter(load, value);
    startIL.InsertAfter(value, store);
    // Cecil does not widen short branches automatically after inserting IL.
    var opcodes = typeof(OpCodes).GetFields().Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => opcode.Name);
    foreach (var instruction in moveNext.Body.Instructions)
    {
        if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
        { instruction.OpCode = opcodes[instruction.OpCode.Name[..^2]]; }
    }

    dispose.Body = new MethodBody(dispose) { MaxStackSize = 2 };
    var il = dispose.Body.GetILProcessor();
    var begin = Instruction.Create(OpCodes.Ldarg_0);
    var leave = Instruction.Create(OpCodes.Leave, Instruction.Create(OpCodes.Ret));
    var finish = (Instruction)leave.Operand;
    var cleanup = Instruction.Create(OpCodes.Ldarg_0);
    il.Append(begin);
    il.Emit(OpCodes.Ldfld, active);
    il.Emit(OpCodes.Brfalse, leave);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldfld, service);
    il.Emit(OpCodes.Callvirt, module.ImportReference(endSession));
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldc_I4_0);
    il.Emit(OpCodes.Stfld, active);
    il.Emit(OpCodes.Call, module.ImportReference(typeof(Console).GetProperty(nameof(Console.Error))!.GetMethod!));
    il.Emit(OpCodes.Ldstr, "SUNFLECK_WATCH_SESSION_ENDED: before Workspace.Dispose and next build");
    il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(TextWriter).GetMethod(nameof(TextWriter.WriteLine), new[] { typeof(string) })!));
    il.Append(leave);
    il.Append(cleanup);
    il.Emit(OpCodes.Ldfld, workspaceField);
    il.Emit(OpCodes.Callvirt, workspaceDispose);
    il.Emit(OpCodes.Endfinally);
    il.Append(finish);
    dispose.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
        TryStart = begin, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = finish });

    var copied = new List<object>();
    foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(source, path);
        string target = Path.Combine(destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(path, target, overwrite: false);
        copied.Add(new { File = relative.Replace('\\', '/'), SHA256 = Hash(path) });
    }
    string patched = Path.Combine(destination, filename);
    // Only the newly copied file is overwritten. Keep an original alongside it.
    File.Copy(patched, patched + ".original", overwrite: false);
    module.Mvid = Guid.NewGuid();
    assembly.Write(patched, new WriterParameters { WriteSymbols = false });
    if (Hash(original) != supported) { throw new InvalidOperationException("Installed SDK changed during preparation."); }
    var report = new { Source = source, Destination = destination, OriginalSHA256 = supported, PatchedSHA256 = Hash(patched),
        PatchedMvid = module.Mvid, Files = copied,
        SdkRoot = sdkRoot,
        Change = "Track successful HotReloadService.StartSessionAsync; EndSession once before Workspace.Dispose; always dispose workspace in finally.",
        Relocation = "Resolve MSBuild from the original installed SDK root instead of six parents of the verification copy.",
        Scope = "Local SDK 11 verification copy only; not a Sunfleck product package or global SDK update." };
    File.WriteAllText(Path.Combine(destination, "sunfleck-watch-patch.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { destination, report.OriginalSHA256, report.PatchedSHA256 }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
