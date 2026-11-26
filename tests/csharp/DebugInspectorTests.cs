using System.Text.Json.Nodes;
using Sunfleck;
using Sunfleck.Development;

internal static class DebugInspectorTests
{
    private static string EncodedDrive(string uri) => uri[..8] + uri[8..].Replace(":/", "%3A/", StringComparison.Ordinal);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Rejected(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid inspector operation was accepted.");
    }

    internal static void Run()
    {
        var graph = JsonNode.Parse("""
            {"version":1,"timestamp":1,"windows":[{"id":0},{"id":1}],"nodes":[
            {"id":"9007199254740993","parent":"2","window":0},
            {"id":"2","parent":"9007199254740993","window":0},
            {"id":"3","parent":"2","window":1},{"id":"4","parent":"4","window":1}]}
            """)!.AsObject();
        var projection = DebugInspector.Project(graph);
        Check(projection["inspector"]!["parents"]!["2"]!.GetValue<string>() == "0" &&
            projection["inspector"]!["parents"]!["3"]!.GetValue<string>() == "0" &&
            projection["inspector"]!["parents"]!["4"]!.GetValue<string>() == "0",
            "Shared inspector did not break cycles, cross-window parents or self parents.");
        var duplicate = graph.DeepClone().AsObject();
        duplicate["nodes"]!.AsArray().Add(duplicate["nodes"]![0]!.DeepClone());
        Rejected(() => DebugInspector.Project(duplicate));
        var deep = new JsonObject { ["version"] = 1, ["timestamp"] = 1L,
            ["windows"] = new JsonArray(new JsonObject { ["id"] = 0 }), ["nodes"] = new JsonArray() };
        for (int index = 1; index <= 4096; index++)
        {
            deep["nodes"]!.AsArray().Add(new JsonObject { ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["parent"] = (index == 4096 ? 1 : index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ["window"] = 0 });
        }
        Check(DebugInspector.Project(deep)["inspector"]!["parents"]!["4096"]!.GetValue<string>() == "0",
            "Shared hierarchy did not handle a 4096-node cycle without recursive traversal.");
        string sourceUri = new Uri(Path.Combine(Path.GetTempPath(), "source.fleck")).AbsoluteUri;
        var located = JsonNode.Parse("""
            {"nodes":[
            {"id":"9007199254740993","source":{"uri":"file:///source.fleck","range":{"line":2,"column":3,"endLine":2,"endColumn":9,"length":6}}},
            {"id":"2","source":{"uri":"file:///source.fleck","range":{"line":1,"column":1,"endLine":3,"endColumn":1,"length":40}}},
            {"id":"3","source":{"uri":"file:///other.fleck","range":{"line":2,"column":1,"endLine":3,"endColumn":1}}}]}
            """.Replace("file:///source.fleck", sourceUri, StringComparison.Ordinal))!.AsObject();
        var position = new JsonObject { ["uri"] = sourceUri, ["line"] = 2, ["column"] = 3 };
        Check(DebugInspector.Locate(located, position)["nodes"]!.AsArray().Select(node => node!["id"]!.GetValue<string>())
            .SequenceEqual(["9007199254740993", "2"]), "Shared source matching lost identity, file filtering or smallest-range order.");
        if (OperatingSystem.IsWindows())
        {
            string specialPath = Path.Combine(Path.GetTempPath(), "source 中文%20.fleck");
            string encoded = "file:///" + string.Join('/', specialPath.Split('\\').Select(Uri.EscapeDataString));
            Check(RuntimeModels.SourcePath(specialPath) == specialPath, "A native source path decoded literal percent escapes.");
            Check(RuntimeModels.SourcePath(encoded) == specialPath, "VS Code encoded drive URI lost its local path or decoded twice.");
            position["uri"] = EncodedDrive(sourceUri);
            Check(DebugInspector.Locate(located, position)["nodes"]!.AsArray().Count == 2,
                "Encoded drive URI could not locate canonical runtime source objects.");
            located["nodes"]![0]!["source"]!["uri"] = position["uri"]!.DeepClone();
            position["uri"] = sourceUri;
            Check(DebugInspector.Locate(located, position)["nodes"]!.AsArray().Count == 2,
                "Canonical editor URI could not locate encoded runtime source objects.");
        }
        position["column"] = 9;
        Check(DebugInspector.Locate(located, position)["nodes"]!.AsArray().Count == 1, "Source matching included an exclusive range end.");
        position["uri"] = "https://example.test/source.fleck";
        Rejected(() => DebugInspector.Locate(located, position));
        position["uri"] = "file://remote-server/share/source.fleck";
        Rejected(() => DebugInspector.Locate(located, position));

        string pipe = "sunfleck-test-" + Guid.NewGuid().ToString("N");
        var inspector = new DebugInspector();
        Rejected(() => inspector.Handle("sunfleck/debugSnapshot", new JsonObject { ["path"] = Path.GetTempPath() }));
        Rejected(() => inspector.Handle("sunfleck/debugSnapshot", new JsonObject { ["pipe"] = "bad/name" }));
        App CreateAutomaticallyConnectedApp()
        {
            var previous = Environment.GetEnvironmentVariable("SUNFLECK_DEBUG_PIPE");
            try
            {
                Environment.SetEnvironmentVariable("SUNFLECK_DEBUG_PIPE", "bad/name");
                Rejected(() => { using var invalid = new App(Renderer.Software, headless: true); });
                Environment.SetEnvironmentVariable("SUNFLECK_DEBUG_PIPE", pipe);
                return new App(Renderer.Software, headless: true);
            }
            finally { Environment.SetEnvironmentVariable("SUNFLECK_DEBUG_PIPE", previous); }
        }
        using var app = CreateAutomaticallyConnectedApp();
        using var model = app.CreatePropertyModel();
        using var command = app.CreateCommand(_ => { });
        model.AddCommand("Run", command);
        int reads = 0;
        model.Add("Name", () => { reads++; return "Business"; });
        app.LoadFleck("<Window xmlns='urn:sunfleck:v1' Model='LiveModel'><TextBlock Id='label' Text='{Binding Name}'/></Window>", model);
        string runtimeUri = new Uri(Path.Combine(Path.GetTempPath(), "inspector.fleck")).AbsoluteUri;
        app.SetFleckSourceUri(runtimeUri);
        app.OpenWindow();
        T Pump<T>(Func<T> action)
        {
            var task = Task.Run(action);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until) { app.Poll(); Thread.Sleep(1); }
            Check(task.IsCompleted, "Pipe test did not finish.");
            return task.GetAwaiter().GetResult();
        }
        JsonObject Read() => Pump(() => inspector.Handle("sunfleck/debugSnapshot", new JsonObject { ["pipe"] = pipe }));
        app.Poll();
        int previousReads = reads;
        var snapshot = Read();
        var node = snapshot["nodes"]!.AsArray().Single(item => item!["fleckId"]!.GetValue<string>() == "label")!;
        string modelId = node["bindings"]![0]!["model"]!.GetValue<string>();
        Check(node["source"]!["model"]!.GetValue<string>() == "LiveModel" &&
            snapshot["inspector"]!["models"]![modelId]!["properties"]!.AsArray().Any(property => property!["path"]!.GetValue<string>() == "Name"),
            "Pipe lost the model association.");
        Pump(() => { CheckLanguageSync(pipe, OperatingSystem.IsWindows() ? EncodedDrive(runtimeUri) : runtimeUri); return true; });
        Check(reads == previousReads, "Pipe inspection invoked a business getter.");
        string session = snapshot["debugSession"]!.GetValue<string>();
        Check(Read()["debugSession"]!.GetValue<string>() == session, "Polling changed the active session.");
        using (var other = new App(Renderer.Software, headless: true))
        {
            bool conflict = false;
            try { other.EnableDebugServer(pipe); }
            catch (InvalidOperationException error) when (error.Message.Contains(pipe, StringComparison.Ordinal)) { conflict = true; }
            Check(conflict, "Duplicate Pipe ownership was not rejected synchronously with the Pipe name.");
            Check(Read()["debugSession"]!.GetValue<string>() == session, "A duplicate listener changed the original session.");
            string otherPipe = pipe + "-other";
            other.EnableDebugServer(otherPipe);
            other.OpenWindow();
            JsonObject ReadOther()
            {
                var pending = Task.Run(() => inspector.Handle("sunfleck/debugSnapshot", new JsonObject { ["pipe"] = otherPipe }));
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!pending.IsCompleted && DateTime.UtcNow < deadline) { app.Poll(); other.Poll(); Thread.Sleep(1); }
                Check(pending.IsCompleted, "Second application Pipe did not respond.");
                return pending.GetAwaiter().GetResult();
            }
            string otherSession = ReadOther()["debugSession"]!.GetValue<string>();
            Check(otherSession != session, "Independent apps shared a debug session.");
            Rejected(() => other.EnableDebugServer(pipe));
            Rejected(() => other.EnableDebugServer("bad/name"));
            Check(ReadOther()["debugSession"]!.GetValue<string>() == otherSession,
                "A failed Pipe change destroyed the existing listener.");
            other.EnableDebugServer(null);
            other.EnableDebugServer(otherPipe);
            other.EnableDebugServer(null);
        }
        app.EnableDebugServer(pipe);
        Check(Read()["debugSession"]!.GetValue<string>() == session, "Enabling the same Pipe replaced its session.");
        Rejected(() => { using var duplicateApp = CreateAutomaticallyConnectedApp(); });
        Check(Read()["debugSession"]!.GetValue<string>() == session, "Failed automatic startup disrupted the original application.");
        app.RefreshFleck("<Window xmlns='urn:sunfleck:v1' Model='LiveModel'><Button Id='label'/></Window>", model);
        var runtime = new RuntimeModels();
        runtime.Update((pipe, "unbound-page"), Read());
        Check(runtime.For(runtimeUri, new LanguageCatalog()).Model("LiveModel").Data is not null,
            "An unbound page lost its registered metadata.");
        app.EnableDebugServer(null);
        app.EnableDebugServer(pipe);
        Check(Read()["debugSession"]!.GetValue<string>() != session, "Reopening a Pipe reused a retired session.");
        app.EnableDebugServer(null);
    }

    private static void CheckLanguageSync(string pipe, string uri)
    {
        const string text = "<Window xmlns='urn:sunfleck:v1' Model='LiveModel'><TextBlock Text='{Binding Name}'/></Window>";
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        int sequence = 0;
        void Send(string method, JsonObject? parameters = null) => LspServer.Write(input,
            new JsonObject { ["id"] = ++sequence, ["method"] = method, ["params"] = parameters });
        JsonObject Document() => new() { ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = 0, ["character"] = text.IndexOf("Name", StringComparison.Ordinal) + 2 } };
        Send("initialize");
        Send("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 1, ["text"] = text } });
        Send("sunfleck/debugSnapshot", new JsonObject { ["pipe"] = pipe });
        Send("textDocument/completion", Document());
        Send("textDocument/definition", Document());
        Send("textDocument/hover", Document());
        Send("sunfleck/debugSnapshot", new JsonObject { ["pipe"] = pipe });
        Send("textDocument/didChange", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "<Window xmlns='urn:sunfleck:v1' Model='LiveModel'><Button Command='{Binding Run}' CommandMode='Async'/></Window>" }) });
        Send("sunfleck/debugDisconnect", new JsonObject { ["pipe"] = pipe });
        Send("shutdown");
        LspServer.Write(input, new JsonObject { ["method"] = "exit" });
        input.Position = 0;
        Check(new LspServer(input, output).Run() == 0, "Runtime language sync broke LSP framing.");
        output.Position = 0;
        var messages = new List<JsonObject>();
        while (LspServer.Read(output) is { } message)
        {
            messages.Add(message);
        }
        JsonNode? Result(int id) {
            var response = messages.Single(message => message["id"]?.GetValue<int>() == id);
            Check(response["error"] is null, "LSP request failed: " + response.ToJsonString());
            return response["result"];
        }
        Check(Result(4)!.AsArray().Any(item => item!["label"]!.GetValue<string>() == "Name"), "Native snapshot did not drive LSP completion without setModels.");
        Check(Result(5)!["uri"]!.GetValue<string>().EndsWith("DebugInspectorTests.cs", StringComparison.Ordinal), "Runtime model source did not drive definition.");
        Check(Result(6)!["contents"]!["value"]!.GetValue<string>().Contains("string", StringComparison.Ordinal), "Runtime property type did not drive hover.");
        var diagnostics = messages.Where(message => message["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
            .Select(message => message["params"]!["diagnostics"]!.AsArray()).ToArray();
        Check(diagnostics.Length == 4 && diagnostics[0].Any(item => item!["code"]!.GetValue<string>() == "model_uncertain") &&
            diagnostics[1].Count == 0 && diagnostics[2].Any(item => item!["code"]!.GetValue<string>() == "command_mismatch") &&
            diagnostics[3].Any(item => item!["code"]!.GetValue<string>() == "model_uncertain"),
            "Runtime sync did not recover diagnostics, deduplicate identical polls or remove models on disconnect.");
    }
}
