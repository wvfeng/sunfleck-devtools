using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Sunfleck.VisualStudio;

internal sealed class BrowserInspector
{
    internal const string ProjectConfiguration = "sunfleck-inspector.json";
    private const string InspectorHeader = "X-Sunfleck-Inspector";
    private const string PageHeader = "X-Sunfleck-Page";
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip,
        UseProxy = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(2),
        MaxResponseContentBufferSize = 32 * 1024 * 1024
    };
    private readonly Uri preview;
    private readonly string? sourceRoot;
    internal Uri? Endpoint { get; private set; }
    internal bool IsProjectPreview { get; }

    internal BrowserInspector(Uri preview, string? sourceRoot, bool isProjectPreview)
    {
        this.preview = preview;
        this.sourceRoot = sourceRoot;
        IsProjectPreview = isProjectPreview;
        if (isProjectPreview)
        {
            Endpoint = new Uri(preview, "/__sunfleck/snapshot");
        }
    }

    internal static bool TryPreview(string? address, out Uri preview)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttp &&
            parsed.Host == "127.0.0.1" && parsed.UserInfo.Length == 0)
        {
            preview = parsed;
            return true;
        }
        preview = null!;
        return false;
    }

    internal static string? FindSourceRoot(string file)
    {
        for (var directory = new FileInfo(file).Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ProjectConfiguration)) ||
                File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }
        return Path.GetDirectoryName(file);
    }

    internal static string? ReadProjectPreview(string root)
    {
        try
        {
            string file = Path.Combine(root, ProjectConfiguration);
            return File.Exists(file) ? (string?)JObject.Parse(File.ReadAllText(file))["url"] : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Open()
    {
        string? session = IsProjectPreview ? null : Guid.NewGuid().ToString("D");
        var query = new List<string>();
        foreach (string parameter in preview.Query.TrimStart('?').Split('&'))
        {
            string key = Uri.UnescapeDataString(parameter.Split('=')[0]);
            if (parameter.Length != 0 && key is not ("sunfleckInspector" or "sunfleckInspectorSession"))
            {
                query.Add(parameter);
            }
        }
        query.Add("sunfleckInspector=1");
        if (session is not null)
        {
            query.Add("sunfleckInspectorSession=" + session);
        }
        var address = new UriBuilder(preview) { Query = string.Join("&", query) };
        using (Process? process = Process.Start(new ProcessStartInfo(address.Uri.AbsoluteUri) { UseShellExecute = true }))
        {
            if (session is not null)
            {
                Endpoint = new Uri(preview, "/__sunfleck/snapshot?session=" + session);
            }
        }
    }

    internal static async Task<(JObject? Snapshot, string? Page)> ReadAsync(Uri endpoint)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
        {
            request.Headers.Add(InspectorHeader, "1");
            using (var response = await Client.SendAsync(request).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return (null, null);
                }
                string? page = null;
                if (response.Headers.TryGetValues(PageHeader, out var headers))
                {
                    foreach (string header in headers)
                    {
                        if (Guid.TryParseExact(header, "D", out _))
                        {
                            page = header;
                        }
                    }
                }
                return (JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)), page);
            }
        }
    }

    internal static async Task SendAsync(Uri endpoint, string page, string action, string? value = null)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            request.Headers.Add(InspectorHeader, "1");
            request.Headers.Add(PageHeader, page);
            request.Content = new StringContent(JsonConvert.SerializeObject(new { action, value }), Encoding.UTF8, "application/json");
            using (var response = await Client.SendAsync(request).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
            }
        }
    }

    internal string? ResolveRelativeSource(string source)
    {
        if (sourceRoot is null || source.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || source.Contains(":"))
        {
            return null;
        }
        string[] parts = source.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || Array.Exists(parts, part => part == ".." || (part != "." && part.EndsWith(".", StringComparison.Ordinal)) ||
            part != part.Trim() || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return null;
        }
        string relative = Path.Combine(parts);
        string direct = Path.Combine(sourceRoot, relative);
        if (File.Exists(direct))
        {
            return direct;
        }

        // Runtime template locations can use names relative to the resource loader.
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        string? match = null;
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string candidate = Path.GetFullPath(Path.Combine(directory, relative));
            if (File.Exists(candidate) && !string.Equals(candidate, match, StringComparison.OrdinalIgnoreCase))
            {
                if (match is not null)
                {
                    return null;
                }
                match = candidate;
            }
            try
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    if (name is not (".git" or ".vs" or "out" or "obj" or "bin" or "node_modules") &&
                        (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Other source directories are still searchable.
            }
        }
        return match;
    }
}

