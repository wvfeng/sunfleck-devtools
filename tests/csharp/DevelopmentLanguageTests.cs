using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Sunfleck.Development;

internal sealed class ExportedModel : Exception
{
    public string NeverRead => throw new InvalidOperationException("The exporter must not call getters.");
    public int ReadOnly => 7;
    public string Writable { get; set; } = "";
    [Sunfleck.CommandModes(Sunfleck.CommandExecutionModes.Sync)]
    public Sunfleck.Command SyncCommand => throw new InvalidOperationException("Command export must not call getters.");
    public Sunfleck.Command UnknownCommand => throw new InvalidOperationException("Command export must not call getters.");
}

internal static class DevelopmentLanguageTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
    private static JsonObject Model() => JsonNode.Parse("""
        {"version":1,"name":"TestModel","properties":[
        {"path":"Name","type":"string","writable":true,"source":{"uri":"file:///model.cs","line":7,"column":4}},
        {"path":"ReadOnly","type":"string","writable":false},
        {"path":"Options.Selected","type":"integer","writable":true},
        {"path":"Enabled","type":"bool","writable":true},
        {"path":"Run","type":"command","writable":false}]}
        """)!.AsObject();
    private static string[] Labels(JsonArray items) => items.Select(item => item!["label"]!.GetValue<string>()).ToArray();
    private static string[] Codes(JsonArray diagnostics) => diagnostics.Select(item => item!["code"]!.GetValue<string>()).ToArray();

    internal static void Run()
    {
        DebugInspectorTests.Run();
        var catalog = new LanguageCatalog();
        catalog.Add(Model());
        void Completes(string text, string expected)
        {
            var document = new FleckDocument(text);
            Check(Labels(LanguageFeatures.Complete(catalog, document, text.Length)).Contains(expected), $"Missing completion {expected}: {text}");
        }
        Completes("<Win", "Window");
        Completes("<Window xmlns='urn:sunfleck:v1'><StackPanel><Bu", "Button");
        Completes("<Window xmlns='urn:sunfleck:v1'><Button IsE", "IsEnabled");
        Completes("<Window xmlns='urn:sunfleck:v1'><Button IsEnabled='t", "true");
        Completes("<Window xmlns='urn:sunfleck:v1' Model='TestModel'><TextBox Text='{Binding N", "Name");
        Completes("<Window xmlns='urn:sunfleck:v1' Model='TestModel'><TextBlock Text='{Binding Options.", "Options.Selected");
        Completes("<Window xmlns='urn:sunfleck:v1'><Button Content='{Binding Name, Mo", "Mode=");
        Completes("<Window xmlns='urn:sunfleck:v1'><Button Content='{Binding Name, Mode=One", "OneWay");
        Completes("<Theme Base='Flu", "Fluent2");
        Completes("<Theme><DockTemplate Part='Ti", "Title");
        Completes("<Theme><DockTemplate Part='Title'><Button Ro", "Role");
        Completes("<Window><StackPanel></Sta", "StackPanel");
        Completes("<!-- <Theme -->\r\n<Window><Button Con", "Content");

        // Inserting before an auto-generated closing tag must query the unfinished
        // opening tag, including at the exact boundary before the following '<'.
        foreach (var (prefix, suffix, expected) in new[] {
            ("<Window><StackP", "</Window>", "StackPanel"),
            ("<Window><StackPanel><But", "</StackPanel></Window>", "Button"),
            ("<Theme><DockT", "</Theme>", "DockTemplate"),
            ("<Theme><DockTemplate Pa", "</Theme>", "Part"),
            ("<Window><Button IsEnabled='tr", "</Window>", "true") })
        {
            var boundary = new FleckDocument(prefix + suffix);
            Check(Labels(LanguageFeatures.Complete(catalog, boundary, prefix.Length)).Contains(expected),
                $"Completion at a following tag boundary failed: {prefix}|{suffix}");
        }
        foreach (string text in new[] { "<Window/>", "</Window>", "<Window><Button/></Window>" })
        {
            var boundary = new FleckDocument(text);
            for (int offset = 0; offset <= text.Length; offset++)
            {
                // Query every caret boundary, including '<' and between '</'.
                _ = LanguageFeatures.Complete(catalog, boundary, offset);
            }
        }

        var partial = new FleckDocument("<Window Model='TestModel'><TextBox Text='{Binding R");
        Check(!Labels(LanguageFeatures.Complete(catalog, partial, partial.Text.Length)).Contains("ReadOnly"), "Read-only TwoWay source suggested.");
        var command = new FleckDocument("<Window Model='TestModel'><Button Command='{Binding ");
        Check(Labels(LanguageFeatures.Complete(catalog, command, command.Text.Length)).SequenceEqual(["Run"]), "Command completion included non-commands.");

        const string xml = "<Window xmlns='urn:sunfleck:v1' Model='TestModel'>\r\n<StackPanel><TextBlock Text='中文🌱'/><TextBox Text='{Binding ReadOnly}'/><TextBlock Text='{Binding Missing}'/><Button Command='{Binding Name}'/><TextBlock Id='target' Text='{Binding Name}'/></StackPanel></Window>";
        var document = new FleckDocument(xml);
        var diagnostics = LanguageFeatures.Diagnostics(catalog, document);
        Check(Codes(diagnostics).Contains("read_only") && Codes(diagnostics).Contains("path_missing") && Codes(diagnostics).Contains("type_mismatch"), "Binding diagnostics missing.");
        var missing = diagnostics.Single(item => item!["code"]!.GetValue<string>() == "path_missing")!;
        int missingOffset = xml.IndexOf("Missing", StringComparison.Ordinal);
        Check(document.Offset(missing["range"]!["start"]!) == missingOffset, "UTF-16 semantic diagnostic range is wrong.");
        int nameOffset = xml.LastIndexOf("Name", StringComparison.Ordinal);
        var definition = LanguageFeatures.Definition(catalog, document, nameOffset + 2)!;
        Check(definition["uri"]!.GetValue<string>() == "file:///model.cs" && definition["range"]!["start"]!["line"]!.GetValue<int>() == 6, "Definition did not resolve model source.");
        Check(LanguageFeatures.Hover(catalog, document, nameOffset + 2) is not null, "Binding hover missing.");
        Check(LanguageFeatures.Symbols(document)[0]!["children"]![0]!["children"]!.AsArray().Count == 5, "Document hierarchy is wrong.");
        Check(LanguageFeatures.Tokens(document)["data"]!.AsArray().Count > 0, "Shared syntax tokens missing.");

        var invalid = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><Button Content='🌱' Typo='x'/></Window>");
        var native = LanguageFeatures.Diagnostics(catalog, invalid).First()!;
        Check(invalid.Offset(native["range"]!["start"]!) == invalid.Text.IndexOf("Typo", StringComparison.Ordinal), "Native scalar columns were not converted to UTF-16.");
        var incomplete = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><Button Content='{Binding Na");
        Check(LanguageFeatures.Diagnostics(catalog, incomplete).Count > 0, "Unclosed syntax diagnostic missing.");
        var open = new FleckDocument("<Button>");
        Check(LanguageFeatures.CloseTag(open, open.Text.Length)[0]!["newText"]!.GetValue<string>() == "</Button>", "Tag closing missing.");
        Check(LanguageFeatures.CloseTag(new FleckDocument("<Button>text</Button>"), open.Text.Length).Count == 0, "Duplicate closing tag inserted before existing content.");
        var attributeDocument = new FleckDocument("<Window><Button Con");
        var attributeCompletion = LanguageFeatures.Complete(catalog, attributeDocument, attributeDocument.Text.Length).Single(item => item!["label"]!.GetValue<string>() == "Content")!;
        Check(attributeCompletion["insertTextFormat"]!.GetValue<int>() == 2 && attributeCompletion["textEdit"]!["newText"]!.GetValue<string>() == "Content=\"$0\"", "Attribute completion did not position the caret inside its value.");
        var plainCompletion = LanguageFeatures.Complete(catalog, attributeDocument, attributeDocument.Text.Length, snippets: false).Single(item => item!["label"]!.GetValue<string>() == "Content")!;
        Check(plainCompletion["insertTextFormat"]!.GetValue<int>() == 1 && !plainCompletion["textEdit"]!["newText"]!.GetValue<string>().Contains('$'), "Client without snippets received snippet syntax.");

        var unknown = new LanguageCatalog();
        Check(Codes(LanguageFeatures.Diagnostics(unknown, document)).Contains("model_uncertain"), "Missing metadata did not report uncertainty.");
        Check(Labels(LanguageFeatures.Complete(unknown, new FleckDocument("<Bu"), 3)).Length == 0, "Invalid root element suggested.");
        unknown.Add(Model(), stale: true);
        Check(LanguageFeatures.Diagnostics(unknown, document).Where(item => item!["code"]!.GetValue<string>() == "path_missing").All(item => item!["severity"]!.GetValue<int>() == 3), "Stale metadata reported a definitive error.");
        unknown.SetDynamic(new JsonArray(Model()));
        unknown.SetDynamic(new JsonArray(Model()));
        Check(unknown.Model("TestModel").Data is not null && unknown.Model("TestModel").Uncertainty is null, "Dynamic model refresh became ambiguous or stale.");
        Protocol();
        VisualStudioDiscoveryProtocolTests.Run();
        ThemeSchema(catalog);
        CommandMetadata();
        Export();
    }

    private static void Protocol()
    {
        using (var input = new MemoryStream())
        using (var output = new MemoryStream())
        {
            void Send(string json) => LspServer.Write(input, JsonNode.Parse(json)!);
            Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            Send("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///中文.fleck","version":1,"text":"<Window xmlns='urn:sunfleck:v1'><Button/></Window>"}}}""");
            Send("""{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///中文.fleck","version":2},"contentChanges":[{"text":"<Window xmlns='urn:sunfleck:v1'><Button IsE"}]}}""");
            Send("""{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{"textDocument":{"uri":"file:///中文.fleck"},"position":{"line":0,"character":40}}}""");
            Send("""{"jsonrpc":"2.0","id":3,"method":"nonexistent","params":{}}""");
            Send("""{"jsonrpc":"2.0","id":4,"method":"shutdown"}""");
            Send("""{"jsonrpc":"2.0","method":"exit"}""");
            input.Position = 0;
            Check(new LspServer(input, output).Run() == 0, "LSP shutdown failed.");
            output.Position = 0;
            var messages = new List<JsonObject>();
            while (LspServer.Read(output) is { } message)
            {
                messages.Add(message);
            }
            Check(messages.Count == 6, "LSP message framing or notification response is wrong.");
            Check(messages[0]["result"]!["capabilities"]!["positionEncoding"]!.GetValue<string>() == "utf-16", "Encoding negotiation missing.");
            Check(Labels(messages.Single(message => message["id"]?.GetValue<int>() == 2)["result"]!.AsArray()).Contains("IsEnabled"), "Completion protocol failed.");
            Check(messages.Single(message => message["id"]?.GetValue<int>() == 3)["error"]!["code"]!.GetValue<int>() == -32601, "Unknown request handling failed.");
        }
        using (var malformed = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2147483647\r\n\r\n")))
        {
            try
            {
                LspServer.Read(malformed);
                throw new InvalidOperationException("Unbounded LSP message accepted.");
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    private static void ThemeSchema(LanguageCatalog catalog)
    {
        foreach (var metric in catalog.Schema["themeResources"]!["metrics"]!.AsArray())
        {
            string name = metric!["name"]!.GetValue<string>();
            int minimum = metric["minimum"]!.GetValue<int>(), maximum = metric["maximum"]!.GetValue<int>();
            string Theme(int value) => $"<Theme><Resource Key='{name}' Value='{value}'/></Theme>";
            Check(Sunfleck.App.ValidateFleck(Theme(minimum)) is null && Sunfleck.App.ValidateFleck(Theme(maximum)) is null,
                $"Exported resource bounds disagree with the parser: {name}.");
            Check(Sunfleck.App.ValidateFleck(Theme(minimum - 1)) is not null && Sunfleck.App.ValidateFleck(Theme(maximum + 1)) is not null,
                $"Parser accepted a resource outside its exported range: {name}.");
        }
        foreach (var attribute in catalog.Schema["templateElements"]!["attributes"]!.AsArray().Where(attribute => attribute?["minimum"] is not null))
        {
            string name = attribute!["name"]!.GetValue<string>();
            int minimum = attribute["minimum"]!.GetValue<int>(), maximum = attribute["maximum"]!.GetValue<int>();
            string Theme(int value) => $"<Theme><DockTemplate Part='Title'><Fill {name}='{value}'/></DockTemplate></Theme>";
            Check(Sunfleck.App.ValidateFleck(Theme(minimum)) is null && Sunfleck.App.ValidateFleck(Theme(maximum)) is null &&
                Sunfleck.App.ValidateFleck(Theme(minimum - 1)) is not null && Sunfleck.App.ValidateFleck(Theme(maximum + 1)) is not null,
                $"Exported template bounds disagree with the parser: {name}.");
        }
        foreach (var document in catalog.Schema["documents"]!.AsArray())
        {
            foreach (var attribute in document!["attributes"]!.AsArray().Where(attribute => attribute?["name"]?.GetValue<string>() is "Base" or "Part"))
            {
                foreach (var value in attribute!["values"]!.AsArray())
                {
                    string xml = attribute["name"]!.GetValue<string>() == "Base" ? $"<Theme Base='{value}'/>" :
                        $"<Theme><DockTemplate Part='{value}'><Fill/></DockTemplate></Theme>";
                    Check(Sunfleck.App.ValidateFleck(xml) is null, "Exported Theme enum rejected by parser: " + value);
                }
            }
        }
        string prefix = "<Theme><Resource Key='dock.float";
        Check(Labels(LanguageFeatures.Complete(catalog, new FleckDocument(prefix), prefix.Length)).SequenceEqual(["dock.floating_padding"]),
            "Theme resource completion is not driven by shared keys.");
        prefix = "<Theme><DockTemplate Part='Title' State='Active|Ho";
        var hover = LanguageFeatures.Complete(catalog, new FleckDocument(prefix), prefix.Length).Single(item => item!["label"]!.GetValue<string>() == "Hover")!;
        Check(hover["textEdit"]!["range"]!["start"]!["character"]!.GetValue<int>() == prefix.LastIndexOf('|') + 1 &&
            hover["textEdit"]!["newText"]!.GetValue<string>() == "Hover", "Combined template state completion replaced earlier flags.");
        prefix = "<Theme><DockTemplate Part='Title' State='Active|";
        Check(!Labels(LanguageFeatures.Complete(catalog, new FleckDocument(prefix), prefix.Length)).Contains("Active"), "Combined template state suggested a duplicate flag.");
        prefix = "<Theme><DockTemplate Part='Title'><Button Role='";
        var roles = Labels(LanguageFeatures.Complete(catalog, new FleckDocument(prefix), prefix.Length));
        Check(roles.SequenceEqual(["Close", "Pin", "Float", "Menu"]), "Button template role completion includes a role rejected by the parser.");
        foreach (string role in roles.Append("Drag"))
        {
            Check((Sunfleck.App.ValidateFleck($"<Theme><DockTemplate Part='Title'><Button Role='{role}'/></DockTemplate></Theme>") is null) == (role != "Drag"),
                "Button role schema and parser diverged.");
        }
        const string resource = "<Theme><Resource Key='dock.floating_padding' Value='12'/></Theme>";
        var resourceHover = LanguageFeatures.Hover(catalog, new FleckDocument(resource), resource.IndexOf("Value=", StringComparison.Ordinal) + 2);
        Check(resourceHover?["contents"]?["value"]?.GetValue<string>().Contains("0 … 64", StringComparison.Ordinal) == true,
            "Theme resource hover omitted its shared numeric range.");
    }

    private static void CommandMetadata()
    {
        var catalog = new LanguageCatalog();
        catalog.Add(JsonNode.Parse("""
            {"version":1,"name":"Commands","properties":[
                {"path":"Sync","type":"command","writable":false,"commandModes":["Sync"]},
                {"path":"Both","type":"command","writable":false,"commandModes":["Sync","Async"]},
                {"path":"Unknown","type":"command","writable":false},
                {"path":"AsyncOnly","type":"command","writable":false,"commandModes":["Async"]}]}
            """)!.AsObject());
        const string start = "<Window xmlns='urn:sunfleck:v1' Model='Commands'><Button ";
        string partial = start + "CommandMode='Async' Command='{Binding ";
        Check(Labels(LanguageFeatures.Complete(catalog, new FleckDocument(partial), partial.Length)).SequenceEqual(["Both", "Unknown", "AsyncOnly"]),
            "Async command completion included a known synchronous command or excluded legacy metadata.");
        partial = start + "Command='{Binding Sync}' CommandMode='";
        Check(Labels(LanguageFeatures.Complete(catalog, new FleckDocument(partial), partial.Length)).SequenceEqual(["Sync"]),
            "CommandMode completion ignored command capabilities.");
        foreach (string command in new[] { "Sync", "Both", "Unknown" })
        {
            var document = new FleckDocument(start + $"Command='{{Binding {command}}}' CommandMode='Async'/></Window>");
            Check(Codes(LanguageFeatures.Diagnostics(catalog, document)).Contains("command_mismatch") == (command == "Sync"),
                "Static command mode diagnostic disagreed with declared or unknown capabilities.");
        }
        Check(Codes(LanguageFeatures.Diagnostics(catalog, new FleckDocument(start + "Command='{Binding AsyncOnly}'/></Window>"))).Contains("command_mismatch"),
            "Static command check ignored Fleck's implicit Sync mode.");
        var stale = new LanguageCatalog();
        stale.Add(catalog.Model("Commands").Data!, stale: true);
        var invalid = new FleckDocument(start + "Command='{Binding Sync}' CommandMode='Async'/></Window>");
        Check(LanguageFeatures.Diagnostics(stale, invalid).Single(diagnostic => diagnostic?["code"]?.GetValue<string>() == "command_mismatch")!["severity"]!.GetValue<int>() == 3,
            "Stale command capabilities produced a definitive error.");
        foreach (string invalidModes in new[] { "[]", "[\"Other\"]", "[\"Sync\",\"Sync\"]" })
        {
            try
            {
                catalog.Add(JsonNode.Parse("{\"name\":\"Invalid\",\"properties\":[{\"path\":\"Run\",\"type\":\"command\",\"writable\":false,\"commandModes\":" + invalidModes + "}]}")!.AsObject());
                throw new InvalidOperationException("Malformed command capabilities were accepted.");
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    private static void Export()
    {
        string file = Path.Combine(Path.GetTempPath(), "sunfleck-model-" + Guid.NewGuid().ToString("N") + ".fleckmodel.json");
        try
        {
            Sunfleck.Development.Program.ExportModels(Assembly.GetExecutingAssembly().Location, file, [typeof(ExportedModel).FullName!]);
            var data = JsonNode.Parse(File.ReadAllText(file))!;
            var properties = data["models"]![0]!["properties"]!.AsArray();
            var property = properties.Single(property => property!["path"]!.GetValue<string>() == "NeverRead")!;
            Check(property["source"]!["uri"]!.GetValue<string>().EndsWith("DevelopmentLanguageTests.cs", StringComparison.Ordinal), "Portable PDB source mapping failed.");
            Check(properties.Any(property => property!["path"]!.GetValue<string>() == "Message"), "Inherited property missing from metadata.");
            Check(properties.Single(property => property!["path"]!.GetValue<string>() == "SyncCommand")!["commandModes"]!.AsArray()
                .Select(mode => mode!.GetValue<string>()).SequenceEqual(["Sync"]), "Command attribute was not exported without evaluating its getter.");
            Check(properties.Single(property => property!["path"]!.GetValue<string>() == "UnknownCommand")!["commandModes"] is null,
                "Type exporter invented capabilities for an unannotated command.");
            var catalog = new LanguageCatalog();
            catalog.Configure(new JsonArray(file));
            Check(catalog.Errors.Count == 0 && catalog.Model(typeof(ExportedModel).FullName).Data is not null, "Exported model cannot be loaded by language service.");
            File.WriteAllText(file, "{\"version\":1,\"models\":[]}");
            catalog.Reload();
            Check(catalog.Model(typeof(ExportedModel).FullName).Data is null, "Model update retained removed exports.");
            File.WriteAllText(file, "{\"version\":1,\"models\":[{}]}");
            catalog.Reload();
            Check(catalog.Errors.Count == 1, "Malformed model metadata was not isolated.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    internal static void NativeModels(string[] files)
    {
        var catalog = new LanguageCatalog();
        catalog.Configure(new JsonArray(files.Select(file => (JsonNode?)JsonValue.Create(file)).ToArray()));
        Check(catalog.Errors.Count == 0, "Build-generated metadata is invalid: " + string.Join("; ", catalog.Errors));
        foreach (string name in new[] { "CCounter", "CppCounter", "CounterModel" })
        {
            var (model, uncertainty) = catalog.Model(name);
            Check(model is not null && uncertainty is null, $"Build-generated {name} metadata is missing or stale.");
            Check(model!["properties"]!.AsArray().Any(property => property!["source"]?["line"]?.GetValue<int>() > 0), $"{name} declaration source missing.");
        }
        foreach (string name in new[] { "CCounter", "CppCounter" })
        {
            var document = new FleckDocument($"<Window Model='{name}'><TextBlock Text='{{Binding Counter.");
            Check(Labels(LanguageFeatures.Complete(catalog, document, document.Text.Length)).Contains("Counter.Value"), $"{name} declarations do not drive Binding completion.");
        }
    }
}
