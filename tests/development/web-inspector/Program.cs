using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sunfleck.VisualStudio;

bool verify = args.Contains("--verify", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(verify ? Array.Empty<string>() : args);
if (verify)
{
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
const string endpoint = "/__sunfleck/snapshot";
const string editorHeader = "X-Sunfleck-Inspector";
const string pageHeader = "X-Sunfleck-Page";
var fixture = new FixtureSnapshot(app.Environment.ContentRootPath);
var gate = new object();
Guid page = Guid.Empty;
string highlight = "0";
string? pick = null;
string? selectedRequest = null;
string selection = "0";
DateTimeOffset highlightedUntil = default;
DateTimeOffset pickedUntil = default;

app.MapGet(endpoint, (HttpContext context) =>
{
    lock (gate)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (context.Request.Headers[editorHeader] == "1")
        {
            if (page == Guid.Empty)
            {
                return Results.NoContent();
            }
            context.Response.Headers[pageHeader] = page.ToString("D");
            context.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(fixture.Read(highlightedUntil > now ? highlight : "0", selectedRequest, selection), "application/json");
        }
        if (Guid.TryParse(context.Request.Headers[pageHeader].ToString(), out var sender) && sender == page)
        {
            return Results.Json(new { highlight = highlightedUntil > now ? highlight : "0", pick = pickedUntil > now ? pick : null });
        }
        return Results.NotFound();
    }
});

app.MapPost(endpoint, async (HttpContext context) =>
{
    if (!Guid.TryParse(context.Request.Headers[pageHeader].ToString(), out var sender) || sender == Guid.Empty)
    {
        return Results.BadRequest();
    }
    var command = await context.Request.ReadFromJsonAsync<Command>(context.RequestAborted);
    lock (gate)
    {
        bool fromEditor = context.Request.Headers[editorHeader] == "1";
        if (!fromEditor && context.Request.Headers.Origin != $"http://{context.Request.Host}")
        {
            return Results.NotFound();
        }
        if (!fromEditor && command?.Action == "ready")
        {
            if (sender != page)
            {
                page = sender;
                ResetControls();
            }
            return Results.NoContent();
        }
        if (sender != page)
        {
            return Results.Conflict();
        }
        switch (command?.Action)
        {
            case "highlight" when fromEditor && command.Value is "0" or "1" or "2" or "3" or "4" or "5":
                highlight = command.Value;
                highlightedUntil = DateTimeOffset.UtcNow.AddSeconds(1);
                break;
            case "pick" when fromEditor && Guid.TryParse(command.Value, out _):
                pick = command.Value;
                pickedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                break;
            case "selected" when !fromEditor && pick is not null && pick == command.Value && pickedUntil > DateTimeOffset.UtcNow
                && command.Selection is "0" or "3" or "4" or "5":
                selectedRequest = pick;
                selection = command.Selection;
                pick = null;
                break;
            case "clear" when fromEditor:
                ResetControls();
                break;
            default:
                return Results.BadRequest();
        }
        return Results.NoContent();
    }
});

void ResetControls()
{
    highlight = "0";
    pick = null;
    selectedRequest = null;
    selection = "0";
}

if (!verify)
{
    await app.RunAsync();
    return;
}
await app.StartAsync();
try
{
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
    await VerifyAsync(new Uri(addresses.Addresses.Single()), fixture.Root);
}
finally
{
    await app.StopAsync();
}

static async Task VerifyAsync(Uri baseUri, string project)
{
    var endpoint = new Uri(baseUri, "/__sunfleck/snapshot");
    var firstPage = Guid.NewGuid().ToString("D");
    using (var browser = new HttpClient { BaseAddress = baseUri })
    {
        browser.DefaultRequestHeaders.Add("Origin", baseUri.GetLeftPart(UriPartial.Authority));
        browser.DefaultRequestHeaders.Add("X-Sunfleck-Page", firstPage);
        async Task PostAsync(string action, string? value = null, string? selection = null)
        {
            using (var response = await browser.PostAsJsonAsync(endpoint, new Command(action, value, selection)))
            {
                response.EnsureSuccessStatusCode();
            }
        }
        async Task<JsonElement> ControlsAsync()
        {
            return await browser.GetFromJsonAsync<JsonElement>(endpoint);
        }

        Check((await BrowserInspector.ReadAsync(endpoint)).Snapshot is null, "No snapshot before the browser connects");
        await PostAsync("ready");
        var (tree, pageId) = await BrowserInspector.ReadAsync(endpoint);
        Check(pageId == firstPage && tree?["nodes"]?.Count() == 5, "Gzip snapshot and five nodes");
        var node = tree!["nodes"]![2];
        Check((int?)node!["properties"]![0]!["value"] == 144 &&
            (int?)node["properties"]![0]!["defaultValue"] == 0, "Current and default properties");

        var connection = new BrowserInspector(baseUri, project, true);
        var template = tree["nodes"]![4]!["source"]!;
        var templatePath = connection.ResolveRelativeSource((string)template["uri"]!);
        Check(templatePath == Path.Combine(project, "components", "ActionTemplate.fleck") &&
            (int?)template["range"]?["line"] == 3 && (int?)template["range"]?["column"] == 3, "Relative template source location");

        await BrowserInspector.SendAsync(endpoint, firstPage, "highlight", "5");
        Check((await ControlsAsync()).GetProperty("highlight").GetString() == "5", "Hover highlight");
        await BrowserInspector.SendAsync(endpoint, firstPage, "highlight", "0");
        Check((await ControlsAsync()).GetProperty("highlight").GetString() == "0", "Hover clear");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            string request = Guid.NewGuid().ToString("D");
            await BrowserInspector.SendAsync(endpoint, firstPage, "pick", request);
            Check((await ControlsAsync()).GetProperty("pick").GetString() == request, "Pick command");
            string expected = attempt < 2 ? "5" : "0";
            await PostAsync("selected", request, expected);
            var result = (await BrowserInspector.ReadAsync(endpoint)).Snapshot?["browserPick"];
            Check((string?)result?["request"] == request && (string?)result?["selection"] == expected,
                attempt < 2 ? "Pick and repeat pick on the same node" : "Escape cancellation");
        }

        string secondPage = Guid.NewGuid().ToString("D");
        browser.DefaultRequestHeaders.Remove("X-Sunfleck-Page");
        browser.DefaultRequestHeaders.Add("X-Sunfleck-Page", secondPage);
        await PostAsync("ready");
        Check((await BrowserInspector.ReadAsync(endpoint)).Page == secondPage, "Reload reconnects with a new page ID");
        bool rejected = false;
        try
        {
            await BrowserInspector.SendAsync(endpoint, firstPage, "highlight", "5");
        }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            rejected = true;
        }
        Check(rejected, "Obsolete page IDs are rejected");
    }

    static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(label);
        }
        Console.WriteLine($"PASS {label}");
    }
}

