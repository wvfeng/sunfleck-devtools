using System.Text.Json.Nodes;
using Sunfleck.Development;
using Sunfleck.VisualStudio;

internal static class VisualStudioDiscoveryProtocolTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sunfleck-vs-protocol-" + Guid.NewGuid().ToString("N")));
        try
        {
            string[] projects = [Path.Combine(root, "first"), Path.Combine(root, "first", "nested"), Path.Combine(root, "second")];
            string[] names = ["Parent", "Nested", "Sibling", "Global"];
            string[] pages = projects.Select(project => Path.Combine(project, "ui", "中文.fleck"))
                .Append(Path.Combine(root, "first-extra", "中文.fleck")).ToArray();
            string Source(string name, string configuration) => new Uri(Path.Combine(root, name + configuration + ".cs")).AbsoluteUri;
            void WriteModel(string file, string name, string configuration)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, new JsonObject
                {
                    ["version"] = 1,
                    ["models"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "Scoped",
                        ["properties"] = new JsonArray(new JsonObject
                        {
                            ["path"] = configuration + name, ["type"] = "string", ["writable"] = true,
                            ["source"] = new JsonObject { ["uri"] = Source(name, configuration), ["line"] = 7, ["column"] = 2 }
                        })
                    })
                }.ToJsonString());
            }
            for (int i = 0; i < projects.Length; ++i)
            {
                foreach (string configuration in new[] { "Debug", "Release" })
                {
                    WriteModel(Path.Combine(projects[i], "bin", configuration, "same.fleckmodel.json"), names[i], configuration);
                }
            }
            string global = Path.Combine(root, "global.fleckmodel.json");
            WriteModel(global, "Global", "Debug");
            JsonObject Settings(string configuration, bool unload = false)
            {
                var scopes = new JsonArray();
                for (int i = 0; i < projects.Length; ++i)
                {
                    if (unload && i == 2)
                    {
                        continue;
                    }
                    var files = unload && i == 1 ? [] : FleckModelDiscovery.Discover(projects[i], configuration, ["Debug", "Release"], projects);
                    scopes.Add(new JsonObject
                    {
                        ["uri"] = new Uri(projects[i] + Path.DirectorySeparatorChar).AbsoluteUri,
                        ["modelFiles"] = new JsonArray(files.Select(file => (JsonNode?)JsonValue.Create(file)).ToArray())
                    });
                }
                return new JsonObject { ["modelFiles"] = new JsonArray(global), ["modelScopes"] = scopes };
            }
            using var input = new MemoryStream();
            using var output = new MemoryStream();
            void Send(string method, JsonObject? parameters = null, int? id = null)
            {
                var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
                if (id.HasValue)
                {
                    message["id"] = id.Value;
                }
                LspServer.Write(input, message);
            }
            JsonObject Request(int index, int offset) => new()
            {
                ["textDocument"] = new JsonObject { ["uri"] = new Uri(pages[index]).AbsoluteUri },
                ["position"] = new JsonObject { ["line"] = 0, ["character"] = offset }
            };
            Send("initialize", new JsonObject { ["initializationOptions"] = Settings("Debug") }, 1);
            string[] texts = names.Select(name => "<Window xmlns='urn:sunfleck:v1' Model='Scoped'><TextBlock Text='{Binding Debug" + name + "}'/></Window>").ToArray();
            for (int i = 0; i < pages.Length; ++i)
            {
                Send("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = new Uri(pages[i]).AbsoluteUri, ["version"] = 1, ["text"] = texts[i] }
                });
                int offset = texts[i].IndexOf("Debug", StringComparison.Ordinal);
                Send("textDocument/completion", Request(i, offset), 10 + i);
                Send("textDocument/definition", Request(i, offset + 2), 20 + i);
            }
            Send("workspace/didChangeConfiguration", new JsonObject { ["settings"] = new JsonObject { ["sunfleck"] = Settings("Release") } });
            for (int i = 0; i < pages.Length; ++i)
            {
                int offset = texts[i].IndexOf("Debug", StringComparison.Ordinal);
                Send("textDocument/completion", Request(i, offset), 30 + i);
                Send("textDocument/definition", Request(i, offset + 2), 40 + i);
            }
            Send("workspace/didChangeConfiguration", new JsonObject { ["settings"] = Settings("Release", unload: true) });
            Send("textDocument/completion", Request(1, texts[1].IndexOf("Debug", StringComparison.Ordinal)), 51);
            Send("textDocument/completion", Request(2, texts[2].IndexOf("Debug", StringComparison.Ordinal)), 52);
            Send("shutdown", id: 90);
            Send("exit");
            input.Position = 0;
            Check(new LspServer(input, output).Run() == 0, "Discovery-to-LSP session failed.");
            output.Position = 0;
            var messages = new List<JsonObject>();
            while (LspServer.Read(output) is { } message)
            {
                messages.Add(message);
            }
            JsonNode? Result(int id)
            {
                var message = messages.Single(message => message["id"]?.GetValue<int>() == id);
                Check(message["error"] is null, "Discovery protocol returned an error: " + message);
                return message["result"];
            }
            string[] Labels(int id) => Result(id)!.AsArray().Select(item => item!["label"]!.GetValue<string>()).ToArray();
            for (int i = 0; i < names.Length; ++i)
            {
                Check(Labels(10 + i).SequenceEqual(["Debug" + names[i]]), "Discovered same-name models crossed project boundaries.");
                Check(Result(20 + i)?["uri"]?.GetValue<string>() == Source(names[i], "Debug"), "Discovered model definition resolved to another project.");
                string expected = i == 3 ? "DebugGlobal" : "Release" + names[i];
                Check(Labels(30 + i).SequenceEqual([expected]), "Configuration switch retained stale or foreign properties.");
                Check(i == 3 ? Result(40 + i) is not null : Result(40 + i) is null, "Configuration switch retained a retired definition.");
                var publications = messages.Where(message => message["method"]?.GetValue<string>() == "textDocument/publishDiagnostics" &&
                    message["params"]?["uri"]?.GetValue<string>() == new Uri(pages[i]).AbsoluteUri).ToArray();
                Check(publications.Length == 3, "Configuration change did not republish all open documents.");
                bool Missing(JsonObject publication) => publication["params"]!["diagnostics"]!.AsArray()
                    .Any(diagnostic => diagnostic?["code"]?.GetValue<string>() == "path_missing");
                Check(!Missing(publications[0]) && Missing(publications[1]) == (i < 3), "Configuration diagnostics did not match discovered metadata.");
            }
            Check(Labels(51).Length == 0, "Empty nested scope fell back to its parent's model.");
            Check(Labels(52).SequenceEqual(["DebugGlobal"]), "Unloaded project scope retained its model.");
        }
        finally
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("sunfleck-vs-protocol-", StringComparison.Ordinal) && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
