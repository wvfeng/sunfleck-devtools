using System.Text.Json;
using Sunfleck;

internal static class DevelopmentTests
{
    private sealed class MetadataModel
    {
        public string Name => throw new InvalidOperationException("Metadata must not call getters.");
        public int Writable { get; set; }
        public object Unsupported { get; } = new();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
    private static void ExpectFailure(Action action)
    {
        try
        {
            action();
        }
        catch (SunfleckException)
        {
            return;
        }
        throw new InvalidOperationException("Expected a native binding error.");
    }
    internal static void Run()
    {
        CheckVisualStudioDiscovery();
        using (var app = new App(Renderer.Software, headless: true))
        using (var model = app.CreatePropertyModel())
        {
            Node? previous = null;
            string? readDuringRefresh = null;
            bool observe = false;
            model.Add("Name", () =>
            {
                if (observe)
                {
                    Check(app.GetFleckRefreshStatus().Pending, "Queued refresh lost its in-progress status inside a getter.");
                    readDuringRefresh = previous!.Text;
                }
                return "Bound";
            });
            const string first = "<Window xmlns='urn:sunfleck:v1'><StackPanel><TextBox Id='editor' Text='Saved'/><TextBlock Text='{Binding Name}'/></StackPanel></Window>";
            app.LoadFleck(first, model);
            app.OpenWindow();
            previous = app.FindFleckNode("editor");
            observe = true;
            app.QueueFleckRefresh(first.Replace("TextBox", "TextBlock", StringComparison.Ordinal), model);
            app.Poll();
            ExpectFailure(() => _ = previous.Text);
            Check(readDuringRefresh == "Saved" && !app.GetFleckRefreshStatus().Pending,
                "Getter access lost the old draft or left a completed refresh pending.");
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            const string first = "<Window xmlns='urn:sunfleck:v1'><TextBox Id='editor'/></Window>";
            const string next = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='editor'/></Window>";
            app.LoadFleck(first);
            app.OpenWindow();
            var previous = app.FindFleckNode("editor");
            previous.Text = "Managed application draft";
            string captured = "";
            int captures = 0, completions = 0;
            bool reject = false;
            app.SetFleckRefreshStateCallback((phase, result) =>
            {
                if (phase == FleckRefreshPhase.Capture)
                {
                    captures++;
                    captured = app.FindFleckNode("editor").Text;
                    return reject ? Result.InvalidState : Result.Ok;
                }
                completions++;
                if (result == Result.Ok)
                {
                    ExpectFailure(() => _ = previous.Text);
                    app.FindFleckNode("editor").Text = captured;
                }
                return Result.Ok;
            });
            GC.Collect();
            GC.WaitForPendingFinalizers();
            app.QueueFleckRefresh(next);
            app.Poll();
            Check(captures == 1 && completions == 1 && app.FindFleckNode("editor").Text == "Managed application draft",
                "Managed state callback was collected or could not restore a replacement node.");
            previous = app.FindFleckNode("editor");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            app.QueueFleckRefresh(first);
            app.Poll();
            Check(captures == 2 && completions == 2 && app.FindFleckNode("editor").Text == "Managed application draft",
                "Managed state callback did not stay alive across Poll calls.");
            reject = true;
            ExpectFailure(() => app.RefreshFleck(next));
            Check(captures == 3 && completions == 2 && app.FindFleckNode("editor").Text == captured,
                "Rejected state capture changed the page or completed a rejected attempt.");
            app.SetFleckRefreshStateCallback(null);
            app.RefreshFleck(next);
            Check(captures == 3 && completions == 2, "Removing the managed state hook left a native callback attached.");
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            const string first = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='label' Text='Before'/></Window>";
            const string next = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='label' Text='After'/></Window>";
            app.LoadFleck(first);
            app.OpenWindow();
            bool failCapture = true;
            app.SetFleckRefreshStateCallback((phase, _) =>
            {
                if (failCapture || phase == FleckRefreshPhase.Complete)
                {
                    throw new ArithmeticException("Application state hook failed");
                }
                return Result.Ok;
            });
            void RejectQueuedCallback()
            {
                app.QueueFleckRefresh(next);
                try
                {
                    app.Poll();
                }
                catch (ArithmeticException)
                {
                    return;
                }
                throw new InvalidOperationException("State hook exception escaped its managed callback channel.");
            }
            RejectQueuedCallback();
            Check(app.FindFleckNode("label").Text == "Before" && app.GetFleckRefreshStatus().Result == Result.InvalidState,
                "Capture exception changed the old page or lost its terminal result.");
            failCapture = false;
            RejectQueuedCallback();
            Check(app.FindFleckNode("label").Text == "After" && app.GetFleckRefreshStatus().Result == Result.Ok,
                "Completion exception undid the committed page or changed the refresh result.");
            app.SetFleckRefreshStateCallback(null);
        }
        using (var app = new App(Renderer.Software, headless: true))
        using (var model = app.CreatePropertyModel())
        {
            const string page = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='label' Text='Initial'/></Window>";
            app.LoadFleck(page);
            app.OpenWindow();
            var previous = app.FindFleckNode("label");
            ExpectFailure(() => model.Bind(previous, PropertyTarget.Text, "Missing"));
            int callbacks = 0;
            app.BindingDiagnosticsChanged += _ =>
            {
                ExpectFailure(() => _ = previous.Text);
                callbacks++;
            };
            app.Post(() => app.QueueFleckRefresh("<Window xmlns='urn:sunfleck:v1'><Button Id='label' Content='Updated'/></Window>"));
            app.Poll();
            Check(callbacks == 1 && !app.GetFleckRefreshStatus().Pending && app.GetFleckRefreshStatus().Result == Result.Ok &&
                app.FindFleckNode("label").Text == "Updated", "Managed queue did not retire wrappers before callbacks or lost a posted request.");
            ExpectFailure(() => _ = previous.Text);
            app.QueueFleckRefresh("");
            app.Poll();
            Check(app.GetFleckRefreshStatus().Result == Result.InvalidArgument && app.GetFleckRefreshStatus().Diagnostic is not null,
                "Empty queued XML was treated as cancellation.");
            ulong sequence = app.GetFleckRefreshStatus().Sequence;
            app.QueueFleckRefresh(page);
            app.QueueFleckRefresh(null);
            app.Poll();
            Check(app.GetFleckRefreshStatus().Sequence == sequence && app.FindFleckNode("label").Text == "Updated", "Managed queue cancellation changed the UI.");
        }
        using (var app = new App(Renderer.Software, headless: true))
        using (var command = app.CreateCommand(_ => { }))
        using (var model = app.CreatePropertyModel().AddCommand("Run", command))
        {
            Check(command.ExecutionModes == CommandExecutionModes.Sync && model.Describe("Commands").Contains("\"commandModes\":[\"Sync\"]", StringComparison.Ordinal),
                "Managed registered command metadata was not inferred.");
            model.SetCommandModes("Run", CommandExecutionModes.None);
            Check(!model.Describe("Commands").Contains("commandModes", StringComparison.Ordinal), "Command metadata could not be cleared.");
            ExpectFailure(() => model.SetCommandModes("Run", (CommandExecutionModes)4));
            using (var reflected = app.CreatePropertyModel(new ExportedModel()))
            {
                Check(reflected.Describe("Reflected").Contains("\"commandModes\":[\"Sync\"]", StringComparison.Ordinal),
                    "Reflected runtime command metadata called a getter or lost its attribute.");
            }
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            const string page = "<Window xmlns='urn:sunfleck:v1'><DockHost Id='dock'><DockGroup><DockDocument Id='item'><TextBox Id='editor'/></DockDocument></DockGroup></DockHost></Window>";
            app.LoadFleck(page);
            app.OpenWindow();
            var dock = app.CreateDock(app.FindFleckNode("dock"));
            var previous = dock.Find("item");
            var node = app.FindFleckNode("item");
            var editor = app.FindFleckNode("editor");
            editor.Text = "Retained draft";
            app.RefreshFleck(page.Replace("DockDocument", "DockTool", StringComparison.Ordinal));
            ExpectFailure(() => _ = previous.State);
            ExpectFailure(() => _ = node.Text);
            Check(dock.Find("item").State == DockState.Docked && editor.Text == "Retained draft",
                "Managed Dock type migration lost its workspace or retained child state.");
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            const string first = "<Window xmlns='urn:sunfleck:v1'><DockHost Id='dock'><DockGroup><DockDocument Id='a'/><DockDocument Id='b'/></DockGroup></DockHost></Window>";
            app.LoadFleck(first);
            app.OpenWindow();
            var dock = app.CreateDock(app.FindFleckNode("dock"));
            var old = dock.Find("b");
            var retained = dock.Find("a");
            app.RefreshFleck(first.Replace("Id='b'", "Id='c'", StringComparison.Ordinal));
            ExpectFailure(() => _ = old.Id);
            Check(retained.Id == "a" && dock.Find("c").Id == "c", "Dock refresh lost retained or new items.");
            int retiredEvents = 0;
            dock.Event += (_, _) => retiredEvents++;
            app.RefreshFleck("<Window xmlns='urn:sunfleck:v1'><Panel/></Window>");
            app.Poll();
            try
            {
                _ = retained.Id;
                throw new InvalidOperationException("A removed Dock item was still accessible.");
            }
            catch (ObjectDisposedException)
            {
            }
            app.RefreshFleck(first);
            var recreated = app.CreateDock(app.FindFleckNode("dock"));
            Check(!ReferenceEquals(dock, recreated), "Removed Dock workspace was reused.");
            recreated.Find("b").Activate();
            Check(retiredEvents == 0, "Removed Dock workspace still delivered events.");
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            app.LoadFleck("<Window xmlns='urn:sunfleck:v1'><StackPanel><Button Id='item'/><TextBlock Id='retained'/></StackPanel></Window>");
            app.OpenWindow();
            var previous = app.FindFleckNode("item");
            previous.SetAction(_ => throw new InvalidOperationException("Retired callback ran."));
            var retained = app.FindFleckNode("retained");
            var previousId = previous.DebugId;
            app.RefreshFleck("<Window xmlns='urn:sunfleck:v1'><StackPanel><TextBlock Id='item'/><TextBlock Id='retained' Text='Updated'/></StackPanel></Window>");
            ExpectFailure(() => _ = previous.Text);
            ExpectFailure(() => _ = app.FindDebugNode(previousId));
            Check(retained.Text == "Updated" && app.FindFleckNode("item").DebugId != previousId,
                "Fleck refresh lost retained state or reused a retired ID.");
        }
        Check(App.ValidateFleck("<Theme xmlns='urn:sunfleck:v1' Base='Fluent2'/>") is null, "Valid Theme rejected.");
        using (var app = new App(Renderer.Software, headless: true))
        {
            const string firstPage = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='label' Text='First'/></Window>";
            const string secondPage = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='label' Text='Second'/></Window>";
            app.LoadFleck(firstPage);
            var first = app.GetFleckRoot();
            app.LoadFleck(secondPage);
            var second = app.GetFleckRoot();
            app.OpenWindow();
            app.SetRoot(first);
            app.RefreshFleck(firstPage.Replace("First", "Updated", StringComparison.Ordinal));
            Check(app.FindFleckNode("label").Text == "Updated" && second.Text == "Second", "Refresh crossed page boundaries.");
            app.SetRoot(second);
            app.DiscardFleckPage(first);
            ExpectFailure(() => _ = first.Text);
            Check(app.FindFleckNode("label").Text == "Second", "Discard changed the active page.");
        }
        Check(App.ValidateFleck("<Window xmlns='urn:sunfleck:v1'><Button Typo='bad'/></Window>")?.Code == FleckError.Attribute, "Invalid attribute accepted.");
        using (var schema = JsonDocument.Parse(App.GetFleckSchema()))
        {
            var button = schema.RootElement.GetProperty("elements").EnumerateArray().Single(element => element.GetProperty("name").GetString() == "Button");
            var content = button.GetProperty("attributes").EnumerateArray().Single(attribute => attribute.GetProperty("name").GetString() == "Content");
            Check(content.GetProperty("binding").GetBoolean() && !content.GetProperty("twoWay").GetBoolean(), "Button.Content schema differs from runtime.");
        }
        using (var metadata = JsonDocument.Parse(PropertyModel.DescribeType<MetadataModel>()))
        {
            Check(metadata.RootElement.GetProperty("properties").GetArrayLength() == 2, "Unsupported members exported or supported members missing.");
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            string text = "Original";
            bool broken = false;
            using (var model = app.CreatePropertyModel().Add("Name", () =>
            {
                if (broken)
                {
                    throw new ArithmeticException("Getter failed");
                }
                return text;
            }, value => text = value))
            {
                using (var metadata = JsonDocument.Parse(model.Describe("TestModel")))
                {
                    var source = metadata.RootElement.GetProperty("properties")[0].GetProperty("source");
                    Check(source.GetProperty("uri").GetString()!.EndsWith("DevelopmentTests.cs", StringComparison.Ordinal) && source.GetProperty("line").GetUInt32() > 0, "C# registration source missing.");
                }
                const string xml = "<Window xmlns='urn:sunfleck:v1'><StackPanel><TextBlock Text='🌱' Id='unicode'/><TextBlock Id='label' Text='{Binding Name}'/><Button Id='once' Content='{Binding Name, Mode=OneTime}'/></StackPanel></Window>";
                app.LoadFleck(xml, model);
                app.SetFleckSourceUri("file:///development.fleck");
                var label = app.FindFleckNode("label");
                Check(app.FindDebugNode(label.DebugId).DebugId == label.DebugId, "Object ID lookup failed.");
                using (var snapshot = JsonDocument.Parse(app.GetDebugSnapshot()))
                {
                    var nodes = snapshot.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
                    var unicode = nodes.Single(node => node.GetProperty("fleckId").GetString() == "unicode");
                    var id = unicode.GetProperty("source").GetProperty("attributes").EnumerateArray().Single(attribute => attribute.GetProperty("name").GetString() == "Id");
                    Check(id.GetProperty("range").GetProperty("column").GetInt32() == xml.IndexOf("Id=", StringComparison.Ordinal) + 1, "Unicode columns must use UTF-16 units.");
                    Check(nodes.Single(node => node.GetProperty("fleckId").GetString() == "once").GetProperty("bindings").GetArrayLength() == 1, "OneTime binding declaration missing.");
                }
                int events = 0;
                app.BindingDiagnosticsChanged += _ => events++;
                var logBatches = new List<string>();
                Action<string> sink = json => logBatches.Add(json);
                app.BindingDiagnosticLog += sink;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                broken = true;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        model.NotifyPropertyChanged("Name");
                        throw new InvalidOperationException("Getter exception lost.");
                    }
                    catch (ArithmeticException)
                    {
                    }
                }
                Check(label.Text == "Original", "Read failure changed displayed value.");
                using (var diagnostics = JsonDocument.Parse(app.GetBindingDiagnostics()))
                {
                    var entries = diagnostics.RootElement.GetProperty("diagnostics");
                    Check(entries.GetArrayLength() == 1 && entries[0].GetProperty("occurrences").GetUInt64() == 3, "Repeated diagnostics not coalesced.");
                    Check(entries[0].GetProperty("active").GetBoolean() && entries[0].GetProperty("code").GetString() == "getter_failed", "Wrong read diagnostic.");
                }
                app.DispatchBindingDiagnostics();
                app.DispatchBindingDiagnostics();
                Check(events == 1, "Diagnostic event repeated without changes.");
                Check(logBatches.Count == 1 && logBatches[0].Contains("\"occurrences\":3", StringComparison.Ordinal),
                    "Managed log adapter displaced diagnostics, lost its delegate or replayed unchanged records.");
                broken = false;
                model.NotifyPropertyChanged("Name");
                app.DispatchBindingDiagnostics();
                Check(events == 2, "Recovery event missing.");
                Check(logBatches.Count == 2 && logBatches[1].Contains("\"active\":false", StringComparison.Ordinal), "Managed recovery log missing.");
                app.BindingDiagnosticLog -= sink;
                using (var diagnostics = JsonDocument.Parse(app.GetBindingDiagnostics()))
                {
                    Check(!diagnostics.RootElement.GetProperty("diagnostics")[0].GetProperty("active").GetBoolean(), "Recovered binding still active.");
                }
                ExpectFailure(() => model.Bind(label, PropertyTarget.Text, "Missing"));
                Check(app.GetBindingDiagnostics().Contains("path_missing", StringComparison.Ordinal), "Missing path diagnostic absent.");
                app.DispatchBindingDiagnostics();
                Check(logBatches.Count == 2, "Removed log subscriber received diagnostics.");
                model.Dispose();
                using (var snapshot = JsonDocument.Parse(app.GetDebugSnapshot()))
                {
                    Check(snapshot.RootElement.GetProperty("models").GetArrayLength() == 0, "Disposed model retained.");
                }
            }
        }
    }
    private static void CheckVisualStudioDiscovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "sunfleck-vs-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string project in new[] { "first", "second" })
            {
                foreach (string folder in new[] { "bin/Debug", "bin/Release", "obj/Debug", "node_modules/package" })
                {
                    string directory = Path.Combine(root, project, folder);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "same.fleckmodel.json"), "{\"version\":1,\"models\":[]}");
                }
            }
            string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
            var debug = Sunfleck.VisualStudio.FleckModelDiscovery.Discover(first, "Debug", ["Debug", "Release"]);
            var release = Sunfleck.VisualStudio.FleckModelDiscovery.Discover(first, "Release", ["Debug", "Release"]);
            var other = Sunfleck.VisualStudio.FleckModelDiscovery.Discover(second, "Debug", ["Debug", "Release"]);
            Check(debug.Length == 1 && debug[0] == Path.Combine(first, "bin", "Debug", "same.fleckmodel.json"), "VS discovery mixed build configurations.");
            Check(release.Length == 1 && release[0] != debug[0] && other.Length == 1 && other[0] != debug[0], "VS discovery mixed project scopes.");
            var configured = Sunfleck.VisualStudio.FleckModelDiscovery.Configured("bin/Debug/same.fleckmodel.json;\r\n" + debug[0], first);
            Check(configured.Length == 1 && configured[0] == debug[0], "VS configured model paths were not resolved or deduplicated.");
            Check(Sunfleck.VisualStudio.FleckModelDiscovery.Configured("relative.fleckmodel.json", null).Length == 0, "VS discovery resolved a relative path without a solution root.");
            Directory.CreateDirectory(Path.Combine(first, "Prod"));
            File.WriteAllText(Path.Combine(first, "Prod", "model.fleckmodel.json"), "{}");
            Check(!Sunfleck.VisualStudio.FleckModelDiscovery.Discover(first, "Debug", ["Debug", "Release", "Prod"])
                .Any(path => path.Contains(Path.DirectorySeparatorChar + "Prod" + Path.DirectorySeparatorChar, StringComparison.Ordinal)), "VS discovery ignored custom configurations.");
            string nested = Path.Combine(first, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "model.fleckmodel.json"), "{}");
            var parentFiles = Sunfleck.VisualStudio.FleckModelDiscovery.Discover(first, "Debug", ["Debug", "Release", "Prod"], [first, nested]);
            Check(parentFiles.Length == 1 && parentFiles[0] == debug[0], "VS parent project absorbed a nested project's models.");
            var nestedFiles = Sunfleck.VisualStudio.FleckModelDiscovery.Discover(nested, "Debug", ["Debug", "Release"], [first, nested]);
            Check(nestedFiles.Length == 1 && nestedFiles[0] == Path.Combine(nested, "model.fleckmodel.json"), "VS nested project lost its own models.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
