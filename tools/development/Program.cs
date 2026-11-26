using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunfleck;

namespace Sunfleck.Development;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1 && args[0] == "lsp" && args.Skip(1).All(argument =>
                argument == "--stdio" || argument.StartsWith("--clientProcessId=", StringComparison.Ordinal) &&
                int.TryParse(argument[18..], out int processId) && processId > 0))
            {
                return new LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput()).Run();
            }
            if (args.Length == 3 && args[0] == "components") {
                using var stream = File.OpenRead(args[1]);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes()) {
                    var attribute = reader.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) { continue; }
                    var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (constructor.Parent.Kind != HandleKind.TypeReference) { continue; }
                    var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                    if (reader.GetString(type.Namespace) != "Sunfleck" || reader.GetString(type.Name) != "FleckManifestAttribute") { continue; }
                    var blob = reader.GetBlobReader(attribute.Value);
                    if (blob.ReadUInt16() != 1) { throw new InvalidDataException("Invalid component metadata attribute."); }
                    var json = blob.ReadSerializedString() ?? throw new InvalidDataException("Missing component metadata.");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                    File.WriteAllText(args[2], json);
                    return 0;
                }
                throw new InvalidDataException("No generated component metadata found in the assembly.");
            }
            if (args.Length >= 3 && args[0] == "model")
            {
                ExportModels(args[1], args[2], args.Skip(3).ToArray());
                return 0;
            }
            Console.Error.WriteLine("Usage: Sunfleck.Development lsp | model assembly output.fleckmodel.json [type ...]");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    internal static void ExportModels(string assemblyPath, string outputPath, string[] typeNames)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        var directory = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var types = typeNames.Length == 0 ? assembly.GetTypes().Where(type => type.IsClass && !type.IsGenericTypeDefinition && (type.IsPublic || type.IsNestedPublic)).ToArray()
            : typeNames.Select(name => assembly.GetType(name, throwOnError: true)!).ToArray();
        var models = new JsonArray();
        foreach (var type in types)
        {
            var metadata = JsonNode.Parse(PropertyModel.DescribeType(type))!.AsObject();
            foreach (var property in metadata["properties"]!.AsArray())
            {
                var reflected = type.GetProperty(property!["path"]!.GetValue<string>())!;
                if (reflected.GetMethod is { } getter && FindSource(getter) is { } source)
                {
                    property["source"] = source;
                }
            }
            models.Add(metadata);
        }
        var result = new JsonObject { ["version"] = 1, ["assembly"] = assemblyPath, ["models"] = models };
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + ".tmp";
        File.WriteAllText(temporary, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, outputPath, overwrite: true);
    }

    private static JsonObject? FindSource(MethodInfo getter)
    {
        // Tokens are module-local. Inherited properties must use their declaring module's PDB.
        string modulePath = getter.Module.FullyQualifiedName;
        string pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(modulePath) || !File.Exists(pdbPath))
        {
            return null;
        }
        try
        {
            using (var binary = File.OpenRead(modulePath))
            using (var pe = new PEReader(binary))
            using (var pdb = File.OpenRead(pdbPath))
            using (var provider = MetadataReaderProvider.FromPortablePdbStream(pdb))
            {
                var reader = provider.GetMetadataReader();
                var id = new BlobContentId(reader.DebugMetadataHeader!.Id);
                if (!pe.ReadDebugDirectory().Any(entry => entry.Type == DebugDirectoryEntryType.CodeView && pe.ReadCodeViewDebugDirectoryData(entry).Guid == id.Guid))
                {
                    return null;
                }
                var handle = MetadataTokens.MethodDebugInformationHandle(getter.MetadataToken & 0x00ffffff);
                var method = reader.GetMethodDebugInformation(handle);
                foreach (var point in method.GetSequencePoints())
                {
                    if (point.IsHidden)
                    {
                        continue;
                    }
                    var document = point.Document.IsNil ? method.Document : point.Document;
                    if (!document.IsNil)
                    {
                        string path = reader.GetString(reader.GetDocument(document).Name);
                        return new JsonObject
                        {
                            ["uri"] = Path.IsPathFullyQualified(path) ? new Uri(path).AbsoluteUri : path,
                            ["line"] = point.StartLine,
                            ["column"] = point.StartColumn
                        };
                    }
                }
            }
        }
        catch (BadImageFormatException)
        {
            // Windows PDBs and unrelated/stale PDBs supply no reliable source location.
        }
        return null;
    }
}
