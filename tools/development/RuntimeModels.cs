using System.Text.Json.Nodes;

namespace Sunfleck.Development;

// Inspector owners supply complete snapshots. Keep their metadata separate from
// configured files and explicit setModels, and apply it only to the exact source.
internal sealed class RuntimeModels
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Snapshot(Dictionary<string, Dictionary<string, JsonObject?>> Sources);
    private readonly Dictionary<string, Dictionary<string, Snapshot>> snapshots = new(Paths);
    private Dictionary<string, Dictionary<string, JsonObject?>> sources = new(Paths);

    internal static string SourceUri(string value)
    {
        if (!Path.IsPathFullyQualified(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile) { return uri.AbsoluteUri; }
        // Uri's filesystem constructor can treat a literal "%20" as an
        // existing escape. Encode each path segment explicitly, exactly once.
        string path = Path.GetFullPath(value).Replace('\\', '/');
        static string Encode(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        if (path.StartsWith("//", StringComparison.Ordinal)) { return "file:" + Encode(path); }
        if (path.StartsWith('/')) { return "file://" + Encode(path); }
        return "file:///" + path[..2] + Encode(path[2..]);
    }

    internal static string? SourcePath(string value)
    {
        if (!Path.IsPathFullyQualified(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            if (uri.IsUnc)
            {
                return null;
            }
            value = uri.LocalPath;
            // VS Code encodes the drive colon (file:///c%3A/...). .NET decodes
            // it but retains the URI's leading slash. Decode only once so a
            // literal percent escape in a filename remains part of the name.
            if (OperatingSystem.IsWindows() && value.Length >= 4 && value[0] == '/' &&
                char.IsAsciiLetter(value[1]) && value[2] == ':' && value[3] is '/' or '\\')
            {
                value = value[1..];
            }
        }
        return Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal)
            ? Path.GetFullPath(value) : null;
    }

    internal static (string Pipe, string Owner) Identity(JsonNode? parameters)
    {
        string pipe = DebugInspector.PipeName(parameters);
        string owner = parameters?["owner"]?.GetValue<string>() ?? "";
        if (owner.Length > 128 || (parameters?["owner"] is not null && string.IsNullOrWhiteSpace(owner)))
        {
            throw new ArgumentException("Inspector owner must contain 1 to 128 characters.");
        }
        return (pipe, owner);
    }

    internal LanguageCatalog For(string uri, LanguageCatalog catalog) => SourcePath(uri) is { } path && sources.TryGetValue(path, out var models)
        ? catalog.WithRuntime(models) : catalog;

    internal bool Update((string Pipe, string Owner) identity, JsonObject snapshot)
    {
        var updated = new Dictionary<string, Dictionary<string, JsonObject?>>(Paths);
        var visited = new Dictionary<string, HashSet<(string Name, string Id)>>(Paths);
        var registry = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var model in snapshot["models"]?.AsArray() ?? [])
        {
            if (model is not JsonObject entry || entry["id"] is null || !registry.TryAdd(entry["id"]!.GetValue<string>(), entry))
            {
                throw new InvalidDataException("Duplicate or missing runtime model identity.");
            }
        }
        foreach (var node in snapshot["nodes"]!.AsArray())
        {
            var source = node?["source"];
            string? name = source?["model"]?.GetValue<string>();
            string? id = source?["modelId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || id is null or "0" || SourcePath(source?["uri"]?.GetValue<string>() ?? "") is not { } path)
            {
                continue; // Legacy snapshots and unnamed/code pages keep configured metadata.
            }
            if (!registry.TryGetValue(id, out var model))
            {
                // A model may have been destroyed while its page remains alive.
                continue;
            }
            if (!visited.TryGetValue(path, out var identities))
            {
                visited[path] = identities = [];
            }
            if (!identities.Add((name, id)))
            {
                continue;
            }
            var metadata = new JsonObject { ["name"] = name, ["properties"] = model["properties"]?.DeepClone() };
            LanguageCatalog.ValidateModel(metadata);
            Merge(updated, path, name, metadata);
        }
        if (!snapshots.TryGetValue(identity.Pipe, out var owners))
        {
            snapshots[identity.Pipe] = owners = new(StringComparer.Ordinal);
        }
        owners[identity.Owner] = new Snapshot(updated);
        return Rebuild();
    }

    internal bool Remove((string Pipe, string Owner) identity)
    {
        if (!snapshots.TryGetValue(identity.Pipe, out var owners) || !owners.Remove(identity.Owner))
        {
            return false;
        }
        if (owners.Count == 0)
        {
            snapshots.Remove(identity.Pipe);
        }
        return Rebuild();
    }

    private static void Merge(Dictionary<string, Dictionary<string, JsonObject?>> target, string path, string name, JsonObject? metadata)
    {
        if (!target.TryGetValue(path, out var models))
        {
            target[path] = models = new(StringComparer.Ordinal);
        }
        if (models.TryGetValue(name, out var previous) && !JsonNode.DeepEquals(previous, metadata))
        {
            models[name] = null; // Preserve ambiguity rather than picking an instance.
        }
        else
        {
            models[name] = metadata;
        }
    }

    private bool Rebuild()
    {
        var updated = new Dictionary<string, Dictionary<string, JsonObject?>>(Paths);
        foreach (var owners in snapshots.Values)
        {
            foreach (var snapshot in owners.Values)
            {
                foreach (var (path, models) in snapshot.Sources)
                {
                    foreach (var (name, metadata) in models)
                    {
                        Merge(updated, path, name, metadata);
                    }
                }
            }
        }
        bool changed = sources.Count != updated.Count || sources.Any(source =>
            !updated.TryGetValue(source.Key, out var models) || source.Value.Count != models.Count ||
            source.Value.Any(model => !models.TryGetValue(model.Key, out var metadata) || !JsonNode.DeepEquals(model.Value, metadata)));
        sources = updated;
        return changed;
    }
}
