using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sunfleck.Development;

internal sealed class LspServer(Stream input, Stream output)
{
    private readonly LanguageCatalog catalog = new();
    private readonly List<(string Prefix, LanguageCatalog Catalog)> scopes = [];
    private JsonArray dynamicModels = [];
    private readonly DebugInspector inspector = new();
    private readonly RuntimeModels runtimeModels = new();

    private static string ModelPath(string uri) => RuntimeModels.SourcePath(uri)?.Replace('\\', '/') ?? uri;

    private LanguageCatalog CatalogFor(string uri) => runtimeModels.For(uri, scopes
        .Where(scope => ModelPath(uri).StartsWith(scope.Prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        .OrderByDescending(scope => scope.Prefix.Length)
        .Select(scope => scope.Catalog)
        .FirstOrDefault() ?? catalog);

    private void ConfigureModels(JsonNode? settings)
    {
        if (settings?["language"] is JsonValue language)
        {
            catalog.English = language.GetValue<string>() == "en";
        }
        catalog.Configure(settings?["modelFiles"]);
        scopes.Clear();
        foreach (var scope in settings?["modelScopes"]?.AsArray() ?? [])
        {
            var scoped = new LanguageCatalog { English = catalog.English };
            scoped.Configure(scope!["modelFiles"]);
            scoped.SetDynamic(dynamicModels);
            scopes.Add((ModelPath(scope["uri"]!.GetValue<string>()).TrimEnd('/') + "/", scoped));
        }
    }
    private readonly Dictionary<string, (FleckDocument Document, int Version)> documents = new(StringComparer.Ordinal);
    private bool shutdown;
    private bool snippets;

    internal int Run()
    {
        while (Read(input) is { } message)
        {
            string? method = message["method"]?.GetValue<string>();
            JsonNode? id = message["id"], parameters = message["params"];
            if (method == "exit")
            {
                return shutdown ? 0 : 1;
            }
            try
            {
                JsonNode? result = Handle(method, parameters);
                if (id is not null)
                {
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or JsonException or KeyNotFoundException or NotSupportedException or IOException or UnauthorizedAccessException or NullReferenceException or FormatException or OverflowException)
            {
                if (id is not null)
                {
                    Send(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = error is NotSupportedException ? -32601 : -32602, ["message"] = error.Message }
                    });
                }
                else
                {
                    Notify("window/logMessage", new JsonObject { ["type"] = 1, ["message"] = error.Message });
                }
            }
        }
        return shutdown ? 0 : 1;
    }

    private JsonNode? Handle(string? method, JsonNode? parameters)
    {
        if (method == "initialize")
        {
            snippets = parameters?["capabilities"]?["textDocument"]?["completion"]?["completionItem"]?["snippetSupport"]?.GetValue<bool>() == true;
            ConfigureModels(parameters?["initializationOptions"]);
            var result = JsonNode.Parse("""
                {"capabilities":{"positionEncoding":"utf-16","textDocumentSync":{"openClose":true,"change":1},
                "completionProvider":{"triggerCharacters":["<"," ","=","\"","'","{",".",",","/"]},
                "hoverProvider":true,"definitionProvider":true,"documentSymbolProvider":true,"documentFormattingProvider":true,
                "semanticTokensProvider":{"legend":{"tokenTypes":["type","property","string"],"tokenModifiers":[]},"full":true},
                "documentOnTypeFormattingProvider":{"firstTriggerCharacter":">"}},
                "serverInfo":{"name":"Sunfleck Fleck Language Server"}}
                """)!;
            result["serverInfo"]!["version"] = typeof(LspServer).Assembly.GetName().Version?.ToString(3);
            return result;
        }
        if (method == "shutdown")
        {
            shutdown = true;
            return null;
        }
        if (shutdown)
        {
            throw new InvalidOperationException("Language server has shut down.");
        }
        if (method is "initialized" or "$/cancelRequest" or "$/setTrace")
        {
            return null;
        }
        if (method == "workspace/didChangeConfiguration")
        {
            var settings = parameters?["settings"]?["sunfleck"] ?? parameters?["settings"];
            ConfigureModels(settings);
            PublishAll();
            return null;
        }
        if (method == "workspace/didChangeWatchedFiles")
        {
            catalog.Reload();
            foreach (var scope in scopes)
            {
                scope.Catalog.Reload();
            }
            PublishAll();
            return null;
        }
        if (method == "sunfleck/setModels")
        {
            catalog.SetDynamic(parameters!["models"]!.AsArray());
            dynamicModels = parameters["models"]!.DeepClone().AsArray();
            foreach (var scope in scopes)
            {
                scope.Catalog.SetDynamic(dynamicModels);
            }
            PublishAll();
            return null;
        }
        if (method is "sunfleck/debugSnapshot" or "sunfleck/debugDisconnect")
        {
            var identity = RuntimeModels.Identity(parameters);
            if (method == "sunfleck/debugDisconnect")
            {
                if (runtimeModels.Remove(identity))
                {
                    PublishAll();
                }
                return new JsonObject();
            }
            try
            {
                var snapshot = inspector.Handle(method, parameters);
                if (runtimeModels.Update(identity, snapshot))
                {
                    PublishAll();
                }
                return snapshot;
            }
            catch
            {
                if (runtimeModels.Remove(identity))
                {
                    PublishAll();
                }
                throw;
            }
        }
        if (method is "sunfleck/debugRequest" or "sunfleck/debugLocate")
        {
            return inspector.Handle(method, parameters);
        }
        string uri = parameters?["textDocument"]?["uri"]?.GetValue<string>() ?? throw new NotSupportedException($"Unsupported method: {method}");
        if (method == "textDocument/didClose")
        {
            documents.Remove(uri);
            Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray() });
            return null;
        }
        if (method is "textDocument/didOpen" or "textDocument/didChange")
        {
            int version = parameters!["textDocument"]!["version"]!.GetValue<int>();
            if (documents.TryGetValue(uri, out var previous) && version <= previous.Version)
            {
                return null;
            }
            string text;
            if (method == "textDocument/didOpen")
            {
                text = parameters["textDocument"]!["text"]!.GetValue<string>();
            }
            else
            {
                var changes = parameters["contentChanges"]!.AsArray();
                if (changes.Count == 0)
                {
                    return null;
                }
                if (changes.Any(change => change?["range"] is not null))
                {
                    throw new ArgumentException("This server negotiated full document synchronization.");
                }
                text = changes.Last()!["text"]!.GetValue<string>();
            }
            documents[uri] = (new FleckDocument(text), version);
            Publish(uri);
            return null;
        }
        if (!documents.TryGetValue(uri, out var current))
        {
            throw new ArgumentException("Document is not open.");
        }
        var document = current.Document;
        if (method == "textDocument/documentSymbol")
        {
            return LanguageFeatures.Symbols(document);
        }
        if (method == "textDocument/semanticTokens/full")
        {
            return LanguageFeatures.Tokens(document);
        }
        if (method == "textDocument/formatting")
        {
            return LanguageFeatures.Format(document, parameters?["options"]);
        }
        int offset = document.Offset(parameters!["position"]!);
        var activeCatalog = CatalogFor(uri);
        activeCatalog.LoadComponents(uri);
        return method switch
        {
            "textDocument/completion" => LanguageFeatures.Complete(activeCatalog, document, offset, snippets, uri),
            "textDocument/hover" => LanguageFeatures.Hover(activeCatalog, document, offset),
            "textDocument/definition" => LanguageFeatures.Definition(activeCatalog, document, offset, uri),
            "textDocument/onTypeFormatting" => LanguageFeatures.CloseTag(document, offset),
            _ => throw new NotSupportedException($"Unsupported method: {method}")
        };
    }

    private void PublishAll()
    {
        foreach (string uri in documents.Keys)
        {
            Publish(uri);
        }
        foreach (string error in catalog.Errors.Concat(scopes.SelectMany(scope => scope.Catalog.Errors)).Distinct())
        {
            Notify("window/logMessage", new JsonObject { ["type"] = 2, ["message"] = error });
        }
    }
    private void Publish(string uri)
    {
        var (document, version) = documents[uri];
        var activeCatalog = CatalogFor(uri);
        activeCatalog.LoadComponents(uri);
        Notify("textDocument/publishDiagnostics", new JsonObject
        {
            ["uri"] = uri, ["version"] = version, ["diagnostics"] = LanguageFeatures.Diagnostics(activeCatalog, document, uri)
        });
    }

    private void Notify(string method, JsonObject parameters)
        => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
    private void Send(JsonObject message) => Write(output, message);

    internal static void Write(Stream output, JsonNode message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        output.Write(header);
        output.Write(bytes);
        output.Flush();
    }

    internal static JsonObject? Read(Stream input)
    {
        int length = -1, total = 0;
        while (true)
        {
            var line = new StringBuilder();
            while (true)
            {
                int value = input.ReadByte();
                if (value < 0)
                {
                    if (total == 0)
                    {
                        return null;
                    }
                    throw new EndOfStreamException("Truncated LSP header.");
                }
                if (++total > 8192)
                {
                    throw new InvalidDataException("LSP header exceeds 8 KiB.");
                }
                if (value == '\n')
                {
                    break;
                }
                line.Append((char)value);
            }
            string header = line.ToString().TrimEnd('\r');
            if (header.Length == 0)
            {
                break;
            }
            int colon = header.IndexOf(':');
            if (colon > 0 && header[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (length >= 0 || !int.TryParse(header[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0 || length > 4 * 1024 * 1024)
                {
                    throw new InvalidDataException("Invalid LSP Content-Length.");
                }
            }
        }
        if (length < 0)
        {
            throw new InvalidDataException("Missing LSP Content-Length.");
        }
        var bytes = new byte[length];
        input.ReadExactly(bytes);
        return JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("Expected a JSON-RPC object.");
    }
}
