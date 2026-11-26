// Read-only IL call-site inspection of the installed SDK, without loading its assemblies.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

try { return Run(args); }
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static int Run(string[] args)
{
    if (args.Length is < 1 or > 2) { throw new ArgumentException("Provide an SDK assembly path and optional method-name regex."); }
    using var stream = File.OpenRead(args[0]);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var filter = new Regex(args.Length > 1 ? args[1] : ".*");
    var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => unchecked((ushort)opcode.Value));
    string TypeName(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => TypeDefinition((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Namespace) + "." + metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name),
        HandleKind.TypeSpecification => "<generic-type>",
        _ => handle.Kind.ToString()
    };
    string TypeDefinition(TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        return (type.IsNested ? TypeDefinition(type.GetDeclaringType()) + "+" : metadata.GetString(type.Namespace) + ".") + metadata.GetString(type.Name);
    }
    string Symbol(EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            return TypeDefinition(method.GetDeclaringType()) + "." + metadata.GetString(method.Name);
        }
        if (handle.Kind == HandleKind.MemberReference)
        {
            var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            return TypeName(member.Parent) + "." + metadata.GetString(member.Name);
        }
        if (handle.Kind == HandleKind.MethodSpecification) { return Symbol(metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method); }
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            var field = metadata.GetFieldDefinition((FieldDefinitionHandle)handle);
            return TypeDefinition(field.GetDeclaringType()) + "." + metadata.GetString(field.Name);
        }
        return TypeName(handle);
    }
    var methods = new List<object>();
    foreach (var handle in metadata.MethodDefinitions)
    {
        var method = metadata.GetMethodDefinition(handle);
        string name = Symbol(handle);
        if (!filter.IsMatch(name) || method.RelativeVirtualAddress == 0) { continue; }
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        var reader = body.GetILReader();
        var instructions = new List<object>();
        while (reader.RemainingBytes > 0)
        {
            int offset = reader.Offset;
            ushort code = reader.ReadByte();
            if (code == 0xfe) { code = (ushort)(0xfe00 | reader.ReadByte()); }
            var opcode = opcodes[code];
            object? operand = null;
            switch (opcode.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    operand = Symbol(MetadataTokens.EntityHandle(reader.ReadInt32())); break;
                case OperandType.InlineString:
                    operand = metadata.GetUserString(MetadataTokens.UserStringHandle(reader.ReadInt32() & 0xffffff)); break;
                case OperandType.InlineI: case OperandType.InlineBrTarget: case OperandType.InlineSig: operand = reader.ReadInt32(); break;
                case OperandType.InlineI8: operand = reader.ReadInt64(); break;
                case OperandType.InlineR: operand = reader.ReadDouble(); break;
                case OperandType.ShortInlineR: operand = reader.ReadSingle(); break;
                case OperandType.InlineVar: operand = reader.ReadUInt16(); break;
                case OperandType.ShortInlineI: case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineVar: operand = reader.ReadByte(); break;
                case OperandType.InlineSwitch:
                    int count = reader.ReadInt32(); var targets = new int[count];
                    for (int index = 0; index < count; index++) { targets[index] = reader.ReadInt32(); }
                    operand = targets; break;
                case OperandType.InlineNone: break;
                default: throw new InvalidDataException("Unsupported IL operand: " + opcode.OperandType);
            }
            instructions.Add(new { Offset = offset, OpCode = opcode.Name, Operand = operand });
        }
        methods.Add(new
        {
            Name = name,
            Instructions = instructions,
            ExceptionRegions = body.ExceptionRegions.Select(region => new { Kind = region.Kind.ToString(), region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength })
        });
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Assembly = Path.GetFullPath(args[0]),
        SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[0]))),
        Mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid),
        Methods = methods
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
