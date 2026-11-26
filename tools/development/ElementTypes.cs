using Path = System.IO.Path;
using System.Text.Json.Nodes;
using Sunfleck.Elements;

namespace Sunfleck.Development;

internal sealed partial class LanguageCatalog
{
    private Dictionary<string, JsonObject> componentAttributeSources = new(StringComparer.Ordinal);
    private IEnumerable<JsonObject> BuiltInElements() => Schema["elements"]!.AsArray().OfType<JsonObject>()
        .Concat(Schema["documents"]!.AsArray().OfType<JsonObject>())
        .Concat(Schema["templateElements"]!["elements"]!.AsArray().OfType<JsonObject>());

    private void SelectBuiltInTypes(string directory)
    {
        componentAttributeSources.Clear();
        foreach (var element in BuiltInElements()) {
            element.Remove("source"); element.Remove("className");
            foreach (var attribute in element["attributes"]!.AsArray().OfType<JsonObject>()) { attribute.Remove("source"); }
        }
        string configPath = Path.Combine(directory, "fleck.project.json");
        string sdk = Path.Combine(directory, "sdk");
        try {
            string? language = null;
            if (File.Exists(configPath)) {
                var config = JsonNode.Parse(File.ReadAllText(configPath))!;
                language = config["language"]?.GetValue<string>();
                sdk = Path.GetFullPath(Path.Combine(directory, config["sdk"]?.GetValue<string>() ?? "sdk"));
            }
            string manifestPath = Path.Combine(sdk, "fleck.types.json");
            if (!File.Exists(manifestPath) && !File.Exists(configPath)) { AttachBuiltInTypes(); return; }
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            string declared = manifest["language"]!.GetValue<string>();
            if (manifest["version"]?.GetValue<int>() != 1 || declared is not ("c" or "cpp" or "csharp") ||
                (language is not null && language != declared)) { throw new InvalidDataException("Fleck SDK language/version mismatch."); }
            string source = Path.GetFullPath(Path.Combine(sdk, manifest["source"]!.GetValue<string>()));
            if (!File.Exists(source)) { throw new FileNotFoundException("Fleck SDK declarations are missing.", source); }
            JsonObject Location(JsonNode entry) => new() { ["uri"] = RuntimeModels.SourceUri(source), ["line"] = entry["line"]!.GetValue<int>(), ["column"] = 1 };
            // Validate the entire map before updating a shared catalog.
            var updates = new List<(JsonObject Element, JsonObject Entry)>();
            foreach (var entry in manifest["types"]!.AsArray().OfType<JsonObject>()) {
                string group = entry["group"]!.GetValue<string>(), name = entry["name"]!.GetValue<string>();
                var entries = group switch { "Document" => Schema["documents"], "Theme" => Schema["templateElements"]!["elements"], _ => Schema["elements"] };
                var element = entries!.AsArray().OfType<JsonObject>().Single(e => e["name"]!.GetValue<string>() == name);
                var properties = entry["properties"]!.AsArray().OfType<JsonObject>().ToDictionary(p => p["name"]!.GetValue<string>());
                if (entry["line"]!.GetValue<int>() < 1 || element["attributes"]!.AsArray().Any(a => !properties.ContainsKey(a!["name"]!.GetValue<string>()))) {
                    throw new InvalidDataException("Incomplete Fleck SDK property declarations.");
                }
                updates.Add((element, entry));
            }
            if (updates.Select(u => u.Element).Distinct().Count() != BuiltInElements().Count()) { throw new InvalidDataException("Incomplete Fleck SDK type declarations."); }
            foreach (var (element, entry) in updates) {
                element["source"] = Location(entry); element["className"] = entry["className"]!.DeepClone();
                foreach (var property in entry["properties"]!.AsArray().OfType<JsonObject>()) {
                    var attribute = element["attributes"]!.AsArray().OfType<JsonObject>().Single(a => a["name"]!.GetValue<string>() == property["name"]!.GetValue<string>());
                    attribute["source"] = Location(property);
                }
            }
            foreach (var attribute in manifest["componentAttributes"]?.AsArray().OfType<JsonObject>() ?? []) {
                string attributeFile = Path.GetFullPath(Path.Combine(sdk, attribute["source"]!.GetValue<string>()));
                if (!File.Exists(attributeFile)) { throw new FileNotFoundException("Component attribute declaration is missing.", attributeFile); }
                componentAttributeSources[attribute["name"]!.GetValue<string>()] = new JsonObject {
                    ["uri"] = RuntimeModels.SourceUri(attributeFile), ["line"] = attribute["line"]!.GetValue<int>(), ["column"] = 1
                };
            }
        } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or NullReferenceException) {
            Errors.Add("Fleck SDK: " + error.Message);
        }
    }

    private void AttachBuiltInTypes()
    {
        // Installed tools must navigate within their own SDK even when the
        // original build checkout happens to remain on this computer.
        var file = Path.Combine(AppContext.BaseDirectory, "types", "Elements.cs");
        if (!File.Exists(file)) { file = BuiltInElementTypes.SourceFile; }
        JsonObject Source(int line) => new() { ["uri"] = RuntimeModels.SourceUri(file), ["line"] = line, ["column"] = 1 };
        foreach (var type in BuiltInElementTypes.All) {
            JsonObject? element = type.Group switch {
                "Document" => documents.GetValueOrDefault(type.Name),
                "Theme" => Schema["templateElements"]?["elements"]?.AsArray().OfType<JsonObject>().FirstOrDefault(e => e["name"]?.GetValue<string>() == type.Name),
                _ => elements.GetValueOrDefault(type.Name)
            };
            if (element is null) { continue; }
            element["source"] = Source(type.Line);
            element["className"] = type.Type.FullName;
            foreach (var property in type.Properties) {
                if (element["attributes"]?.AsArray().OfType<JsonObject>().FirstOrDefault(a => a["name"]?.GetValue<string>() == property.Name) is not { } attribute) { continue; }
                attribute["source"] = Source(property.Line);
                attribute["binding"] = property.Bindable;
                attribute["twoWay"] = property.TwoWay;
                attribute["defaultMode"] = property.Mode;
            }
        }
        foreach (var attribute in BuiltInElementTypes.ComponentAttributes) {
            string path = Path.Combine(AppContext.BaseDirectory, "types", attribute.File);
            if (!File.Exists(path)) { path = Path.Combine(Path.GetDirectoryName(BuiltInElementTypes.SourceFile)!, attribute.File); }
            if (File.Exists(path)) { componentAttributeSources[attribute.Name] = new JsonObject { ["uri"] = RuntimeModels.SourceUri(path), ["line"] = attribute.Line, ["column"] = 1 }; }
        }
    }
}
