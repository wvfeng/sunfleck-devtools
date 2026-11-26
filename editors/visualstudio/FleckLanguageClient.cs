using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Sunfleck.VisualStudio;

internal static class FleckContentType
{
    [Export, Name("fleck"), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteBaseTypeName)]
    internal static ContentTypeDefinition? ContentType = null;

    [Export, FileExtension(".fleck"), ContentType("fleck")]
    internal static FileExtensionToContentTypeDefinition? Extension = null;
}

[Export(typeof(ILanguageClient)), ContentType("fleck")]
public sealed class FleckLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
{
    private static FleckLanguageClient? active;
    private Process? server;
    private JsonRpc? rpc;
    private Timer? modelTimer;
    private int updatingModels;
    private string? modelSignature;
    public object? MiddleLayer => null;
    public object? CustomMessageTarget => null;
    public Task AttachForCustomMessageAsync(JsonRpc connection)
    {
        rpc = connection;
        active = this;
        return Task.CompletedTask;
    }
    internal static Task<JObject?> DebugAsync(string method, object parameters)
    {
        var connection = active?.rpc;
        return connection is null ? Task.FromResult<JObject?>(null) : connection.InvokeWithParameterObjectAsync<JObject?>(method, parameters);
    }
    public string Name => "Sunfleck Fleck";
    public IEnumerable<string> ConfigurationSections => Array.Empty<string>();
    public object InitializationOptions => new
    {
        modelFiles = (Environment.GetEnvironmentVariable("SUNFLECK_MODEL_FILES") ?? "").Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries),
        language = SnapshotMargin.Language
    };
    public IEnumerable<string> FilesToWatch => new[] { "**/*.fleckmodel.json", "**/*.fleckcomponents.json", "**/*.fleck", "**/*.cs", "**/*.csproj" };
    public bool ShowNotificationOnInitializeFailed => true;
    public event AsyncEventHandler<EventArgs>? StartAsync;
#pragma warning disable CS0067 // Required host contract; the client does not request a server stop.
    public event AsyncEventHandler<EventArgs>? StopAsync;
