using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Sunfleck.Development;

internal sealed partial class LanguageCatalog
{
    private string? sourceTypesFingerprint;
    private string? sourceTypesManifest;
    internal IReadOnlyList<Diagnostic> ComponentTypeErrors { get; private set; } = [];
    // Use the same compiler analysis as MSBuild; never load a business assembly,
    // evaluate an initializer, construct a component or invoke a property getter.
    private string? ReadSourceTypes(string directory, string[] files)
    {
        if (!Directory.EnumerateFiles(directory, "*.csproj").Any()) { ComponentTypeErrors = []; return null; }
        var sources = files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0 && !files.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))) { return null; }
        var inputs = sources.Concat(componentSources.Keys).Order(StringComparer.Ordinal).ToArray();
        string fingerprint = string.Join("|", inputs.Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks + ":" + new FileInfo(f).Length));
        if (sourceTypesFingerprint == fingerprint) { return sourceTypesManifest; }
        string cache = Path.Combine(Path.GetTempPath(), "sunfleck-type-definitions", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..24]);
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(FrameworkElement).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).Select(f => MetadataReference.CreateFromFile(f));
        var compilation = CSharpCompilation.Create("FleckEditorTypes",
            sources.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new Sunfleck.Generators.ComponentGenerator()],
            componentSources.Select(p => (AdditionalText)new ComponentText(p.Key, p.Value)),
            optionsProvider: new ComponentOptionsProvider(cache));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        ComponentTypeErrors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        string? manifest = null;
        foreach (var generated in result.Results.SelectMany(r => r.GeneratedSources)) {
            if (generated.HintName == "FleckComponents.g.cs") {
                var attribute = generated.SyntaxTree.GetRoot().DescendantNodes().OfType<AttributeSyntax>()
                    .FirstOrDefault(a => a.Name.ToString().EndsWith("FleckManifestAttribute", StringComparison.Ordinal));
                manifest = (attribute?.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
            } else {
                string path = Path.Combine(cache, "Sunfleck.ComponentGenerator", "Sunfleck.Generators.ComponentGenerator", generated.HintName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, generated.SourceText.ToString());
            }
        }
        sourceTypesFingerprint = fingerprint;
        return sourceTypesManifest = manifest;
    }
    private sealed class ComponentText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
    private sealed class ComponentOptions(string? directory) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) {
            value = key == "build_property.CompilerGeneratedFilesOutputPath" ? directory ?? "" : "";
            return value.Length != 0;
        }
    }
    private sealed class ComponentOptionsProvider(string directory) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new ComponentOptions(directory);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new ComponentOptions(null);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new ComponentOptions(null);
    }
    internal JsonObject? ClassDefinition(string name) => componentTypes.Values.FirstOrDefault(t => t["className"]?.GetValue<string>() == name)?["source"] as JsonObject;
}
