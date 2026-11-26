using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Sunfleck.Development;

// Both editor adapters use these operations over the existing stdio connection.
// Pipe exchanges have a bounded wait even when the app is paused.
internal sealed class DebugInspector
{
    internal JsonObject Handle(string method, JsonNode? parameters)
    {
        string pipe = PipeName(parameters);
        return method switch
        {
            "sunfleck/debugSnapshot" => ReadPipe(pipe),
            "sunfleck/debugRequest" => PipeRequest(pipe, "request", parameters!.AsObject()),
            "sunfleck/debugLocate" => Locate(ReadPipe(pipe), parameters!),
            _ => throw new NotSupportedException($"Unsupported inspector method: {method}")
        };
    }

    internal static string PipeName(JsonNode? parameters)
    {
        string? pipe = parameters?["pipe"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(pipe)) { throw new ArgumentException("A local debug pipe name is required."); }
        if (pipe.Length > 256 || pipe.Any(char.IsWhiteSpace) || pipe.Contains('\\') || pipe.Contains('/'))
        {
            throw new ArgumentException("A local debug pipe name is required.");
        }
        return pipe;
    }

    private static JsonObject PipeRequest(string pipe, string method, JsonObject parameters)
    {
        try { return ExchangePipeAsync(pipe, method, parameters).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            // An app that has not started (or is stopped at a breakpoint) is not
            // a language-server failure. Let the inspector retry on its next poll.
            throw new IOException("The application debug service is not available yet or did not respond in time.", error);
        }
    }

    private static async Task<JsonObject> ExchangePipeAsync(string pipe, string method, JsonObject parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var request = new JsonObject { ["method"] = method };
        if (method == "request")
        {
            request["request"] = parameters["method"]?.GetValue<string>() ?? throw new ArgumentException("Debug request method is required.");
            if (parameters["object"] is { } objectId) { request["object"] = objectId.DeepClone(); }
        }
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("The debug service closed the connection.");
        var response = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("Invalid debug service response.");
        if (response["error"] is { } error)
        {
            throw new InvalidOperationException(error.GetValue<string>());
        }
        return response["result"] is { } result ? result.DeepClone()!.AsObject() : response;
    }

    private static JsonObject ReadPipe(string pipe) => Project(PipeRequest(pipe, "snapshot", new JsonObject()));

    private static bool ObjectId(string value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) &&
        number != 0 && number.ToString(CultureInfo.InvariantCulture) == value;

    internal static JsonObject Project(JsonObject snapshot)
    {
        if (snapshot["version"]?.GetValue<int>() != 1 || snapshot["timestamp"]?.GetValue<long>() is null ||
            snapshot["nodes"] is not JsonArray nodes || snapshot["windows"] is not JsonArray windows)
        {
            throw new InvalidDataException("Unsupported runtime snapshot.");
        }
        var ids = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var windowIds = windows.Select(window => window!["id"]!.GetValue<int>()).ToHashSet();
        if (windowIds.Count != windows.Count || windowIds.Any(id => id < 0))
        {
            throw new InvalidDataException("Duplicate or invalid window identity.");
        }
        foreach (var node in nodes)
        {
            string id = node!["id"]!.GetValue<string>();
            if (!ObjectId(id) || !ids.TryAdd(id, node) || !windowIds.Contains(node["window"]!.GetValue<int>()))
            {
                throw new InvalidDataException("Duplicate or invalid runtime object identity.");
            }
        }
        var edges = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, node) in ids)
        {
            string parent = node["parent"]?.GetValue<string>() ?? "0";
            if (parent == id || !ids.TryGetValue(parent, out var owner) || owner["window"]!.GetValue<int>() != node["window"]!.GetValue<int>())
            {
                parent = "0";
            }
            edges[id] = parent;
        }
        // Visit each edge once, including code-created trees deeper than XML's limit.
        var visited = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string id in ids.Keys)
        {
            string ancestor = id;
            var route = new List<string>();
            while (ancestor != "0" && !visited.ContainsKey(ancestor))
            {
                visited[ancestor] = false;
                route.Add(ancestor);
                ancestor = edges[ancestor];
            }
            if (ancestor != "0" && !visited[ancestor])
            {
                edges[route[^1]] = "0";
            }
            foreach (string entry in route)
            {
                visited[entry] = true;
            }
        }
        var parents = new JsonObject();
        foreach (var (id, parent) in edges)
        {
            parents[id] = parent;
        }
        // Include registered model metadata without calling application getters.
        var models = new JsonObject();
        foreach (var model in snapshot["models"]?.AsArray() ?? [])
        {
            if (model?["id"] is { } id)
            {
                models[id.GetValue<string>()] = model.DeepClone();
            }
        }
        snapshot["inspector"] = new JsonObject { ["parents"] = parents, ["models"] = models };
        return snapshot;
    }

    internal static JsonObject Locate(JsonObject snapshot, JsonNode parameters)
    {
        static string? SourcePath(string? source) => source is null ? null : RuntimeModels.SourcePath(source);
        string path = SourcePath(parameters["uri"]!.GetValue<string>()) ?? throw new ArgumentException("An absolute local source URI is required.");
        int line = parameters["line"]!.GetValue<int>(), column = parameters["column"]!.GetValue<int>();
        if (line < 1 || column < 1)
        {
            throw new ArgumentException("Source positions start at one.");
        }
        var matches = new List<JsonNode>();
        foreach (var node in snapshot["nodes"]!.AsArray())
        {
            var source = node!["source"];
            if (!string.Equals(SourcePath(source?["uri"]?.GetValue<string>()), path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || source?["range"] is not JsonObject range)
            {
                continue;
            }
            int startLine = range["line"]?.GetValue<int>() ?? 0, startColumn = range["column"]?.GetValue<int>() ?? 0;
            int endLine = range["endLine"]?.GetValue<int>() ?? 0, endColumn = range["endColumn"]?.GetValue<int>() ?? 0;
            if (startLine > 0 && startColumn > 0 && endLine >= startLine && endColumn > 0 &&
                line >= startLine && line <= endLine && (line != startLine || column >= startColumn) &&
                (line != endLine || column < endColumn))
            {
                matches.Add(node);
            }
        }
        return new JsonObject { ["nodes"] = new JsonArray(matches.OrderBy(node => node["source"]!["range"]!["length"]?.GetValue<long>() ?? long.MaxValue)
            .Select(node => node.DeepClone()).ToArray()) };
    }

}