sealed record Command(string Action, string? Value, string? Selection);

sealed class FixtureSnapshot
{
    private readonly object[] nodes;
    internal string Root { get; }

    internal FixtureSnapshot(string root)
    {
        Root = root;
        const string main = "Main.fleck";
        const string template = "components/ActionTemplate.fleck";
        var markup = XElement.Load(Path.Combine(root, main), LoadOptions.SetLineInfo);
        var appearance = XElement.Load(Path.Combine(root, template), LoadOptions.SetLineInfo).Elements().Single();
        var panel = markup.Elements().Single();
        var buttons = panel.Elements().ToArray();
        nodes =
        [
            Node("1", "0", "", markup, main, [0, 0, 400, 240]),
            Node("2", "1", "controls", panel, main, [24, 48, 376, 200]),
            Node("3", "2", "primary", buttons[0], main, [48, 76, 192, 122], 144),
            Node("4", "2", "secondary", buttons[1], main, [208, 76, 352, 122], 144),
            Node("5", "3", "", appearance, "ActionTemplate.fleck", [48, 76, 70, 122])
        ];
    }

    private static object Node(string id, string parent, string fleckId, XElement element, string uri, int[] bounds, int? width = null)
    {
        var location = (IXmlLineInfo)element;
        int line = location.LineNumber;
        int column = location.LinePosition - 1;
        return new
        {
            id, parent, fleckId, type = element.Name.LocalName, window = 0, page = "1", bounds, clip = bounds,
            source = new { uri, range = new { line, column, endLine = line, endColumn = column + element.Name.LocalName.Length + 1 } },
            properties = width is int value ? new[] { new { name = "Width", value, defaultValue = 0 } } : null
        };
    }

    internal byte[] Read(string highlight, string? request, string selection)
    {
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                JsonSerializer.Serialize(gzip, new
                {
                    version = 1, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    windows = new[] { new { id = 0, title = "Web Inspector Regression", width = 400, height = 240 } },
                    nodes, highlight,
                    browserPick = request is null ? null : new { request, selection }
                });
            }
            return output.ToArray();
        }
    }
}