#pragma warning restore CS0067

    public Task<Connection?> ActivateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        modelTimer?.Dispose();
        modelTimer = null;
        rpc = null;
        modelSignature = null;
        if (server is not null)
        {
            if (!server.HasExited)
            {
                server.Kill();
            }
            server.Dispose();
        }
        string folder = Path.GetDirectoryName(typeof(FleckLanguageClient).Assembly.Location)!;
        string tool = Environment.GetEnvironmentVariable("SUNFLECK_DEVELOPMENT_TOOL") ?? Path.Combine(folder, "server", "Sunfleck.Development.dll");
        if (!File.Exists(tool))
        {
            throw new FileNotFoundException(SnapshotMargin.Language == "en" ? "Build sunfleck_development or set SUNFLECK_DEVELOPMENT_TOOL." :
                "请构建 sunfleck_development 或设置 SUNFLECK_DEVELOPMENT_TOOL。", tool);
        }
        server = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("SUNFLECK_DOTNET") ?? "dotnet",
                Arguments = "\"" + tool + "\" lsp",
                WorkingDirectory = Path.GetDirectoryName(tool)!,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        server.ErrorDataReceived += (_, args) => Trace.WriteLine(args.Data, "Sunfleck");
        if (!server.Start())
        {
            throw new InvalidOperationException(SnapshotMargin.Language == "en" ? "Fleck language server did not start." : "Fleck 语言服务未启动。");
        }
        server.BeginErrorReadLine();
        return Task.FromResult<Connection?>(new Connection(server.StandardOutput.BaseStream, server.StandardInput.BaseStream));
    }

    public async Task OnLoadedAsync()
    {
        if (StartAsync is { } start)
        {
            await start.InvokeAsync(this, EventArgs.Empty);
        }
    }
    public async Task OnServerInitializedAsync()
    {
        await RefreshModelsAsync();
        modelTimer = new Timer(state => { _ = RefreshModelsAsync(); }, null, 2000, 2000);
    }

    private async Task RefreshModelsAsync()
    {
        if (Interlocked.Exchange(ref updatingModels, 1) != 0)
        {
            return;
        }
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var automation = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            EnvDTE.Solution? solution = null;
            if (automation is not null)
            {
                solution = automation.Solution;
            }
            string? solutionRoot = null;
            if (solution is not null && !string.IsNullOrEmpty(solution.FullName))
            {
                solutionRoot = Path.GetDirectoryName(solution.FullName);
            }
            string configured = Environment.GetEnvironmentVariable("SUNFLECK_MODEL_FILES") ?? "";
            if (configured.Length == 0 && Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell)
            {
                var packageId = typeof(FleckFormattingPackage).GUID;
                if (shell.LoadPackage(ref packageId, out var package) >= 0 && package is FleckFormattingPackage settings)
                {
                    configured = settings.ModelFiles;
                }
            }
            var projects = new List<(string Root, string Configuration, string[] Configurations)>();
            if (configured.Length == 0 && solution is not null && solution.IsOpen)
            {
                foreach (EnvDTE.Project project in solution.Projects)
                {
                    CollectProjects(project, projects);
                }
            }
            string language = SnapshotMargin.Language;
            await TaskScheduler.Default;
            var scopes = new JArray();
            var watched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var explicitFiles = FleckModelDiscovery.Configured(configured, solutionRoot);
            watched.UnionWith(explicitFiles);
            foreach (var project in projects.GroupBy(project => project.Root, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                string[] files = FleckModelDiscovery.Discover(project.Root, project.Configuration, project.Configurations, projects.Select(item => item.Root));
                watched.UnionWith(files);
                scopes.Add(new JObject { ["uri"] = new Uri(project.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri,
                    ["modelFiles"] = new JArray(files) });
            }
            var configuration = new JObject { ["modelFiles"] = new JArray(explicitFiles), ["modelScopes"] = scopes, ["language"] = language };
            string signature = configuration.ToString(Formatting.None) + string.Concat(watched.Select(file =>
                File.Exists(file) ? $"|{file}:{File.GetLastWriteTimeUtc(file).Ticks}:{new FileInfo(file).Length}" : $"|{file}:missing"));
            if (signature != modelSignature && rpc is { } connection)
            {
                await connection.NotifyWithParameterObjectAsync("workspace/didChangeConfiguration", new { settings = new { sunfleck = configuration } });
                if (ReferenceEquals(rpc, connection))
                {
                    modelSignature = signature;
                }
            }
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Trace.WriteLine(error.Message, "Sunfleck model discovery");
        }
        finally
        {
            Volatile.Write(ref updatingModels, 0);
        }
    }

    private static void CollectProjects(EnvDTE.Project project, List<(string Root, string Configuration, string[] Configurations)> projects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!string.IsNullOrEmpty(project.FullName) && File.Exists(project.FullName))
            {
                var manager = project.ConfigurationManager;
                string active = manager?.ActiveConfiguration?.ConfigurationName ?? "";
                var names = (manager?.ConfigurationRowNames as Array)?.Cast<string>().ToArray() ?? Array.Empty<string>();
                projects.Add((Path.GetDirectoryName(project.FullName)!, active, names));
                return;
            }
            if (project.ProjectItems is { } items)
            {
                foreach (EnvDTE.ProjectItem item in items)
                {
                    if (item.SubProject is { } child)
                    {
                        CollectProjects(child, projects);
                    }
                }
            }
        }
        catch (COMException)
        {
            // Unloaded and unsupported project types may not expose configuration data.
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(active, this))
        {
            active = null;
        }
        modelTimer?.Dispose();
        modelTimer = null;
        rpc = null;
        if (server is not null)
        {
            if (!server.HasExited)
            {
                server.Kill();
            }
            server.Dispose();
            server = null;
        }
    }
    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        => Task.FromResult<InitializationFailureContext?>(new InitializationFailureContext());
}
