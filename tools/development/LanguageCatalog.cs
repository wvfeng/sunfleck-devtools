using System.Text.Json;
using System.Text.Json.Nodes;
using Sunfleck;

namespace Sunfleck.Development;

internal sealed partial class LanguageCatalog
{
    internal bool English { get; set; }
    internal string Localize(string chinese, string english) => English ? english : chinese;
    internal JsonObject Schema { get; }
    private readonly Dictionary<string, JsonObject> elements;
    private readonly Dictionary<string, JsonObject> documents;
    private readonly Dictionary<string, (JsonObject Data, bool Stale)> models = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguous = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, JsonObject?>? runtimeModels;
    private JsonObject[] dynamicModels = [];
    private string[] modelFiles = [];
    internal List<string> Errors { get; } = [];

    internal LanguageCatalog()
    {
        Schema = JsonNode.Parse(App.GetFleckSchema())!.AsObject();
        elements = Schema["elements"]!.AsArray().ToDictionary(node => node!["name"]!.GetValue<string>(), node => node!.AsObject(), StringComparer.Ordinal);
        documents = Schema["documents"]!.AsArray().ToDictionary(node => node!["name"]!.GetValue<string>(), node => node!.AsObject(), StringComparer.Ordinal);
        AttachBuiltInTypes();
    }

    // A query view shares the configured catalog; runtime overlays never mutate it.
    private LanguageCatalog(LanguageCatalog basis, IReadOnlyDictionary<string, JsonObject?> runtime)
    {
        Schema = basis.Schema;
        elements = basis.elements;
        componentSources = basis.componentSources; componentTypes = basis.componentTypes; componentManifests = basis.componentManifests;
        componentAttributeSources = basis.componentAttributeSources;
        documents = basis.documents;
        models = basis.models;
        ambiguous = basis.ambiguous;
        English = basis.English;
        runtimeModels = runtime;
    }

    internal LanguageCatalog WithRuntime(IReadOnlyDictionary<string, JsonObject?> runtime) => new(this, runtime);

    internal IEnumerable<JsonObject> TriggerSources(FleckDocument.Tag tag) {
        for (var owner = tag.Parent; owner is not null; owner = owner.Parent) {
            if (owner.Name == "Style" && owner.Find("TargetType") is { } type &&
                elements.TryGetValue(type.Value, out var element))
                return element["attributes"]!.AsArray().OfType<JsonObject>().Where(p => p["triggerSource"]?.GetValue<bool>() == true);
        }
        return [];
    }

