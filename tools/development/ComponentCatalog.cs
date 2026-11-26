using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sunfleck;

namespace Sunfleck.Development;

internal sealed partial class LanguageCatalog
{
    private Dictionary<string, string> componentSources = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(string Namespace, string Name), JsonObject> componentTypes = [];
    private List<string> componentManifests = [];
    internal (string Uri, FleckDocument Document)? DictionarySource(string? owner, string source)
    {
        if (!Uri.TryCreate(owner,UriKind.Absolute,out var parent) || !Uri.TryCreate(parent,source,out var child) ||
            !child.IsFile || RuntimeModels.SourcePath(child.AbsoluteUri) is not { } path ||
            !componentSources.TryGetValue(path.Replace('\\','/'),out var text)) return null;
        return (child.AbsoluteUri,new FleckDocument(text));
    }
    internal void LoadComponents(string uri)
    {
        // VS Code sends file:///c%3A/... on Windows. Uri.LocalPath alone
        // leaves a leading slash before the decoded drive and cannot be used
        // as a filesystem path. Share the single-decode path normalization
        // with model discovery (including literal percent signs in filenames).
        if (RuntimeModels.SourcePath(uri) is not { } sourcePath) { return; }
        string? directory = Path.GetDirectoryName(sourcePath);
        if (directory is null || !Directory.Exists(directory)) { return; }
        // Prefer the nearest project so sibling Controls folders are visible.
        var parent = new DirectoryInfo(directory);
        for (int level = 0; parent is not null && level < 4; ++level, parent = parent.Parent) {
            try {
                if (File.Exists(Path.Combine(parent.FullName, "fleck.project.json")) || parent.EnumerateFiles("*.csproj").Any() || File.Exists(Path.Combine(parent.FullName, "CMakeLists.txt"))) { directory = parent.FullName; break; }
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                // Optional parent discovery must not disable completion and
                // definitions for an otherwise accessible open document.
                break;
            }
        }
        SelectBuiltInTypes(directory);
        componentSources.Clear(); componentTypes.Clear(); componentManifests.Clear();
        IEnumerable<string> Files(string folder) {
            foreach (var file in Directory.EnumerateFiles(folder)) { yield return file; }
            foreach (var child in Directory.EnumerateDirectories(folder)) {
                if (Path.GetFileName(child) is "out" or "bin" or "obj" or ".git" or ".tools" or "node_modules" || File.Exists(Path.Combine(child, "fleck.types.json"))) { continue; }
                foreach (var file in Files(child)) { yield return file; }
            }
        }
        try {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Collect(string folder) {
                folder = Path.GetFullPath(folder);
                if (!roots.Add(folder)) { return; }
                foreach (var file in Files(folder).Take(8192)) { discovered.Add(file); }
                foreach (var project in Directory.EnumerateFiles(folder, "*.csproj")) {
                    // The string overload treats paths as URIs and would
                    // decode literal percent escapes in directory names.
                    var xml = XDocument.Parse(File.ReadAllText(project));
                    foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference")) {
                        var include = reference.Attribute("Include")?.Value;
                        if (include is null || include.Contains('$')) { continue; }
                        var path = Path.GetFullPath(Path.Combine(folder, include));
                        if (Path.GetFileName(path) is "Sunfleck.csproj" or "Sunfleck.ComponentGenerator.csproj") { continue; }
                        if (File.Exists(path)) { Collect(Path.GetDirectoryName(path)!); }
                    }
                    foreach (var resource in xml.Descendants().Where(e => e.Name.LocalName == "AdditionalFiles")) {
                        var include = resource.Attribute("Include")?.Value;
                        if (include is null || include.Contains('$')) { continue; }
                        var path = Path.GetFullPath(Path.Combine(folder, include));
                        var resourceFolder = Path.GetDirectoryName(path)!;
                        if (Directory.Exists(resourceFolder)) {
                            foreach (var match in Directory.EnumerateFiles(resourceFolder, Path.GetFileName(path)).Take(8192)) { discovered.Add(match); }
                        }
                    }
                }
                // Only generated metadata is read from build output, never binaries.
                var bin = Path.Combine(folder, "bin");
                if (Directory.Exists(bin)) {
                    foreach (var file in Directory.EnumerateFiles(bin, "*.fleckcomponents.json", SearchOption.AllDirectories).Take(256)) { discovered.Add(file); }
                }
            }
            Collect(directory);
            // CMake writes declaration exports to a small, predictable directory.
            // Inspect only immediate build directories, not the entire build tree.
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent) {
                var candidates = new List<string> { ancestor.FullName };
                foreach (var name in new[] { "out", "build" }) {
                    var output = Path.Combine(ancestor.FullName, name);
                    if (!Directory.Exists(output)) { continue; }
                    candidates.Add(output);
                    candidates.AddRange(Directory.EnumerateDirectories(output).Take(64));
                }
                foreach (var candidate in candidates) {
                    var metadataDirectory = Path.Combine(candidate, ".fleck", "types");
                    if (!Directory.Exists(metadataDirectory)) { continue; }
                    foreach (var metadata in Directory.EnumerateFiles(metadataDirectory, "*.fleckcomponents.json").Take(256)) {
                        var types = JsonNode.Parse(File.ReadAllText(metadata))?["types"]?.AsArray();
                        if (types?.Any(t => t?["source"]?["uri"]?.GetValue<string>() is { } path &&
                            Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == true) { discovered.Add(metadata); }
                    }
                }
                if (File.Exists(Path.Combine(ancestor.FullName, ".git")) || Directory.Exists(Path.Combine(ancestor.FullName, ".git"))) { break; }
            }
            var files = discovered.ToArray();
            foreach (var file in files.Where(file => file.EndsWith(".fleck", StringComparison.OrdinalIgnoreCase))) {
                if (new FileInfo(file).Length > 1024 * 1024) { continue; }
                try {
                    string text = File.ReadAllText(file);
                    var root = XDocument.Parse(text, LoadOptions.SetLineInfo).Root;
                    if (root?.Name.LocalName == "ResourceDictionary") {
                        componentSources[file.Replace('\\','/')] = text;
                        continue;
                    }
                    if (root?.Name.LocalName is not ("UserControl" or "ControlTemplate")) { continue; }
                    string? ns = root.Attribute("Namespace")?.Value, name = root.Attribute("Class")?.Value.Split('.').Last();
                    if (ns is null || name is null) { continue; }
                    componentSources[file] = text;
                    componentTypes[(ns, name)] = new JsonObject {
                        ["name"] = name, ["template"] = root.Name.LocalName == "ControlTemplate",
                        ["className"] = root.Attribute("Class")?.Value,
                        ["source"] = new JsonObject { ["uri"] = RuntimeModels.SourceUri(file), ["line"] = ((System.Xml.IXmlLineInfo)root).LineNumber, ["column"] = ((System.Xml.IXmlLineInfo)root).LinePosition },
                        ["attributes"] = CommonAttributes(), ["children"] = new JsonArray()
                    };
                    if (root.Attribute("TargetType")?.Value is { } target) {
                        int colon = target.IndexOf(':');
                        var targetNamespace = colon < 0 ? root.GetDefaultNamespace() : root.GetNamespaceOfPrefix(target[..colon]);
                        if (targetNamespace is not null) {
                            componentTypes[(ns, name)]["targetNamespace"] = targetNamespace.NamespaceName;
                            componentTypes[(ns, name)]["targetName"] = target[(colon + 1)..];
                        }
                    }
                } catch (System.Xml.XmlException) { }
            }
            var manifests = files.Where(file => file.EndsWith(".fleckcomponents.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).Select(file => (File: file, Text: File.ReadAllText(file))).ToList();
            var sourceManifest = ReadSourceTypes(directory, files);
            var sourceManifestPath = Path.Combine(directory, "source-types");
            if (sourceManifest is not null) { manifests.Add((sourceManifestPath, sourceManifest)); }
            var runtimeTypes = new Dictionary<(string, string), JsonObject>();
            foreach (var (file, text) in manifests) {
                var manifest = JsonNode.Parse(text);
                foreach (var type in (manifest?["types"]?.AsArray().OfType<JsonObject>() ?? []).Concat(manifest?["definitions"]?.AsArray().OfType<JsonObject>() ?? [])) {
                    // Source analysis wins over build output, including deleted declarations.
                    if (sourceManifest is not null && file != sourceManifestPath && type["source"]?["uri"]?.GetValue<string>() is { } typeFile &&
                        typeFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && roots.Any(root => Path.GetFullPath(typeFile).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) { continue; }
                    string ns = type["namespace"]!.GetValue<string>(), name = type["name"]!.GetValue<string>();
                    bool isDefinition = type["template"] is JsonValue templateFlag && templateFlag.TryGetValue<bool>(out var isTemplate) && isTemplate;
                    if (!isDefinition) { runtimeTypes[(ns, name)] = type; }
                    if (!componentTypes.TryGetValue((ns, name), out var description)) {
                        description = new JsonObject { ["name"] = name, ["attributes"] = CommonAttributes(), ["children"] = new JsonArray(), ["source"] = new JsonObject { ["uri"] = RuntimeModels.SourceUri(file) } };
                        componentTypes[(ns, name)] = description;
                    }
                    description["className"] = type["className"]?.DeepClone();
                    description["baseType"] = type["baseType"]?.DeepClone();
                    if (isDefinition) { description["template"] = true; }
                    description["attributes"] = CommonAttributes();
                    if (type["source"] is JsonObject sourceInfo) {
                        description["source"] = sourceInfo.DeepClone();
                        var location = description["source"]!["uri"]?.GetValue<string>();
                        if (location is not null && Path.IsPathFullyQualified(location)) { description["source"]!["uri"] = RuntimeModels.SourceUri(location); }
                    }
                    foreach (var property in type["properties"]?.AsArray().OfType<JsonObject>() ?? []) {
                        string kind = property["type"]!.GetValue<string>();
                        bool readOnly = property["readOnly"]?.GetValue<int>() == 1;
                        bool animatable = kind is "int" or "double" or "color" or "thickness" or "cornerRadius" or "point" or "size" or "matrix" or "length";
                        int flags = property["flags"]?.GetValue<int>() ?? (1 | (readOnly ? 0 : 2) | (animatable ? 64 : 0));
                        description["attributes"]!.AsArray().Add(new JsonObject {
                            ["name"] = property["name"]!.DeepClone(), ["type"] = kind switch { "int" => "integer", "double" => "number", _ => kind },
                            ["readOnly"] = readOnly, ["binding"] = (flags & 1) != 0, ["twoWay"] = (flags & 2) != 0,
                            ["animatable"] = (flags & 64) != 0, ["propertyFlags"] = flags,
                            ["inherits"] = (flags & 128) != 0,
                            ["defaultMode"] = property["mode"]?.DeepClone() ?? JsonValue.Create("OneWay"), ["source"] = property["source"]?.DeepClone()
                        });
                    }
                }
            }
            componentManifests.Add(new JsonObject { ["version"] = 1, ["types"] = new JsonArray(runtimeTypes.Values.Select(t => t.DeepClone()).ToArray()) }.ToJsonString());
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Errors.Add(error.Message); }
    }
    private JsonArray CommonAttributes()
    {
        var result = new JsonArray();
        // Component hosts use the same visual-node attributes, including their
        // binding capabilities and declaration locations in the selected SDK.
        var host = elements["Panel"]["attributes"]!.AsArray();
        foreach (var name in new[] { "Id", "DataContext", "Template", "IsEnabled", "Visibility", "IsHitTestVisible", "IsFocusScope", "TabIndex", "IsTabStop", "FontFamily", "FontSize", "FontWeight", "FontStyle", "FontStretch", "Foreground", "TextAlignment", "TextWrapping", "TextTrimming", "LineHeight", "TextDecorations", "FlowDirection", "Height", "Width", "MinWidth", "MaxWidth", "MinHeight", "MaxHeight", "Margin", "HorizontalAlignment", "VerticalAlignment", "Weight", "Dock" }) {
            if (name is not ("DataContext" or "Template")) {
                result.Add(host.OfType<JsonObject>().Single(a => a["name"]!.GetValue<string>() == name).DeepClone());
            } else {
                result.Add(new JsonObject { ["name"] = name, ["type"] = name == "DataContext" ? "model" : "string",
                    ["binding"] = name == "DataContext", ["twoWay"] = false, ["defaultMode"] = "OneWay",
                    ["values"] = name == "DataContext" ? new JsonArray("null") : null,
                    ["source"] = componentAttributeSources.GetValueOrDefault(name)?.DeepClone() });
            }
        }
        return result;
    }

    private static Dictionary<string, string> Namespaces(FleckDocument.Tag? tag) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (; tag is not null; tag = tag.Parent) {
            foreach (var attribute in tag.Attributes) {
                if (attribute.Name == "xmlns") { result.TryAdd("", attribute.Value); }
                else if (attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal)) { result.TryAdd(attribute.Name[6..], attribute.Value); }
            }
        }
        return result;
    }
    internal IEnumerable<string> ComponentNames(FleckDocument.Tag? tag) {
        foreach (var prefix in Namespaces(tag)) {
            foreach (var entry in componentTypes.Where(entry => entry.Key.Namespace == prefix.Value && entry.Value["template"]?.GetValue<bool>() != true)) { yield return (prefix.Key.Length == 0 ? "" : prefix.Key + ":") + entry.Key.Name; }
        }
    }
    internal IEnumerable<string> TemplateNames(FleckDocument.Tag? tag) {
        if (tag is null) yield break;
        var namespaces = Namespaces(tag);
        int colon = tag.Name.IndexOf(':');
        namespaces.TryGetValue(colon < 0 ? "" : tag.Name[..colon], out var hostNamespace);
        foreach (var prefix in namespaces) {
            foreach (var entry in componentTypes.Where(entry => entry.Key.Namespace == prefix.Value && entry.Value["template"]?.GetValue<bool>() == true)) {
                if (entry.Value["targetName"]?.GetValue<string>() is { } target &&
                    (target != tag.Name[(colon + 1)..] || entry.Value["targetNamespace"]?.GetValue<string>() != hostNamespace)) continue;
                yield return (prefix.Key.Length == 0 ? "" : prefix.Key + ":") + entry.Key.Name;
            }
        }
    }
    internal JsonObject? ComponentElement(FleckDocument.Tag tag, string? name = null) {
        name ??= tag.Name;
        int colon = name.IndexOf(':');
        return Namespaces(tag).TryGetValue(colon < 0 ? "" : name[..colon], out var ns) ? componentTypes.GetValueOrDefault((ns, name[(colon + 1)..])) : null;
    }
    internal JsonObject? BindingOwner(FleckDocument document, FleckDocument.Tag tag, string relativeSource) {
        if (relativeSource == "Self" && ComponentElement(tag) is { } component) { return component; }
        for (var root = tag; root is not null; root = root.Parent) {
            if (root.Name == "ControlTemplate" && root.Find("TargetType") is { } target) {
                if (ComponentElement(root, target.Value) is { } declared) return declared;
                int colon = target.Value.IndexOf(':');
                var prefix = colon < 0 ? "" : target.Value[..colon];
                var name = target.Value[(colon+1)..];
                if (!Namespaces(root).TryGetValue(prefix, out var ns) || ns != "urn:sunfleck:v1" ||
                    !elements.TryGetValue(name, out var builtin)) return null;
                var sources = new JsonArray();
                foreach (var property in builtin["attributes"]!.AsArray())
                    if (property?["templateBindingSource"]?.GetValue<bool>() == true) sources.Add(property.DeepClone());
                return new JsonObject { ["attributes"] = sources };
            }
            if (root.Name == "UserControl" && root.Find("Class") is { } type) { return componentTypes.Values.FirstOrDefault(t => t["className"]?.GetValue<string>() == type.Value); }
        }
        return relativeSource == "Self" ? Element(document, tag) : null;
    }
    internal FleckValidation? ValidateComponents(FleckDocument document) {
        if (document.Theme || !document.Tags.Any(tag => tag.Name.Contains(':') || tag.Name is "UserControl" or "ControlTemplate" or "ResourceDictionary" || tag.Find("Template") is not null || ComponentElement(tag) is not null)) { return App.ValidateFleck(document.Text); }
        using var app = new App(Renderer.Software, headless: true);
        try {
            app.RegisterFleckResources(componentSources);
            foreach (var manifest in componentManifests) { app.RegisterComponentManifest(manifest); }
            return app.ValidateFleckWithComponents(document.Text);
        } catch (SunfleckException error) { return new FleckValidation(FleckError.Value, 1, 1, error.Message); }
    }
}
