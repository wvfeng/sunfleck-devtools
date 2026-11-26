using System.Text.Json.Nodes;
using Sunfleck;

namespace Sunfleck.Development;

internal static class LanguageFeatures
{
    internal static JsonArray Format(FleckDocument document, JsonNode? options)
    {
        var edits = new JsonArray();
        foreach (var edit in FleckFormatter.GetEdits(document,
            options?["tabSize"]?.GetValue<int>() ?? 4, options?["insertSpaces"]?.GetValue<bool>() ?? true))
        {
            edits.Add(new JsonObject { ["range"] = document.Range(edit.Start, edit.Start + edit.Length), ["newText"] = edit.Text });
        }
        return edits;
    }

    private sealed record Part(string Name, string Value, int Start);
    private static List<Part>? Binding(FleckDocument.Attribute attribute)
    {
        string value = attribute.Value;
        if (!value.StartsWith("{Binding", StringComparison.Ordinal) || value.Length > 8 && !char.IsWhiteSpace(value[8]) && value[8] != '}')
        {
            return null;
        }
        var result = new List<Part>();
        int start = 8;
        char quote = '\0';
        for (int i = start; i <= value.Length; i++)
        {
            char ch = i < value.Length ? value[i] : ',';
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch is not (',' or '}'))
            {
                continue;
            }
            string item = value[start..i];
            int equal = item.IndexOf('=');
            string name = equal < 0 ? (result.Count == 0 ? "Path" : "") : item[..equal].Trim();
            int offset = start + (equal < 0 ? 0 : equal + 1);
            while (offset < i && char.IsWhiteSpace(value[offset]))
            {
                offset++;
            }
            result.Add(new Part(name, value[offset..i].TrimEnd(), attribute.ValueStart + offset));
            start = i + 1;
            if (ch == '}')
            {
                break;
            }
        }
        return result;
    }
    private static string Option(List<Part> parts, string name, string fallback = "") => parts.FirstOrDefault(part => part.Name == name)?.Value ?? fallback;
    private static JsonObject Item(FleckDocument document, string label, int start, int end, string? insert = null, string? detail = null, int kind = 10)
        => new()
        {
            ["label"] = label, ["kind"] = kind, ["detail"] = detail,
            ["textEdit"] = new JsonObject { ["range"] = document.Range(start, end), ["newText"] = insert ?? label }
        };

    internal static JsonArray Complete(LanguageCatalog catalog, FleckDocument document, int offset, bool snippets = true, string? documentUri = null)
    {
        var items = new JsonArray();
        var tag = document.At(offset);
        if (tag is null || offset < tag.NameStart)
        {
            return items;
        }
        int nameEnd = tag.NameStart + tag.Name.Length;
        if (offset <= nameEnd)
        {
            var candidates = tag.Closing ? Ancestors(tag.Parent) : catalog.Children(document, tag.Parent);
            string prefix = document.Text[tag.NameStart..offset];
            foreach (string name in candidates.Distinct(StringComparer.Ordinal).Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(Item(document, name, tag.NameStart, nameEnd, kind: 7));
            }
            return items;
        }
        var attribute = tag.Attributes.FirstOrDefault(attribute => offset >= attribute.ValueStart && offset <= attribute.End && attribute.ValueStart > attribute.Start + attribute.Name.Length);
        if (attribute is not null)
        {
            string prefix = attribute.Value[..Math.Min(offset - attribute.ValueStart, attribute.Value.Length)];
            if (attribute.Name == "Name" && tag.Name is "VisualStateGroup" or "VisualState" &&
                catalog.Schema["visualStateGroups"] is JsonObject groups) {
                IEnumerable<string> names = tag.Name == "VisualStateGroup" ? groups.Select(pair => pair.Key) :
                    tag.Parent?.Find("Name") is { } group && groups[group.Value] is JsonArray states ?
                    states.Select(state => state!.GetValue<string>()) : [];
                foreach (var name in names.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    items.Add(Item(document, name, attribute.ValueStart, attribute.ValueStart + attribute.Value.Length));
                return items;
            }
            if (tag.Name is "Trigger" or "Condition" && attribute.Name == "Property") {
                foreach (var property in catalog.TriggerSources(tag)) {
                    var name = property["name"]!.GetValue<string>();
                    if (name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                        items.Add(Item(document,name,attribute.ValueStart,attribute.ValueStart + attribute.Value.Length));
                }
                return items;
            }
            if (tag.Name == "KeyBinding" && attribute.Name == "Gesture") {
                int start = prefix.LastIndexOf('+') + 1;
                for (int n = 1; n <= 24; ++n) {
                    var key = $"F{n}";
                    if (key.StartsWith(prefix[start..], StringComparison.OrdinalIgnoreCase))
                        items.Add(Item(document,key,attribute.ValueStart + start,attribute.ValueStart + attribute.Value.Length));
                }
                return items;
            }
            if (prefix.StartsWith("{StaticResource ", StringComparison.Ordinal) || prefix.StartsWith("{DynamicResource ", StringComparison.Ordinal)) {
                int keyStart = prefix.IndexOf(' ') + 1;
                string keyPrefix = prefix[keyStart..];
                foreach (var key in ResourceKeys(catalog, document, tag, attribute.Name, documentUri).Select(entry => entry.Key).Where(k => k.Value.StartsWith(keyPrefix, StringComparison.Ordinal)))
                    items.Add(Item(document, key.Value, attribute.ValueStart + keyStart, attribute.ValueStart + attribute.Value.TrimEnd('}').Length));
                return items;
            }
            if (prefix.StartsWith("{TemplateBinding ", StringComparison.Ordinal)) {
                string propertyPrefix = prefix[17..].TrimStart();
                foreach (var property in catalog.BindingOwner(document, tag, "TemplatedParent")?["attributes"]?.AsArray() ?? []) {
                    string name = property!["name"]!.GetValue<string>();
                    if (name.StartsWith(propertyPrefix, StringComparison.OrdinalIgnoreCase)) { items.Add(Item(document, name, offset - propertyPrefix.Length, offset, detail: property["type"]?.GetValue<string>())); }
                }
                return items;
            }
            if (attribute.Name == "Template") {
                foreach (var name in catalog.TemplateNames(tag).Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) { items.Add(Item(document, name, attribute.ValueStart, attribute.ValueStart + attribute.Value.Length)); }
                return items;
            }
            var partial = new FleckDocument.Attribute(attribute.Name, prefix, attribute.Start,
                attribute.ValueStart, attribute.End, attribute.Closed);
            if (Binding(partial) is { } parts && prefix.Length >= 8)
            {
                var part = parts.LastOrDefault();
                if (part is null)
                {
                    return items;
                }
                if (part.Name == "Path")
                {
                    string relativeSource = Option(Binding(attribute) ?? parts, "RelativeSource");
                    if (relativeSource.Length > 0) {
                        foreach (var property in catalog.BindingOwner(document, tag, relativeSource)?["attributes"]?.AsArray() ?? []) {
                            string name = property!["name"]!.GetValue<string>();
                            if (name.StartsWith(part.Value, StringComparison.OrdinalIgnoreCase)) { items.Add(Item(document, name, part.Start, offset, detail: property["type"]?.GetValue<string>())); }
                        }
                        return items;
                    }
                    var (model, uncertainty) = catalog.BindingModel(document, tag, attribute.Name);
                    var target = catalog.Attribute(document, tag, attribute.Name);
                    bool twoWay = Option(parts, "Mode", "Default") is "TwoWay" || Option(parts, "Mode", "Default") == "Default" && target?["defaultMode"]?.GetValue<string>() == "TwoWay";
                    foreach (var property in model?["properties"]?.AsArray() ?? [])
                    {
                        string name = property!["path"]!.GetValue<string>();
                        if (!name.StartsWith(part.Value, StringComparison.OrdinalIgnoreCase) || !Compatible(catalog, document, tag, attribute, property.AsObject(), twoWay))
                        {
                            continue;
                        }
                        items.Add(Item(document, name, part.Start, offset, detail: property["type"]!.GetValue<string>() + (uncertainty is null ? "" : " · " + uncertainty), kind: 10));
                    }
                    return items;
                }
                string[]? options = part.Name switch
                {
                    "Mode" => ["Default", "OneWay", "OneTime", "TwoWay"],
                    "UpdateSourceTrigger" => ["PropertyChanged", "LostFocus"],
                    "RelativeSource" => ["Self", "TemplatedParent", "FindAncestor"],
                    "ElementName" => document.Tags.Select(t => t.Find("Id")?.Value).OfType<string>().Distinct().ToArray(),
                    "" => ["Path=", "Mode=", "UpdateSourceTrigger=", "StringFormat=", "RelativeSource=", "AncestorType=", "ElementName=", "Converter="],
                    _ => null
                };
                foreach (string option in options ?? [])
                {
                    var target = catalog.Attribute(document, tag, attribute.Name);
                    if ((option == "TwoWay" || option == "LostFocus") && target?["twoWay"]?.GetValue<bool>() != true ||
                        option.EndsWith('=') && parts.SkipLast(1).Any(existing => existing.Name + "=" == option))
                    {
                        continue;
                    }
                    if (option.StartsWith(part.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(Item(document, option, part.Start, offset));
                    }
                }
                return items;
            }
            var schema = catalog.Attribute(document, tag, attribute.Name);
            int literalStart = attribute.ValueStart;
            string[] previousValues = [];
            if (schema?["separator"]?.GetValue<string>() is { Length: 1 } separator)
            {
                int last = prefix.LastIndexOf(separator[0]);
                if (last >= 0)
                {
                    previousValues = prefix[..last].Split(separator[0]);
                    literalStart += last + 1;
                    prefix = prefix[(last + 1)..];
                }
            }
            if (schema?["autoValue"]?.GetValue<string>() is { } autoValue && autoValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                items.Add(Item(document, autoValue, literalStart, attribute.End));
            }
            foreach (var value in schema?["values"]?.AsArray() ?? [])
            {
                string literal = value!.GetValue<string>();
                if (attribute.Name == "CommandMode" && CommandProperty(catalog, document, tag) is { } command && !CommandSupports(tag, command, literal))
                {
                    continue;
                }
                if (literal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !previousValues.Contains(literal, StringComparer.Ordinal))
                {
                    items.Add(Item(document, literal, literalStart, attribute.End));
                }
            }
            if (schema?["binding"]?.GetValue<bool>() == true && "{Binding ".StartsWith(prefix, StringComparison.Ordinal))
            {
                var item = Item(document, "Binding", attribute.ValueStart, attribute.End, snippets ? "{Binding $0}" : "{Binding ", catalog.Localize("绑定到导出的模型路径", "Bind to an exported model path"));
                item["insertTextFormat"] = snippets ? 2 : 1;
                items.Add(item);
            }
            return items;
        }
        int word = offset;
        while (word > nameEnd && (char.IsAsciiLetterOrDigit(document.Text[word - 1]) || document.Text[word - 1] == '_'))
        {
            word--;
        }
        string attributePrefix = document.Text[word..offset];
        foreach (var candidate in catalog.Element(document, tag)?["attributes"]?.AsArray() ?? [])
        {
            string name = candidate!["name"]!.GetValue<string>();
            if (name.StartsWith(attributePrefix, StringComparison.OrdinalIgnoreCase) && !tag.Attributes.Any(existing => existing.Name == name && existing.Start != word))
            {
                var existing = tag.Attributes.FirstOrDefault(value => value.Start == word);
                bool hasValue = existing is not null && existing.ValueStart > existing.Start + existing.Name.Length;
                var item = Item(document, name, word, existing is null ? offset : existing.Start + existing.Name.Length,
                    hasValue ? name : name + (snippets ? "=\"$0\"" : "=\""), candidate["type"]?.GetValue<string>(), 10);
                item["insertTextFormat"] = snippets && !hasValue ? 2 : 1;
                items.Add(item);
            }
        }
        return items;
    }

    private static IEnumerable<string> Ancestors(FleckDocument.Tag? tag)
    {
        for (; tag is not null; tag = tag.Parent)
        {
            yield return tag.Name;
        }
    }

    private static bool Compatible(LanguageCatalog catalog, FleckDocument document, FleckDocument.Tag tag, FleckDocument.Attribute attribute, JsonObject property, bool twoWay)
    {
        string type = property["type"]!.GetValue<string>();
        if (twoWay && property["writable"]?.GetValue<bool>() != true)
        {
            return false;
        }
        if (catalog.ComponentElement(tag) is not null) {
            var target = catalog.Attribute(document, tag, attribute.Name);
            if (target?["readOnly"]?.GetValue<bool>() == true) { return false; }
            return target?["type"]?.GetValue<string>() == type;
        }
        return attribute.Name switch
        {
            "Command" => type == "command" && CommandSupports(tag, property),
            "DataContext" or "ItemsSource" => type == "model",
            "Background" or "BorderBrush" or "Foreground" => type == "color",
            "BorderThickness" or "Margin" => type == "thickness",
            "CornerRadius" => type == "cornerRadius",
            "Spacing" or "Padding" => type is "integer" or "number",
            "Weight" => type == "integer",
            "IsEnabled" or "IsHitTestVisible" => type == "bool",
            "Visibility" or "FontWeight" or "FontStyle" or "FontStretch" or "TextAlignment" or "TextWrapping" or "TextTrimming" or "TextDecorations" or "FlowDirection" => type == "enum",
            "FontSize" or "LineHeight" or "Opacity" => type == "number",
            "LayoutTransform" or "RenderTransform" => type == "matrix",
            "RenderTransformOrigin" => type == "point",
            "FontFamily" => type == "string",
            "Height" or "Width" or "MinWidth" or "MaxWidth" or "MinHeight" or "MaxHeight" => type is "length" or "integer" or "number",
            "HorizontalAlignment" or "VerticalAlignment" => type == "string",
            "Text" or "Content" => twoWay ? type == "string" : type is "string" or "bool" or "integer" or "number",
            "Value" when catalog.Attribute(document, tag, "Value")?["binding"]?.GetValue<bool>() == true =>
                type is "integer" or "number" || type == "bool" && tag.Name is "CheckBox" or "RadioButton" or "Switch" or "ToggleButton",
            _ => false
        };
    }

    private static JsonObject? CommandProperty(LanguageCatalog catalog, FleckDocument document, FleckDocument.Tag tag)
    {
        var attribute = tag.Find("Command");
        string? path = attribute is null ? null : Binding(attribute)?.FirstOrDefault(part => part.Name == "Path")?.Value;
        return catalog.BindingModel(document, tag, "Command").Data?["properties"]?.AsArray()
            .FirstOrDefault(property => property?["path"]?.GetValue<string>() == path)?.AsObject();
    }

    private static bool CommandSupports(FleckDocument.Tag tag, JsonObject property, string? mode = null)
    {
        mode ??= tag.Find("CommandMode")?.Value ?? "Sync";
        return mode is not ("Sync" or "Async") || property["commandModes"] is not JsonArray modes ||
            modes.Any(value => value?.GetValue<string>() == mode);
    }

    private static (FleckDocument.Tag Tag, FleckDocument.Attribute Attribute, Part Path, JsonObject? Property)? BoundProperty(LanguageCatalog catalog, FleckDocument document, int offset)
    {
        var tag = document.At(offset);
        var attribute = tag?.Attributes.FirstOrDefault(attribute => offset >= attribute.ValueStart && offset <= attribute.End);
        var parts = attribute is null ? null : Binding(attribute);
        var path = parts?.FirstOrDefault(part => part.Name == "Path");
        if (tag is null || attribute is null || path is null || offset < path.Start || offset > path.Start + path.Value.Length)
        {
            return null;
        }
        var (model, _) = catalog.BindingModel(document, tag, attribute.Name);
        var property = model?["properties"]?.AsArray().FirstOrDefault(property => property!["path"]!.GetValue<string>() == path.Value)?.AsObject();
        return (tag, attribute, path, property);
    }

    internal static JsonNode? Hover(LanguageCatalog catalog, FleckDocument document, int offset)
    {
        if (BoundProperty(catalog, document, offset) is { } binding)
        {
            var (_, uncertainty) = catalog.BindingModel(document, binding.Tag, binding.Attribute.Name);
            string value = binding.Property is null ? catalog.Localize("未找到导出路径。", "Exported path not found.") :
                binding.Property["type"]!.GetValue<string>() + (binding.Property["writable"]?.GetValue<bool>() == true
                    ? catalog.Localize(" · 可写", " · Writable") : catalog.Localize(" · 只读", " · Read-only"));
            if (binding.Property?["commandModes"] is JsonArray modes)
            {
                value += catalog.Localize("\n命令模式：", "\nCommand modes: ") + string.Join(", ", modes.Select(mode => mode!.GetValue<string>()));
            }
            return new JsonObject
            {
                ["contents"] = new JsonObject { ["kind"] = "plaintext", ["value"] = $"{document.Model}.{binding.Path.Value}\n{value}" + (uncertainty is null ? "" : "\n" + uncertainty) },
                ["range"] = document.Range(binding.Path.Start, binding.Path.Start + binding.Path.Value.Length)
            };
        }
        var tag = document.At(offset);
        if (tag is null)
        {
            return null;
        }
        var attribute = tag.Attributes.FirstOrDefault(attribute => attribute.Start <= offset && offset <= attribute.Start + attribute.Name.Length);
        var schema = attribute is null ? catalog.Element(document, tag) : catalog.Attribute(document, tag, attribute.Name);
        if (schema is null)
        {
            return null;
        }
        string description = attribute is null ? tag.Name + (schema["className"] is { } className ? "\n" + className + (schema["baseType"] is { } baseType ? " : " + baseType : "") : "") : $"{tag.Name}.{attribute.Name}: {schema["type"]}";
        if (schema["binding"]?.GetValue<bool>() == true)
        {
            description += catalog.Localize($"\n支持 Binding；默认 {schema["defaultMode"]}。", $"\nSupports Binding. Default mode: {schema["defaultMode"]}.");
        }
        if (schema["values"] is JsonArray values && values.Count > 0)
        {
            description += catalog.Localize("\n取值：", "\nValues: ") + string.Join(", ", values.Select(value => value!.GetValue<string>()));
        }
        if (schema["minimum"] is JsonValue minimum && schema["maximum"] is JsonValue maximum)
        {
            description += catalog.Localize("\n范围：", "\nRange: ") + minimum + " … " + maximum;
        }
        if (schema.ContainsKey("defaultValue"))
        {
            description += catalog.Localize("\n默认值：", "\nDefault value: ") + (schema["defaultValue"]?.ToString() ?? "null");
        }
        if (attribute?.Name == "Weight")
        {
            description += catalog.Localize("\nGrid/Flex 中的空间分配权重，不是宽度。设置宽度请使用 Width；Auto 仅用于 Width/Height。",
                "\nRelative space allocation in Grid/Flex, not a width. Use Width for size; Auto applies only to Width/Height.");
        }
        else if (schema["description"] is JsonValue hint)
        {
            description += "\n" + hint;
        }
        return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "plaintext", ["value"] = description } };
    }

    private sealed record ResourceKey(FleckDocument Document, FleckDocument.Attribute Key, string? Uri);

    private static IEnumerable<ResourceKey> ResourceKeys(LanguageCatalog catalog, FleckDocument document, FleckDocument.Tag use, string attribute, string? documentUri)
    {
        bool style = attribute == "Style" || use.Name == "Style" && attribute == "BasedOn";
        string? target = attribute == "Style" ? use.Name.Split(':')[^1] : use.Find("TargetType")?.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<ResourceKey> Entries(FleckDocument file, FleckDocument.Tag container, string? uri, int depth) {
            if (depth > 64) yield break;
            if (container.Name == "ResourceDictionary" && container.Find("Source") is { } source) {
                if (catalog.DictionarySource(uri,source.Value) is { } external && visited.Add(external.Uri)) {
                    var root = external.Document.Tags.FirstOrDefault(tag => !tag.Closing);
                    if (root?.Name == "ResourceDictionary")
                        foreach (var key in Entries(external.Document,root,external.Uri,depth+1)) yield return key;
                }
                yield break;
            }
            foreach (var entry in file.Tags.AsEnumerable().Reverse()) {
                if (entry.Closing || entry.Parent != container) continue;
                if (file == document && use.Start > container.Start && use.Start < container.Extent &&
                    entry.Start >= use.Start && attribute != "BasedOn") continue;
                if (entry.Name is "ResourceDictionary" or "ResourceDictionary.MergedDictionaries") {
                    foreach (var key in Entries(file,entry,uri,depth+1)) yield return key;
                } else if (entry.Name is "Style" or "Resource") {
                    var key = entry.Find("Key") ?? entry.Find("x:Key");
                    if (key is null || !seen.Add(key.Value) || entry == use || style != (entry.Name == "Style")) continue;
                    if (!style || target == entry.Find("TargetType")?.Value) yield return new(file,key,uri);
                }
            }
        }
        for (var scope = use; scope is not null; scope = scope.Parent) {
            var resources = document.Tags.FirstOrDefault(tag => !tag.Closing && tag.Parent == scope && tag.Name == scope.Name + ".Resources");
            if (resources is not null)
                foreach (var key in Entries(document,resources,documentUri,0)) yield return key;
            if (scope.Parent is null && scope.Name == "ResourceDictionary")
                foreach (var key in Entries(document,scope,documentUri,0)) yield return key;
        }
    }

    internal static JsonNode? Definition(LanguageCatalog catalog, FleckDocument document, int offset, string? documentUri = null)
    {
        static JsonObject Location(JsonObject source) {
            string uri = source["uri"]!.GetValue<string>();
            if (Path.IsPathFullyQualified(uri)) { uri = RuntimeModels.SourceUri(uri); }
            int line = Math.Max(0, (source["line"]?.GetValue<int>() ?? 1) - 1);
            int column = Math.Max(0, (source["column"]?.GetValue<int>() ?? 1) - 1);
            return new JsonObject { ["uri"] = uri, ["range"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = column },
                ["end"] = new JsonObject { ["line"] = line, ["character"] = column } } };
        }
        var componentTag = document.Tags.FirstOrDefault(tag => offset >= tag.NameStart && offset <= tag.NameStart + tag.Name.Length);
        if (componentTag is not null && catalog.Element(document, componentTag)?["source"] is JsonObject componentSource) {
            return Location(componentSource);
        }
        foreach (var tag in document.Tags.Where(t => !t.Closing)) {
            foreach (var attribute in tag.Attributes) {
                if (tag.Name is "Trigger" or "Condition" && attribute.Name == "Property" &&
                    offset >= attribute.ValueStart && offset <= attribute.End &&
                    catalog.TriggerSources(tag).FirstOrDefault(p => p["name"]?.GetValue<string>() == attribute.Value)?["source"] is JsonObject triggerSource)
                    return Location(triggerSource);
                if (tag.Name == "ResourceDictionary" && attribute.Name == "Source" &&
                    offset >= attribute.ValueStart && offset <= attribute.End && documentUri is not null &&
                    Uri.TryCreate(documentUri,UriKind.Absolute,out var pageUri) && Uri.TryCreate(pageUri,attribute.Value,out var dictionaryUri) && dictionaryUri.IsFile &&
                    File.Exists(dictionaryUri.LocalPath))
                    return Location(new JsonObject { ["uri"] = dictionaryUri.AbsoluteUri, ["line"] = 1 });
                if (documentUri is not null && offset >= attribute.ValueStart && offset <= attribute.End &&
                    (attribute.Value.StartsWith("{StaticResource ", StringComparison.Ordinal) ||
                     attribute.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal)) && attribute.Value.EndsWith('}')) {
                    var key = ResourceKeys(catalog, document, tag, attribute.Name, documentUri).FirstOrDefault(k => k.Key.Value == attribute.Value[(attribute.Value.IndexOf(' ') + 1)..^1]);
                    if (key is not null) return new JsonObject { ["uri"] = key.Uri, ["range"] = key.Document.Range(key.Key.ValueStart, key.Key.ValueStart + key.Key.Value.Length) };
                }
                if (attribute.Value.StartsWith("{TemplateBinding ", StringComparison.Ordinal) && offset >= attribute.ValueStart + 17 && offset <= attribute.End &&
                    catalog.BindingOwner(document, tag, "TemplatedParent")?["attributes"]?.AsArray().FirstOrDefault(p => p?["name"]?.GetValue<string>() == attribute.Value[17..].TrimEnd('}').Trim())?["source"] is JsonObject templateBindingSource) { return Location(templateBindingSource); }
                if (offset >= attribute.ValueStart && offset <= attribute.End && Binding(attribute) is { } parts &&
                    parts.FirstOrDefault(p => p.Name == "Path") is { } path && offset >= path.Start && offset <= path.Start + path.Value.Length &&
                    Option(parts, "RelativeSource") is { Length: > 0 } relative &&
                    catalog.BindingOwner(document, tag, relative)?["attributes"]?.AsArray().FirstOrDefault(p => p?["name"]?.GetValue<string>() == path.Value)?["source"] is JsonObject bindingSource) { return Location(bindingSource); }
                if (offset >= attribute.Start && offset <= attribute.Start + attribute.Name.Length &&
                    catalog.Attribute(document, tag, attribute.Name)?["source"] is JsonObject propertySource) { return Location(propertySource); }
                if (attribute.Name == "Class" && offset >= attribute.ValueStart && offset <= attribute.End && catalog.ClassDefinition(attribute.Value) is { } classSource) { return Location(classSource); }
                if (attribute.Name is "Template" or "TargetType" && offset >= attribute.ValueStart && offset <= attribute.End &&
                    catalog.ComponentElement(tag, attribute.Value)?["source"] is JsonObject templateSource) { return Location(templateSource); }
            }
        }
        if (BoundProperty(catalog, document, offset)?.Property?["source"] is not JsonObject source)
        {
            return null;
        }
        string? value = source["uri"]?.GetValue<string>();
        int line = source["line"]?.GetValue<int>() ?? 0, column = source["column"]?.GetValue<int>() ?? 0;
        if (string.IsNullOrEmpty(value) || line <= 0)
        {
            return null;
        }
        string uri = Path.IsPathFullyQualified(value) ? RuntimeModels.SourceUri(value) : value;
        return new JsonObject
        {
            ["uri"] = uri,
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line - 1, ["character"] = Math.Max(0, column - 1) },
                ["end"] = new JsonObject { ["line"] = line - 1, ["character"] = Math.Max(0, column - 1) }
            }
        };
    }

    internal static JsonArray Symbols(FleckDocument document)
    {
        var symbols = new JsonArray();
        var children = new Dictionary<FleckDocument.Tag, JsonArray>();
        foreach (var tag in document.Tags.Where(tag => !tag.Closing && tag.Name.Length > 0))
        {
            var descendants = new JsonArray();
            var symbol = new JsonObject
            {
                ["name"] = tag.Name + (tag.Find("Id") is { } id ? " #" + id.Value : ""), ["kind"] = 19,
                ["range"] = document.Range(tag.Start, tag.Extent), ["selectionRange"] = document.Range(tag.NameStart, tag.NameStart + tag.Name.Length),
                ["children"] = descendants
            };
            (tag.Parent is not null && children.TryGetValue(tag.Parent, out var parent) ? parent : symbols).Add(symbol);
            children[tag] = descendants;
        }
        return symbols;
    }

    internal static JsonArray CloseTag(FleckDocument document, int offset)
    {
        var edits = new JsonArray();
        var tag = document.Tags.LastOrDefault(tag => tag.End == offset && tag.Complete && !tag.Closing && !tag.SelfClosing);
        if (tag is not null && !tag.HasClosing)
        {
            edits.Add(new JsonObject { ["range"] = document.Range(offset, offset), ["newText"] = "</" + tag.Name + ">" });
        }
        return edits;
    }

    internal static JsonObject Tokens(FleckDocument document)
    {
        var spans = new List<(int Start, int End, int Kind)>();
        foreach (var tag in document.Tags)
        {
            spans.Add((tag.NameStart, tag.NameStart + tag.Name.Length, 0));
            foreach (var attribute in tag.Attributes)
            {
                spans.Add((attribute.Start, attribute.Start + attribute.Name.Length, 1));
                spans.Add((attribute.ValueStart, attribute.End, 2));
            }
        }
        var data = new JsonArray();
        int previousLine = 0, previousColumn = 0;
        foreach (var (start, end, kind) in spans.OrderBy(span => span.Start))
        {
            for (int segment = start; segment < end;)
            {
                int newline = document.Text.AsSpan(segment, end - segment).IndexOfAny('\r', '\n');
                int lineEnd = newline < 0 ? end : segment + newline;
                int tokenEnd = lineEnd;
                if (tokenEnd > segment)
                {
                    var position = document.Position(segment);
                    int line = position["line"]!.GetValue<int>(), column = position["character"]!.GetValue<int>();
                    data.Add(line - previousLine);
                    data.Add(line == previousLine ? column - previousColumn : column);
                    data.Add(tokenEnd - segment);
                    data.Add(kind);
                    data.Add(0);
                    previousLine = line;
                    previousColumn = column;
                }
                segment = lineEnd + 1;
                if (lineEnd < end && document.Text[lineEnd] == '\r' && segment < end && document.Text[segment] == '\n')
                {
                    segment++;
                }
            }
        }
        return new JsonObject { ["data"] = data };
    }

    internal static JsonArray Diagnostics(LanguageCatalog catalog, FleckDocument document, string? sourceUri = null)
    {
        var diagnostics = new JsonArray();
        void Add(int start, int end, string code, string message, int severity = 1)
        {
            diagnostics.Add(new JsonObject { ["range"] = document.Range(start, end), ["severity"] = severity, ["code"] = code, ["source"] = "sunfleck", ["message"] = message });
        }
        foreach (var typeError in catalog.ComponentTypeErrors) {
            Add(0, Math.Min(1, document.Text.Length), typeError.Id, typeError.GetMessage());
            if (typeError.Location.IsInSource) {
                var position = typeError.Location.GetLineSpan();
                var start = position.StartLinePosition;
                diagnostics[^1]!["relatedInformation"] = new JsonArray(new JsonObject {
                    ["message"] = typeError.GetMessage(), ["location"] = new JsonObject {
                        ["uri"] = RuntimeModels.SourceUri(position.Path),
                        ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = start.Line, ["character"] = start.Character }, ["end"] = new JsonObject { ["line"] = start.Line, ["character"] = start.Character + 1 } }
                    }
                });
            }
        }
        // Resolve local tag names before registering the dependency graph. A
        // resource-registration exception may only carry a document-level location.
        var unknownTags = document.Theme ? [] : document.Tags.Where(tag => !tag.Closing && tag.Complete &&
            tag.Name.Length > 0 && catalog.Element(document, tag) is null).ToArray();
        foreach (var tag in unknownTags)
        {
            Add(tag.NameStart, tag.NameStart + tag.Name.Length, "unknown_element",
                catalog.Localize($"未找到标签“{tag.Name}”的定义。", $"Unknown element '{tag.Name}'."));
        }
        if (unknownTags.Length == 0 && catalog.ValidateComponents(document) is { } error)
        {
            // Legacy native diagnostics use Unicode scalar columns; LSP uses UTF-16.
            int start = NativeOffset(document.Text, error.Line, error.Column);
            var location = System.Text.RegularExpressions.Regex.Match(error.Message, @"^(.*):(\d+):(\d+): (.*)");
            JsonObject? related = null;
            if (location.Success && Path.IsPathFullyQualified(location.Groups[1].Value)) {
                string path = location.Groups[1].Value;
                string uri = RuntimeModels.SourceUri(path);
                string? currentPath = sourceUri is null ? null : RuntimeModels.SourcePath(sourceUri);
                bool sameFile = currentPath is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (sameFile) {
                    start = NativeOffset(document.Text, uint.Parse(location.Groups[2].Value), uint.Parse(location.Groups[3].Value));
                } else {
                    int line = Math.Max(0, int.Parse(location.Groups[2].Value) - 1);
                    int column = Math.Max(0, int.Parse(location.Groups[3].Value) - 1);
                    JsonObject range = new() { ["start"] = new JsonObject { ["line"] = line, ["character"] = column }, ["end"] = new JsonObject { ["line"] = line, ["character"] = column + 1 } };
                    try {
                        var referenced = new FleckDocument(File.ReadAllText(path));
                        int target = NativeOffset(referenced.Text, (uint)line + 1, (uint)column + 1);
                        var targetSpan = DiagnosticSpan(referenced, target);
                        range = referenced.Range(targetSpan.Start, targetSpan.End);
                    } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    related = new JsonObject { ["location"] = new JsonObject { ["uri"] = uri, ["range"] = range }, ["message"] = location.Groups[4].Value };
                    start = document.Tags.FirstOrDefault(tag => !tag.Closing && catalog.ComponentElement(tag) is not null)?.NameStart ?? 0;
                }
            }
            var span = DiagnosticSpan(document, start);
            Add(span.Start, span.End, "fleck_" + error.Code, error.Message);
            if (related is not null) { diagnostics[^1]!["relatedInformation"] = new JsonArray(related); }
        }
        bool uncertain = false;
        foreach (var tag in document.Tags.Where(tag => !tag.Closing))
        {
            // Validate each local attribute even when a dependency/native parser
            // has already reported an earlier error. Keep diagnostics on the attribute.
            if (!document.Theme && catalog.Element(document, tag) is not null) {
                foreach (var attribute in tag.Attributes.Where(a => a.Closed && a.Name != "xmlns" && !a.Name.StartsWith("xmlns:", StringComparison.Ordinal))) {
                    var schema = catalog.Attribute(document, tag, attribute.Name);
                    if (schema is null) {
                        Add(attribute.Start, attribute.Start + attribute.Name.Length, "unknown_attribute", $"Unknown property '{attribute.Name}' on {tag.Name}.");
                        continue;
                    }
                    if (tag.Name == "DataTrigger" && attribute.Name == "Binding" && Binding(attribute) is not null) continue;
                    if (attribute.Value.StartsWith("{StaticResource ", StringComparison.Ordinal) ||
                        attribute.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal)) continue;
                    if (attribute.Value.StartsWith('{') && !attribute.Value.StartsWith("{}", StringComparison.Ordinal)) {
                        if (schema["binding"]?.GetValue<bool>() != true) {
                            Add(attribute.Start, attribute.End, "property_not_bindable", $"{tag.Name}.{attribute.Name} does not support binding.");
                        }
                        continue;
                    }
                    if (schema["autoValue"]?.GetValue<string>() == attribute.Value) { continue; }
                    if (schema["values"] is JsonArray choices && choices.Count > 0 && !choices.Any(v => v?.GetValue<string>() == attribute.Value) &&
                        !(long.TryParse(attribute.Value, out var fontEnum) && (attribute.Name switch { "FontWeight" => fontEnum is >= 1 and <= 999, "FontStyle" or "TextTrimming" => fontEnum is >= 0 and <= 2, "FontStretch" => fontEnum is >= 1 and <= 9, "TextAlignment" or "TextDecorations" => fontEnum is >= 0 and <= 3, "TextWrapping" or "FlowDirection" => fontEnum is >= 0 and <= 1, _ => false }))) {
                        Add(attribute.ValueStart, attribute.End, "invalid_property_value", $"Invalid value for {tag.Name}.{attribute.Name}; expected {string.Join(", ", choices.Select(v => v!.GetValue<string>()))}.");
                    }
                    if (attribute.Name == "Margin") {
                        var values = attribute.Value.Split(',');
                        if (values.Length is not (1 or 2 or 4) || values.Any(v => !double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < -4096 || n > 4096)) {
                            Add(attribute.ValueStart, attribute.End, "invalid_property_value", "Margin requires 1, 2 or 4 finite numbers in the range -4096..4096.");
                        }
                    }
                    if (schema["type"]?.GetValue<string>() == "length") {
                        if (!double.TryParse(attribute.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var length) || !double.IsFinite(length) || length < 0 || length > 10000000) {
                            Add(attribute.ValueStart,attribute.End,"invalid_property_value",$"{attribute.Name} requires a finite nonnegative length or its declared Auto/Infinity value.");
                        }
                    }
                    if (schema["type"]?.GetValue<string>() == "number") {
                        bool valid = double.TryParse(attribute.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value);
                        if (!valid || schema["minimum"] is JsonValue min && value < min.GetValue<double>() ||
                            schema["maximum"] is JsonValue max && value > max.GetValue<double>()) {
                            Add(attribute.ValueStart, attribute.End, "invalid_property_value", $"Invalid numeric value for {tag.Name}.{attribute.Name}.");
                        }
                    }
                    if (schema["type"]?.GetValue<string>() == "integer") {
                        bool valid = long.TryParse(attribute.Value, System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture, out long value);
                        if (!valid || schema["minimum"] is JsonValue min && value < min.GetValue<long>() ||
                            schema["maximum"] is JsonValue max && value > max.GetValue<long>()) {
                            Add(attribute.ValueStart, attribute.End, "invalid_property_value", $"Invalid integer value for {tag.Name}.{attribute.Name}.");
                        }
                    } else if (schema["type"]?.GetValue<string>() == "bool" && attribute.Value is not ("true" or "false")) {
                        Add(attribute.ValueStart, attribute.End, "invalid_property_value", $"{tag.Name}.{attribute.Name} requires true or false.");
                    }
                }
            }
            foreach (string axis in new[] { "Width", "Height" }) {
                if (tag.Find("Min" + axis) is { } minimum && tag.Find("Max" + axis) is { } maximum &&
                    double.TryParse(minimum.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double low) && double.TryParse(maximum.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double high) && low > high) {
                    Add(maximum.Start, maximum.End, "invalid_size_constraints", "Min" + axis + " cannot exceed Max" + axis + ".");
                }
            }
            foreach (var attribute in tag.Attributes.Where(attribute => attribute.Closed && attribute.Value.EndsWith('}')))
            {
                var parts = Binding(attribute);
                if (parts?.FirstOrDefault(part => part.Name == "Path") is not { } path)
                {
                    continue;
                }
                if (parts.Any(part => part.Name is "RelativeSource" or "ElementName")) { continue; }
                var (model, uncertainty) = catalog.BindingModel(document, tag, attribute.Name);
                if (uncertainty is not null && !uncertain)
                {
                    Add(path.Start, path.Start + path.Value.Length, "model_uncertain", uncertainty, 3);
                    uncertain = true;
                }
                if (model is null)
                {
                    continue;
                }
                var property = model["properties"]!.AsArray().FirstOrDefault(property => property!["path"]!.GetValue<string>() == path.Value)?.AsObject();
                int severity = uncertainty is null ? 1 : 3;
                if (property is null)
                {
                    if (path.Value.Contains('.') && model["properties"]!.AsArray().Any(p => p?["path"]?.GetValue<string>() == path.Value.Split('.')[0] && p?["type"]?.GetValue<string>() == "model")) continue;
                    Add(path.Start, path.Start + path.Value.Length, "path_missing", catalog.Localize($"模型 {document.Model} 未导出路径 {path.Value}。", $"Model {document.Model} does not export path {path.Value}."), severity);
                    continue;
                }
                string mode = Option(parts, "Mode", "Default");
                if (parts.Any(part => part.Name == "Converter")) continue;
                bool twoWay = mode == "TwoWay" || mode == "Default" && catalog.Attribute(document, tag, attribute.Name)?["defaultMode"]?.GetValue<string>() == "TwoWay";
                if (twoWay && property["writable"]?.GetValue<bool>() != true)
                {
                    Add(path.Start, path.Start + path.Value.Length, "read_only", catalog.Localize($"TwoWay 绑定需要可写路径；{path.Value} 为只读。", $"TwoWay binding requires a writable path. {path.Value} is read-only."), severity);
                }
                else if (attribute.Name == "Command" && property["type"]?.GetValue<string>() == "command" && !CommandSupports(tag, property))
                {
                    Add(path.Start, path.Start + path.Value.Length, "command_mismatch", catalog.Localize(
                        $"命令 {path.Value} 不支持 {tag.Find("CommandMode")?.Value} 模式。", $"Command {path.Value} does not support {tag.Find("CommandMode")?.Value} mode."), severity);
                }
                else if (!Compatible(catalog, document, tag, attribute, property, twoWay))
                {
                    Add(path.Start, path.Start + path.Value.Length, "type_mismatch", catalog.Localize($"{property["type"]} 类型不能绑定到 {tag.Name}.{attribute.Name}。", $"Type {property["type"]} cannot bind to {tag.Name}.{attribute.Name}."), severity);
                }
            }
        }
        return diagnostics;
    }

    private static (int Start, int End) DiagnosticSpan(FleckDocument document, int offset)
    {
        var tag = document.Tags.FirstOrDefault(tag => tag.Name.Length > 0 && offset >= tag.Start && offset <= tag.NameStart + tag.Name.Length);
        return tag is not null ? (tag.NameStart, tag.NameStart + tag.Name.Length) :
            (offset, Math.Min(offset + (offset < document.Text.Length && char.IsHighSurrogate(document.Text[offset]) ? 2 : 1), document.Text.Length));
    }

    private static int NativeOffset(string text, uint line, uint column)
    {
        int offset = 0;
        for (uint current = 1; current < line && offset < text.Length; current++)
        {
            while (offset < text.Length && text[offset] is not ('\r' or '\n'))
            {
                offset++;
            }
            if (offset < text.Length && text[offset++] == '\r' && offset < text.Length && text[offset] == '\n')
            {
                offset++;
            }
        }
        for (uint current = 1; current < column && offset < text.Length && text[offset] is not ('\r' or '\n'); current++)
        {
            offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
        }
        return offset;
    }
}