    internal JsonObject? Element(FleckDocument document, FleckDocument.Tag tag)
    {
        if (ComponentElement(tag) is { } component) { return component; }
        string name = tag.Name;
        int colon = name.IndexOf(':');
        if (colon >= 0) {
            if (!Namespaces(tag).TryGetValue(name[..colon], out var ns) || ns != "urn:sunfleck:v1") { return null; }
            name = name[(colon + 1)..];
        }
        if (tag.Parent is null && name == "ResourceDictionary") return Schema["styleObjects"]?[name]?.AsObject();
        if (document.InTemplate(tag))
        {
            var template = Schema["templateElements"]!;
            if (template["elements"]?.AsArray().FirstOrDefault(element => element?["name"]?.GetValue<string>() == name) is JsonObject element)
            {
                return element;
            }
            return template["names"]!.AsArray().Any(name => name!.GetValue<string>() == tag.Name)
                ? new JsonObject { ["name"] = tag.Name, ["attributes"] = template["attributes"]!.DeepClone(), ["children"] = template["names"]!.DeepClone() } : null;
        }
        if (tag.Parent is { } parent && Schema["transformObjects"] is JsonObject transforms)
        {
            if (name == parent.Name + ".RenderTransform" || name == parent.Name + ".LayoutTransform")
            {
                return new JsonObject { ["name"] = name };
            }
            if (IsTransformContainer(parent) && transforms[name] is JsonObject transform)
            {
                return transform;
            }
        }
        if (tag.Parent is { } container && Schema["itemObjects"] is JsonObject itemObjects)
        {
            if (name == "Binding" && container.Name == "MultiBinding" ||
                name == "MultiBinding" && container.Name.Contains('.')) return itemObjects[name]?.AsObject();
            if (name.StartsWith(container.Name + ".",StringComparison.Ordinal) &&
                Attribute(document,container,name[(container.Name.Length+1)..])?["binding"]?.GetValue<bool>() == true)
                return new JsonObject { ["name"] = name };
            if (name == "DataGrid.Columns" && container.Name == "DataGrid") return new JsonObject { ["name"] = name };
            if (name == "DataGridTextColumn" && container.Name == "DataGrid.Columns" ||
                name == "HierarchicalDataTemplate" && container.Name == "TreeView.ItemTemplate" ||
                name == "DataTemplate" && container.Name.EndsWith(".ItemTemplateSelector",StringComparison.Ordinal))
                return itemObjects[name]?.AsObject();
        }
        if (tag.Parent is { } itemOwner &&
            (name == itemOwner.Name + ".ItemTemplate" && itemOwner.Name is "ListView" or "ComboBox" or "ItemsControl" or "TreeView" or "DataGrid" ||
             name == itemOwner.Name + ".ItemTemplateSelector" && itemOwner.Name is "ListView" or "ItemsControl" ||
             name == "DataTemplate" && itemOwner.Name.EndsWith(".ItemTemplateSelector",StringComparison.Ordinal) ||
             name is "DataTemplate" or "HierarchicalDataTemplate" && itemOwner.Name.EndsWith(".ItemTemplate",StringComparison.Ordinal)))
            return new JsonObject { ["name"] = name };
        if (tag.Parent is { } visual && Schema["drawingObjects"] is JsonObject drawings)
        {
            if (DrawingChildren(visual.Name).Contains(name)) { return drawings[name]?.AsObject(); }
            if (name.StartsWith(visual.Name + ".", StringComparison.Ordinal) && DrawingChildren(name).Length > 0)
            {
                return new JsonObject { ["name"] = name };
            }
        }
        if (tag.Parent is { } owner && Schema["animationObjects"] is JsonObject animations)
        {
            if (name == owner.Name + ".Storyboard" || name == owner.Name + ".VisualStateGroups" || name == owner.Name + ".InputBindings" || name == owner.Name + ".CommandBindings") { return new JsonObject { ["name"] = name }; }
            if (owner.Name.EndsWith(".InputBindings", StringComparison.Ordinal) && name == "PointerBinding") return animations[name]?.AsObject();
            if (AnimationChild(owner.Name) == name) { return animations[name]?.AsObject(); }
        }
        if (tag.Parent is { } styleOwner && Schema["styleObjects"] is JsonObject styles)
        {
            if (styleOwner.Name == "Style" && name == "Style.Triggers" || styleOwner.Name == "MultiTrigger" && name == "MultiTrigger.Conditions") return new JsonObject { ["name"] = name };
            if (styleOwner.Name is "Style" or "Style.Triggers" && name is "Trigger" or "MultiTrigger" or "DataTrigger") return styles[name]?.AsObject();
            if (name == styleOwner.Name + ".Resources" && !document.Theme)
                return new JsonObject { ["name"] = name };
            if (styleOwner.Name == "ResourceDictionary" && name == "ResourceDictionary.MergedDictionaries")
                return new JsonObject { ["name"] = name };
            if (styleOwner.Name == "ResourceDictionary.MergedDictionaries" && name == "ResourceDictionary") return styles[name]?.AsObject();
            if ((styleOwner.Name.EndsWith(".Resources", StringComparison.Ordinal) || styleOwner.Name == "ResourceDictionary") && styles[name] is JsonObject resource) return resource;
            if (StyleChild(styleOwner.Name) == name) return styles[name]?.AsObject();
        }
        return documents.GetValueOrDefault(name) ?? elements.GetValueOrDefault(name);
    }

    private static bool IsTransformContainer(FleckDocument.Tag tag) =>
        tag.Name == "TransformGroup" || tag.Name.EndsWith(".RenderTransform", StringComparison.Ordinal) ||
        tag.Name.EndsWith(".LayoutTransform", StringComparison.Ordinal);

    private static string? StyleChild(string parent) => parent switch
    {
        "Window.Resources" or "UserControl.Resources" or "ControlTemplate.Resources" => "Style",
        "Style" or "Trigger" or "MultiTrigger" or "DataTrigger" => "Setter",
        "MultiTrigger.Conditions" => "Condition",
        _ => null
    };

    private static string? AnimationChild(string parent) => parent switch
    {
        _ when parent.EndsWith(".CommandBindings", StringComparison.Ordinal) => "CommandBinding",
        _ when parent.EndsWith(".InputBindings", StringComparison.Ordinal) => "KeyBinding",
        "VisualStateGroup" => "VisualState",
        "VisualState" => "Storyboard",
        _ when parent.EndsWith(".VisualStateGroups", StringComparison.Ordinal) => "VisualStateGroup",
        "Storyboard" => "Timeline",
        "Timeline" => "KeyFrame",
        _ when parent.EndsWith(".Storyboard", StringComparison.Ordinal) => "Storyboard",
        _ => null
    };

    private static string[] DrawingChildren(string name)
    {
        if (name is "LinearGradientBrush" or "RadialGradientBrush") { return ["GradientStop"]; }
        int dot = name.LastIndexOf('.');
        return dot < 0 ? [] : name[(dot + 1)..] switch
        {
            "Effect" => ["BlurEffect", "DropShadowEffect"],
            "Fill" or "Stroke" or "Background" or "BorderBrush" or "OpacityMask" =>
                ["SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "ImageBrush"],
            _ => []
        };
    }

    internal IEnumerable<string> Children(FleckDocument document, FleckDocument.Tag? parent)
    {
        if (parent is null)
        {
            return ["Window", "UserControl", "ControlTemplate", "Theme", "ResourceDictionary"];
        }
        if (parent.Name == "DataGrid.Columns") return ["DataGridTextColumn"];
        if (parent.Name == "MultiBinding") return ["Binding"];
        if (parent.Parent is { } owner && parent.Name.StartsWith(owner.Name + ".",StringComparison.Ordinal) &&
            Attribute(document,owner,parent.Name[(owner.Name.Length+1)..])?["binding"]?.GetValue<bool>() == true &&
            DrawingChildren(parent.Name).Length == 0 && !IsTransformContainer(parent)) return ["MultiBinding"];
        if (parent.Name == "TreeView.ItemTemplate") return ["HierarchicalDataTemplate"];
        if (parent.Name.EndsWith(".ItemTemplateSelector",StringComparison.Ordinal)) return ["DataTemplate"];
        if (parent.Name.EndsWith(".ItemTemplate",StringComparison.Ordinal)) return ["DataTemplate"];
        if (parent.Name == "HierarchicalDataTemplate") return ["TextBlock"];
        if (parent.Name == "DataTemplate") return elements.Keys.Concat(ComponentNames(parent));
        if (parent.Name is "Window" or "UserControl" or "ControlTemplate")
        {
            return elements.Keys.Where(name => name is not ("DockSplit" or "DockGroup" or "DockDocument" or "DockTool")).Concat(ComponentNames(parent)).Append(parent.Name + ".Resources");
        }
        if (IsTransformContainer(parent) && Schema["transformObjects"] is JsonObject transforms)
        {
            return transforms.Select(pair => pair.Key);
        }
        if (parent.Name.EndsWith(".Resources", StringComparison.Ordinal)) return ["Style", "Resource", "ResourceDictionary"];
        if (parent.Name == "ResourceDictionary") return ["ResourceDictionary.MergedDictionaries", "Style", "Resource"];
        if (parent.Name == "ResourceDictionary.MergedDictionaries") return ["ResourceDictionary"];
        if (parent.Name == "Style") return ["Setter", "Style.Triggers", "Trigger", "MultiTrigger", "DataTrigger"];
        if (parent.Name == "Style.Triggers") return ["Trigger", "MultiTrigger", "DataTrigger"];
        if (parent.Name == "MultiTrigger") return ["MultiTrigger.Conditions", "Setter"];
        if (StyleChild(parent.Name) is { } styleChild) return [styleChild];
        if (parent.Name.EndsWith(".InputBindings", StringComparison.Ordinal)) return ["KeyBinding","PointerBinding"];
        if (AnimationChild(parent.Name) is { } child) { return [child]; }
        var drawing = DrawingChildren(parent.Name);
        if (drawing.Length > 0) { return drawing; }
        var children = Element(document, parent)?["children"]?.AsArray().Select(node => node!.GetValue<string>()).ToArray() ?? [];
        IEnumerable<string> result = children;
        if (parent.Name is "ListView" or "ComboBox" or "ItemsControl" or "TreeView" or "DataGrid") result = result.Append(parent.Name + ".ItemTemplate");
        if (parent.Name == "DataGrid") result = result.Append("DataGrid.Columns");
        if (parent.Name is "ListView" or "ItemsControl") result = result.Append(parent.Name + ".ItemTemplateSelector");
        if (children.Length > 0 && !document.Theme) { result = result.Concat(ComponentNames(parent)); }
        if (!document.Theme && Attribute(document, parent, "RenderTransform") is not null)
        {
            result = result.Concat([parent.Name + ".Resources", parent.Name + ".RenderTransform", parent.Name + ".LayoutTransform",
                parent.Name + ".Effect", parent.Name + ".OpacityMask", parent.Name + ".Storyboard", parent.Name + ".VisualStateGroups", parent.Name + ".InputBindings", parent.Name + ".CommandBindings"]);
            if (parent.Name == "Border") { result = result.Concat(["Border.Background", "Border.BorderBrush"]); }
            if (parent.Name is "Rectangle" or "Ellipse" or "Line" or "Path")
            {
                result = result.Concat([parent.Name + ".Fill", parent.Name + ".Stroke"]);
            }
        }
        return result;
    }

    internal JsonObject? Attribute(FleckDocument document, FleckDocument.Tag tag, string name)
    {
        if (document.Theme && tag.Name == "Resource" && Schema["themeResources"] is JsonObject resources)
        {
            if (name == "Key")
            {
                var values = resources["colors"]!.AsArray().Select(value => value!.GetValue<string>())
                    .Concat(resources["metrics"]!.AsArray().Select(metric => metric!["name"]!.GetValue<string>()));
                return new JsonObject { ["name"] = "Key", ["type"] = "string", ["required"] = true,
                    ["source"] = Element(document, tag)?["attributes"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == name)?["source"]?.DeepClone(),
                    ["values"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
            }
            if (name == "Value" && tag.Find("Key") is { } key)
            {
                if (resources["metrics"]!.AsArray().FirstOrDefault(metric => metric!["name"]!.GetValue<string>() == key.Value) is JsonObject metric)
                {
                    var value = metric.DeepClone().AsObject();
                    value["name"] = "Value";
                    value["source"] = Element(document, tag)?["attributes"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == name)?["source"]?.DeepClone();
                    return value;
                }
            }
        }
        if (tag.Name == "Style" && name == "x:Key") name = "Key";
        return Element(document, tag)?["attributes"]?.AsArray().FirstOrDefault(attribute => attribute!["name"]!.GetValue<string>() == name)?.AsObject();
    }

    internal void Configure(JsonNode? files)
    {
        modelFiles = files?.AsArray().Select(node => Path.GetFullPath(node!.GetValue<string>())).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        Reload();
    }

    internal void Reload()
    {
        models.Clear();
        ambiguous.Clear();
        Errors.Clear();
        foreach (string file in modelFiles)
        {
            try
            {
                if (new FileInfo(file).Length > 8 * 1024 * 1024)
                {
                    throw new InvalidDataException("Model metadata exceeds 8 MiB.");
                }
                var root = JsonNode.Parse(File.ReadAllText(file))!;
                if (root?["version"]?.GetValue<int>() != 1)
                {
                    throw new InvalidDataException("Unsupported model metadata version.");
                }
                string? assembly = root["assembly"]?.GetValue<string>();
                bool stale = assembly is not null && (!File.Exists(assembly) || File.GetLastWriteTimeUtc(assembly) > File.GetLastWriteTimeUtc(file));
                var entries = root?["models"] as JsonArray ?? throw new InvalidDataException("Model metadata requires a models array.");
                foreach (var entry in entries)
                {
                    ValidateModel(entry);
                }
                foreach (var model in entries)
                {
                    bool sourceChanged = model!["properties"]!.AsArray().Any(property =>
                    {
                        string? source = property?["source"]?["uri"]?.GetValue<string>();
                        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile &&
                            File.Exists(uri.LocalPath) && File.GetLastWriteTimeUtc(uri.LocalPath) > File.GetLastWriteTimeUtc(file);
                    });
                    Add(model.AsObject(), stale || sourceChanged);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                Errors.Add($"{file}: {error.Message}");
            }
        }
        foreach (var model in dynamicModels)
        {
            string name = model["name"]!.GetValue<string>();
            models[name] = (model, false);
            ambiguous.Remove(name);
        }
    }

    internal void Add(JsonObject model, bool stale = false)
    {
        ValidateModel(model);
        string name = model["name"]!.GetValue<string>();
        _ = model["properties"]!.AsArray();
        if (!models.TryAdd(name, (model, stale)))
        {
            ambiguous.Add(name);
        }
    }

    internal static void ValidateModel(JsonNode? model)
    {
        if (model is not JsonObject || model["name"] is not JsonValue name || !name.TryGetValue<string>(out var identifier) || string.IsNullOrWhiteSpace(identifier) || model["properties"] is not JsonArray properties)
        {
            throw new InvalidDataException("Each model requires a name and a properties array.");
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (property is not JsonObject || property["path"] is not JsonValue path || !path.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text) || !paths.Add(text) ||
                property["type"]?.GetValue<string>() is not ("string" or "bool" or "integer" or "number" or "command" or "model") ||
                property["writable"] is not JsonValue writable || !writable.TryGetValue<bool>(out _))
            {
                throw new InvalidDataException("Invalid or duplicate model property.");
            }
            if (property["commandModes"] is { } commandModes && (property["type"]!.GetValue<string>() != "command" ||
                commandModes is not JsonArray modes || modes.Count == 0 || modes.Any(mode => mode?.GetValue<string>() is not ("Sync" or "Async")) ||
                modes.Select(mode => mode!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != modes.Count))
            {
                throw new InvalidDataException("Command modes must be a nonempty, distinct Sync/Async array on a command property.");
            }
        }
    }

    internal void SetDynamic(JsonArray entries)
    {
        foreach (var entry in entries)
        {
            ValidateModel(entry);
        }
        var updated = entries.Select(entry => entry!.DeepClone().AsObject()).ToArray();
        if (updated.Select(entry => entry["name"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != updated.Length)
        {
            throw new ArgumentException("Duplicate dynamic model name.");
        }
        dynamicModels = updated;
        Reload();
    }

    internal (JsonObject? Data, string? Uncertainty) BindingModel(FleckDocument document, FleckDocument.Tag tag, string attribute)
    {
        // DataContext itself reads its parent context. Other bindings read the
        // context selected on this element. A model handle carries no static
        // child-model schema; never diagnose it using the page's unrelated type.
        for (var scope = attribute == "DataContext" ? tag.Parent : tag; scope is not null; scope = scope.Parent) {
            if (scope.Name is "DataTemplate" or "HierarchicalDataTemplate" || scope.Find("DataContext") is not null)
                return (null, Localize("此绑定使用局部 DataContext；嵌套模型元数据尚不可用，路径将在运行时验证。",
                    "This binding uses a local DataContext. Nested model metadata is unavailable; the path is validated at runtime."));
        }
        return Model(document.Model);
    }
    internal (JsonObject? Data, string? Uncertainty) Model(string? name)
    {
        if (name is not null && runtimeModels?.TryGetValue(name, out var runtime) == true)
        {
            return runtime is not null ? (runtime, null) :
                (null, Localize("当前源文件关联的运行时模型元数据存在冲突。", "Conflicting runtime model metadata for this source file."));
        }
        if (name is null || !models.TryGetValue(name, out var model))
        {
            return (null, Localize("未找到模型元数据；请导出模型并配置 modelFiles。", "Model metadata not found. Export the model and configure modelFiles."));
        }
        if (ambiguous.Contains(name))
        {
            return (null, Localize("存在同名模型元数据，请在 modelFiles 中选择当前项目的模型。", "Multiple metadata files export this model. Select the current project's metadata in modelFiles."));
        }
        return (model.Data, model.Stale ? Localize("模型元数据可能已过期，请重新构建。", "Model metadata may be stale. Rebuild the project.") : null);
    }
}
