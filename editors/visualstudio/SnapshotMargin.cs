using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Sunfleck.VisualStudio;

[Export(typeof(IWpfTextViewMarginProvider))]
[Name("SunfleckSnapshot")]
[MarginContainer(PredefinedMarginNames.Right)]
[ContentType("fleck")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class SnapshotMarginProvider : IWpfTextViewMarginProvider
{
    [Import]
    internal ITextDocumentFactoryService Documents = null!;

    private readonly Dictionary<string, BrowserInspector> browsers = new(StringComparer.OrdinalIgnoreCase);

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container)
    {
        string? pipe = Environment.GetEnvironmentVariable("SUNFLECK_DEBUG_PIPE");
        string? address = Environment.GetEnvironmentVariable("SUNFLECK_DEBUG_WEB_URL");
        string? root = null;
        if (Documents.TryGetTextDocument(host.TextView.TextBuffer, out var document))
        {
            root = BrowserInspector.FindSourceRoot(document.FilePath);
        }
        string? configuredRoot = Environment.GetEnvironmentVariable("SUNFLECK_DEBUG_WEB_ROOT");
        if (configuredRoot is not null && Path.IsPathRooted(configuredRoot) && Directory.Exists(configuredRoot))
        {
            root = Path.GetFullPath(configuredRoot);
        }
        bool isProjectPreview = false;
        if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(pipe) && root is not null)
        {
            address = BrowserInspector.ReadProjectPreview(root);
            isProjectPreview = BrowserInspector.TryPreview(address, out _);
        }
        if (BrowserInspector.TryPreview(address, out var preview))
        {
            string key = $"{preview.AbsoluteUri}|{root}";
            if (!browsers.TryGetValue(key, out var browser))
            {
                browser = new BrowserInspector(preview, root, isProjectPreview);
                browsers.Add(key, browser);
            }
            return new SnapshotMargin(null, browser, host.TextView, Documents);
        }
        return string.IsNullOrWhiteSpace(pipe) ? null : new SnapshotMargin(pipe, null, host.TextView, Documents);
    }
}

internal sealed class SnapshotMargin : IWpfTextViewMargin
{
    private const string Name = "SunfleckSnapshot";
    private static readonly TimeSpan WebSnapshotLifetime = TimeSpan.FromSeconds(5);
    private readonly string? pipe;
    private readonly BrowserInspector? browser;
    private readonly Button openBrowser = new() { Margin = new Thickness(6) };
    private readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Uri? browserEndpoint;
    private string? browserPage;
    private string? browserPick;
    private DateTimeOffset pickDeadline;
    private string? lastHighlight;
    private bool highlighting;
    private readonly IWpfTextView editor;
    private readonly ITextDocumentFactoryService documents;
    private readonly DockPanel panel = new() { Width = 395 };
    private readonly TextBlock status = new() { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock refreshStatus = new() { Margin = new Thickness(6, 0, 6, 6) };
    private readonly TreeView tree = new();
    private readonly Style treeRow = SnapshotProperties.RowStyle(typeof(TreeViewItem), TreeViewItem.IsSelectedProperty);
    private static bool english;
    private static event Action? LanguageChanged;
    private bool updatingLanguage;
    private readonly ComboBox language = new() { Margin = new Thickness(6) };
    private readonly Button pick = new() { Margin = new Thickness(6) };
    private readonly Button sourceButton = new() { ToolTip = "Ctrl+Alt+Shift+F12", Margin = new Thickness(6) };
    private readonly Button locate = new() { Margin = new Thickness(6) };
    private readonly Button launch = new() { Margin = new Thickness(6) };
    private static string Tr(string chinese, string englishText) => english ? englishText : chinese;
    internal static string Language => english ? "en" : "zh-CN";
    private readonly List<TreeViewItem> sourceItems = new();
    private readonly Dictionary<TreeViewItem, TreeViewItem> treeParents = new();
    private readonly SnapshotProperties details = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private long? timestamp;
    private string? selected;
    private string? previousRuntimeSelection;
    private bool disposed;
    private bool reading;
    private readonly string inspectorOwner = Guid.NewGuid().ToString("N");
    private JObject? currentSnapshot;
    private Process? developmentLauncher;
    private string? launcherPath;

    internal SnapshotMargin(string? pipe, BrowserInspector? browser, IWpfTextView editor, ITextDocumentFactoryService documents)
    {
        this.pipe = pipe;
        this.browser = browser;
        this.editor = editor;
        this.documents = documents;
        panel.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        panel.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        SnapshotProperties.UseTheme(tree);
        tree.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Sunfleck.VisualStudio;component/InspectorTreeStyles.xaml", UriKind.Relative)
        });
        language.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var button in new[] { openBrowser, pick, sourceButton, locate, launch })
        {
            button.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ButtonStyleKey);
        }
        language.Items.Add("简体中文");
        language.Items.Add("English");
        language.SelectionChanged += LanguageSelected;
        DockPanel.SetDock(language, Dock.Top);
        panel.Children.Add(language);
        if (browser is not null)
        {
            openBrowser.Click += (_, _) => OpenBrowser();
            DockPanel.SetDock(openBrowser, Dock.Top);
            panel.Children.Add(openBrowser);
            pick.IsEnabled = false;
            hoverTimer.Tick += HoverTick;
            hoverTimer.Start();
        }
        pick.Click += (_, _) => RequestPickAsync().Forget();
        sourceButton.Click += (_, _) =>
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            GoToSource();
        };
        locate.Click += (_, _) => FindAtCaret(locate);
        locate.ToolTip = "Ctrl+Alt+Shift+F10";
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(refreshStatus, Dock.Top);
        DockPanel.SetDock(pick, Dock.Top);
        DockPanel.SetDock(sourceButton, Dock.Top);
        DockPanel.SetDock(locate, Dock.Top);
        panel.Children.Add(status);
        panel.Children.Add(refreshStatus);
        panel.Children.Add(pick);
        panel.Children.Add(sourceButton);
        panel.Children.Add(locate);
        string? launcher = Environment.GetEnvironmentVariable("SUNFLECK_DEVELOPMENT_LAUNCHER");
        if (!string.IsNullOrWhiteSpace(launcher) && Path.IsPathRooted(launcher) &&
            string.Equals(Path.GetExtension(launcher), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            launcherPath = launcher;
            launch.Click += (_, _) => LaunchDevelopment(launcher!);
            launch.ToolTip = "Ctrl+Alt+Shift+F9";
            DockPanel.SetDock(launch, Dock.Top);
            panel.Children.Add(launch);
        }
        var split = new Grid();
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 75 });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 100 });
        var divider = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        divider.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        Grid.SetRow(divider, 1);
        Grid.SetRow(details, 2);
        split.Children.Add(tree);
        split.Children.Add(divider);
        split.Children.Add(details);
        panel.Children.Add(split);
        tree.SelectedItemChanged += SelectItem;
        editor.VisualElement.PreviewKeyDown += EditorKeyDown;
        timer.Tick += Tick;
        LanguageChanged += ApplyLanguage;
        ApplyLanguage();
        timer.Start();
    }

    static SnapshotMargin()
    {
        try
        {
            english = ReadLanguage();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            english = false;
        }
    }

    private static string LanguageFile => Path.Combine(
        Environment.GetEnvironmentVariable("SUNFLECK_VS_SETTINGS_DIRECTORY") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunfleck", "VisualStudio"),
        "language.txt");

    private static bool ReadLanguage() => File.Exists(LanguageFile) && File.ReadAllText(LanguageFile).Trim() == "en";

    private void LanguageSelected(object sender, SelectionChangedEventArgs args)
    {
        if (updatingLanguage || language.SelectedIndex < 0)
        {
            return;
        }
        SetLanguage(language.SelectedIndex == 1);
    }

    private void SetLanguage(bool useEnglish)
    {
        english = useEnglish;
        LanguageChanged?.Invoke();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile)!);
            File.WriteAllText(LanguageFile, english ? "en" : "zh-CN");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = Tr("无法保存界面语言设置", "Could not save the display language setting");
        }
    }

    private void ApplyLanguage()
    {
        updatingLanguage = true;
        language.SelectedIndex = english ? 1 : 0;
        updatingLanguage = false;
        System.Windows.Automation.AutomationProperties.SetName(language, Tr("开发工具界面语言", "Development tools display language"));
        language.ToolTip = Tr("开发工具界面语言（Ctrl+Alt+Shift+F11）", "Development tools display language (Ctrl+Alt+Shift+F11)");
        openBrowser.Content = Tr("打开 Web 调试页面", "Open Web debug page");
        pick.Content = Tr("拾取运行时对象", "Pick running object");
        sourceButton.Content = Tr("定位源码", "Go to source");
        locate.Content = Tr("查找光标处的对象", "Find objects at caret");
        launch.Content = Tr("运行已配置的开发启动器", "Run configured development launcher");
        details.Show((tree.SelectedItem as TreeViewItem)?.Tag as JToken, english, currentSnapshot?["inspector"]?["models"] as JObject);
        timestamp = null;
        RefreshAsync().Forget();
    }

    public FrameworkElement VisualElement => panel;
    public double MarginSize => panel.ActualWidth;
    public bool Enabled => !disposed;
    public ITextViewMargin? GetTextViewMargin(string marginName) => marginName == Name ? this : null;

    private void Tick(object? sender, EventArgs args)
    {
        // VS may defer an outstanding custom LSP response while the debugger is paused.
        // Age the cached capture independently so the panel cannot imply it is live.
        if (reading && timestamp is long captured)
        {
            ShowCaptureTime(captured);
        }
        RefreshAsync().Forget();
    }

    private void OpenBrowser()
    {
        try
        {
            browser?.Open();
            RefreshAsync().Forget();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            status.Text = Tr("无法打开 Web 调试页面", "Could not open the Web debug page") + $" · {error.Message}";
        }
    }

    private void HoverTick(object? sender, EventArgs args) => UpdateHighlightAsync().Forget();

    private async Task UpdateHighlightAsync()
    {
        var endpoint = browserEndpoint;
        var page = browserPage;
        if (endpoint is null || page is null || highlighting)
        {
            return;
        }
        string target = "0";
        if (!disposed && tree.IsVisible && tree.IsMouseOver && timestamp is long captured &&
            DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(captured) <= WebSnapshotLifetime &&
            tree.InputHitTest(Mouse.GetPosition(tree)) is DependencyObject hit &&
            ItemsControl.ContainerFromElement(null, hit) is TreeViewItem item &&
            item.Uid.StartsWith("node:", StringComparison.Ordinal))
        {
            target = item.Uid.Substring("node:".Length);
        }
        if (target == "0" && lastHighlight is null)
        {
            return;
        }
        highlighting = true;
        try
        {
            await BrowserInspector.SendAsync(endpoint, page, "highlight", target);
            if (endpoint == browserEndpoint && page == browserPage)
            {
                lastHighlight = target == "0" ? null : target;
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            lastHighlight = null;
        }
        finally
        {
            highlighting = false;
        }
    }

    private void EditorKeyDown(object sender, KeyEventArgs args)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.F9 && launcherPath is string launcher && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            LaunchDevelopment(launcher);
            args.Handled = true;
            return;
        }
        if (key == Key.F10 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            FindAtCaret(locate);
            args.Handled = true;
            return;
        }
        if (key == Key.F11 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            language.Focus();
            language.IsDropDownOpen = true;
            args.Handled = true;
            return;
        }
        if (key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            SetLanguage(!english);
            args.Handled = true;
            return;
        }
        if (key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            GoToSource();
            args.Handled = true;
        }
    }

    private void LaunchDevelopment(string executable)
    {
        try
        {
            if (developmentLauncher is not null && !developmentLauncher.HasExited)
            {
                return;
            }
            developmentLauncher?.Dispose();
            developmentLauncher = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false
            });
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(error.Message, Tr("Sunfleck 开发启动器", "Sunfleck development launcher"));
        }
    }

    private void FindAtCaret(Button anchor) => FindAtCaretAsync(anchor).Forget();

    private async Task FindAtCaretAsync(Button anchor)
    {
        var caret = editor.Caret.Position.BufferPosition;
        var line = caret.GetContainingLine();
        long row = line.LineNumber + 1, column = caret.Position - line.Start.Position + 1;
        HashSet<string>? matches = null;
        if (pipe is not null && documents.TryGetTextDocument(editor.TextBuffer, out var document))
        {
            try
            {
                var result = await FleckLanguageClient.DebugAsync("sunfleck/debugLocate", new
                {
                    pipe, uri = new Uri(document.FilePath).AbsoluteUri, line = row, column
                });
                if (disposed || editor.Caret.Position.BufferPosition != caret)
                {
                    return;
                }
                if (result?["nodes"] is JArray nodes)
                {
                    matches = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var node in nodes)
                    {
                        matches.Add($"node:{node["id"]}");
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or OperationCanceledException)
            {
                status.Text = Tr("无法查找运行时对象", "Could not locate runtime objects");
                return;
            }
        }
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        TreeViewItem? singleMatch = null;
        var resolvedSources = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in sourceItems)
        {
            if (matches is not null && !matches.Contains(item.Uid))
            {
                continue;
            }
            if (!TrySource(item, out var uri, out var range) ||
                range["endLine"]?.Type != JTokenType.Integer || range["endColumn"]?.Type != JTokenType.Integer)
            {
                continue;
            }
            long start = (long)range["line"]!, end = (long)range["endLine"]!;
            if (row < start || row > end || (row == start && column < (long)range["column"]!) ||
                (row == end && column >= (long)range["endColumn"]!))
            {
                continue;
            }
            if (!resolvedSources.TryGetValue(uri, out var path))
            {
                path = ResolveSource(uri);
                resolvedSources.Add(uri, path);
            }
            if (path is null || !IsCurrentDocument(path))
            {
                continue;
            }
            var option = new MenuItem { Header = $"{AutomationName(item)} ({item.Uid})" };
            singleMatch = item;
            string uid = item.Uid;
            option.Click += (_, _) =>
            {
                var current = sourceItems.Find(candidate => candidate.Uid == uid);
                if (current is not null)
                {
                    Reveal(current, scroll: true);
                }
            };
            menu.Items.Add(option);
        }
        if (menu.Items.Count == 0)
        {
            status.Text = Tr("光标处没有运行时对象", "No running object at caret");
            return;
        }
        if (menu.Items.Count == 1 && singleMatch is not null)
        {
            Reveal(singleMatch, scroll: true);
            return;
        }
        menu.IsOpen = true;
    }

    private void GoToSource(TreeViewItem? item = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        item ??= tree.SelectedItem as TreeViewItem;
        if (item is null || !TrySource(item, out var uri, out var range))
        {
            status.Text = Tr("所选节点没有源码位置", "The selected node has no source location");
            return;
        }
        var path = ResolveSource(uri);
        if (path is null)
        {
            status.Text = Tr("无法唯一定位源码文件", "Could not uniquely locate the source file") + $" · {uri}";
            return;
        }
        long line = (long)range["line"]!, column = (long)range["column"]!;
        if (line <= 0 || column <= 0 || line > int.MaxValue || column > int.MaxValue)
        {
            return;
        }
        if (!IsCurrentDocument(path))
        {
            NavigateToOtherDocument(path, (int)line, (int)column);
            return;
        }
        var snapshot = editor.TextSnapshot;
        if (line <= 0 || line > snapshot.LineCount || column <= 0)
        {
            return;
        }
        var row = snapshot.GetLineFromLineNumber((int)line - 1);
        var at = row.Start + (int)Math.Min(column - 1, row.Length);
        editor.Caret.MoveTo(at);
        editor.ViewScroller.EnsureSpanVisible(new SnapshotSpan(at, 0));
        editor.VisualElement.Focus();
    }

    private bool IsCurrentDocument(string path) =>
        documents.TryGetTextDocument(editor.TextBuffer, out var document) &&
        string.Equals(path, document.FilePath, StringComparison.OrdinalIgnoreCase);

    private static bool TrySource(TreeViewItem item, out string uri, out JObject range)
    {
        uri = "";
        range = null!;
        if (item.Tag is not JObject node || (node["propertySource"] ?? node["source"]) is not JObject source ||
            source["range"] is not JObject candidate || source["uri"]?.Type != JTokenType.String ||
            candidate["line"]?.Type != JTokenType.Integer || candidate["column"]?.Type != JTokenType.Integer)
        {
            return false;
        }
        uri = (string)source["uri"]!;
        range = candidate;
        return uri.Length != 0 && (long)candidate["line"]! is > 0 and <= int.MaxValue &&
            (long)candidate["column"]! is > 0 and <= int.MaxValue;
    }

    private string? ResolveSource(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return parsed.IsFile && !parsed.IsUnc && Path.IsPathRooted(parsed.LocalPath) ? parsed.LocalPath : null;
        }
        return browser?.ResolveRelativeSource(uri);
    }

    private void NavigateToOtherDocument(string path, int line, int column)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (!File.Exists(path))
        {
            status.Text = Tr("源码文件不可用", "Source file unavailable");
            return;
        }
        try
        {
            var automation = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (automation is null)
            {
                return;
            }
            var window = automation.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            window.Activate();
            if (window.Document?.Selection is EnvDTE.TextSelection selection)
            {
                selection.MoveToLineAndOffset(line, column);
            }
        }
        catch (COMException)
        {
            status.Text = Tr("无法打开源码位置", "Could not open the source location");
        }
    }

    private async Task RequestPickAsync()
    {
        try
        {
            if (browser is not null)
            {
                var endpoint = browserEndpoint;
                var page = browserPage;
                if (endpoint is null || page is null)
                {
                    status.Text = Tr("等待 Web 页面连接", "Waiting for the Web page");
                    return;
                }
                string request = Guid.NewGuid().ToString("D");
                browserPick = request;
                pickDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
                pick.IsEnabled = false;
                if (timestamp is long captured)
                {
                    ShowCaptureTime(captured);
                }
                try
                {
                    await BrowserInspector.SendAsync(endpoint, page, "pick", request);
                }
                catch
                {
                    if (browserPick == request)
                    {
                        browserPick = null;
                        pick.IsEnabled = !disposed && panel.IsVisible;
                    }
                    throw;
                }
                return;
            }
            if ((int?)currentSnapshot?["debugProtocol"]?["version"] == 1)
            {
                var ticket = await FleckLanguageClient.DebugAsync("sunfleck/debugRequest", new { pipe, method = "pick" });
                if (disposed)
                {
                    return;
                }
                status.Text = ticket is null ? Tr("语言服务未连接", "Language server is not connected") :
                    ((int?)ticket["result"] == 0 ? Tr("调试请求成功", "Debug request succeeded") : Tr("调试请求失败", "Debug request failed"));
                return;
            }
            status.Text = Tr("等待应用连接", "Waiting for application");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or OperationCanceledException)
        {
            status.Text = Tr("无法请求 Sunfleck 拾取", "Sunfleck pick request unavailable");
        }
    }

    private async Task RefreshAsync()
    {
        if (reading || disposed || (browser is not null && !panel.IsVisible))
        {
            return;
        }
        reading = true;
        try
        {
            JObject? snapshot;
            if (browser is not null)
            {
                var endpoint = browser.Endpoint;
                if (browserEndpoint != endpoint)
                {
                    ResetBrowserPage(endpoint, null);
                }
                if (endpoint is null)
                {
                    status.Text = Tr("启动预览服务后，点击「打开 Web 调试页面」", "Start the preview server, then open the Web debug page");
                    return;
                }
                var response = await BrowserInspector.ReadAsync(endpoint);
                if (disposed || endpoint != browser.Endpoint)
                {
                    return;
                }
                if (browserPage != response.Page)
                {
                    ResetBrowserPage(endpoint, response.Page);
                }
                snapshot = response.Snapshot;
                if (snapshot is null)
                {
                    status.Text = browser.IsProjectPreview
                        ? Tr("等待 Web 调试页面。启动工程后自动连接", "Waiting for the project’s Web debug page")
                        : Tr("等待 Web 页面发送调试快照", "Waiting for the Web page to send a debug snapshot");
                    return;
                }
            }
            else
            {
                snapshot = await FleckLanguageClient.DebugAsync("sunfleck/debugSnapshot", new { pipe, owner = inspectorOwner });
                if (disposed)
                {
                    return;
                }
                if (snapshot is null)
                {
                    throw new InvalidOperationException("Language service is not connected.");
                }
            }
            if ((int?)snapshot["version"] != 1 || snapshot["timestamp"]?.Type != JTokenType.Integer ||
                snapshot["nodes"] is not JArray nodes || snapshot["windows"] is not JArray windows)
            {
                status.Text = Tr("不支持的 Sunfleck 快照", "Unsupported Sunfleck snapshot");
                return;
            }
            var pickResult = snapshot["browserPick"] as JObject;
            bool completedPick = browserPick is not null && (string?)pickResult?["request"] == browserPick;
            if (completedPick || (browserPick is not null && DateTimeOffset.UtcNow >= pickDeadline))
            {
                browserPick = null;
            }
            long captured = (long)snapshot["timestamp"]!;
            var diagnostics = snapshot["bindingDiagnostics"]?["diagnostics"] as JArray;
            bool unchanged = browser is not null && timestamp is not null &&
                JToken.DeepEquals(currentSnapshot?["windows"], windows) &&
                JToken.DeepEquals(currentSnapshot?["nodes"], nodes) &&
                JToken.DeepEquals(currentSnapshot?["bindingDiagnostics"]?["diagnostics"], diagnostics);
            if (browser is not null)
            {
                pick.IsEnabled = browserPage is not null && browserPick is null &&
                    DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(captured) <= WebSnapshotLifetime;
            }
            currentSnapshot = snapshot;
            ShowCaptureTime(captured);
            refreshStatus.Text = snapshot["fleckRefresh"] is JObject refresh &&
                refresh["elapsedMicroseconds"]?.Type == JTokenType.Integer && refresh["result"]?.Type == JTokenType.Integer
                ? (refresh["pending"]?.Value<bool>() == true ? Tr("Fleck 刷新等待中", "Fleck refresh pending") :
                    (int)refresh["result"]! == 0 ? Tr("Fleck 刷新成功", "Fleck refresh succeeded") :
                    Tr("Fleck 刷新失败", "Fleck refresh failed")) + $" · {(long)refresh["elapsedMicroseconds"]! / 1000.0:F2} ms"
                : "";
            if (!completedPick && (timestamp == captured || unchanged))
            {
                timestamp = captured;
                return;
            }
            string? picked = browser is null ? (string?)snapshot["selection"] : completedPick ? (string?)pickResult?["selection"] : null;
            bool reveal = picked is not null && picked != "0" && (completedPick || picked != previousRuntimeSelection);
            if (reveal)
            {
                selected = $"node:{picked}";
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Render(windows, nodes, diagnostics, reveal);
            previousRuntimeSelection = picked;
            timestamp = captured;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or OperationCanceledException)
        {
            if (browser is not null)
            {
                pick.IsEnabled = false;
            }
            if (timestamp is long captured)
            {
                ShowCaptureTime(captured);
                status.Text += Tr(" · 应用连接中断或暂停，等待恢复", " · Application disconnected or paused; waiting to reconnect");
            }
            else
            {
                status.Text = browser?.IsProjectPreview == true
                    ? Tr("启动工程的 Web 调试后自动连接", "Start the Web debug project to connect")
                    : Tr("应用连接中断或暂停，等待恢复", "Application disconnected or paused; waiting to reconnect");
            }
        }
        finally
        {
            reading = false;
            if (disposed)
            {
                await DisconnectAsync();
            }
        }
    }

    private void ResetBrowserPage(Uri? endpoint, string? page)
    {
        browserEndpoint = endpoint;
        browserPage = page;
        browserPick = null;
        lastHighlight = null;
        selected = null;
        timestamp = null;
        currentSnapshot = null;
        pick.IsEnabled = false;
        tree.Items.Clear();
        sourceItems.Clear();
        treeParents.Clear();
        details.Show(null, english);
    }

    private async Task DisconnectAsync()
    {
        try
        {
            if (browser is not null)
            {
                await UpdateHighlightAsync();
            }
            else
            {
                await FleckLanguageClient.DebugAsync("sunfleck/debugDisconnect", new { pipe, owner = inspectorOwner });
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
            StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or OperationCanceledException)
        {
            // A stopped language server has already discarded its runtime metadata.
        }
    }

    private void ShowCaptureTime(long captured)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(captured);
        status.Text = $"Sunfleck · {time.ToLocalTime():T}" +
            (DateTimeOffset.UtcNow - time > (browser is null ? TimeSpan.FromSeconds(1.5) : WebSnapshotLifetime) ? Tr(" · 快照未更新", " · Snapshot idle") : "");
        if (browserPick is not null)
        {
            status.Text += Tr(" · 在浏览器点击元素，按 Esc 取消", " · Click an element in the browser or press Esc to cancel");
        }
    }

    private void Render(JArray windows, JArray nodes, JArray? diagnostics, bool reveal)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var expanded = new HashSet<string>();
        var previous = new Stack<TreeViewItem>();
        foreach (TreeViewItem item in tree.Items)
        {
            previous.Push(item);
        }
        while (previous.Count != 0)
        {
            var item = previous.Pop();
            if (item.IsExpanded && item.Uid.Length != 0)
            {
                expanded.Add(item.Uid);
            }
            foreach (TreeViewItem child in item.Items)
            {
                previous.Push(child);
            }
        }
        tree.Items.Clear();
        sourceItems.Clear();
        treeParents.Clear();
        var roots = new Dictionary<int, TreeViewItem>();
        var items = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        TreeViewItem? selection = null;
        foreach (var window in windows)
        {
            if ((int?)window["id"] is int id && !roots.ContainsKey(id))
            {
                var item = new TreeViewItem { Header = (string?)window["title"] ?? Tr("窗口", "Window"), Uid = $"window:{id}", Tag = window, Style = treeRow };
                item.IsExpanded = timestamp is null || expanded.Contains(item.Uid);
                roots.Add(id, item);
                tree.Items.Add(item);
            }
        }
        foreach (var node in nodes)
        {
            string? id = (string?)node["id"];
            if (id is null || id.Length == 0 || items.ContainsKey(id))
            {
                continue;
            }
            string? name = (string?)node["fleckId"];
            string? label = string.IsNullOrEmpty(name) ? (string?)node["type"] : name;
            if ((string?)node["page"] == "0")
            {
                label += $" ({Tr("代码", "code")})";
            }
            var item = CreateObjectItem(label ?? "", $"node:{id}", node);
            item.IsExpanded = expanded.Contains(item.Uid);
            items.Add(id, item);
            sourceItems.Add(item);
            if (item.Uid == selected)
            {
                selection = item;
            }
        }
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.Values)
        {
            var node = (JToken)item.Tag;
            string id = (string)node["id"]!;
            string? parent = (string?)currentSnapshot?["inspector"]?["parents"]?[id] ?? (string?)node["parent"];
            int window = (int?)node["window"] ?? 0;
            if (parent is not null && items.TryGetValue(parent, out var owner) && owner != item &&
                (int?)((JToken)owner.Tag)["window"] == window && !CreatesCycle(id, parent, parents))
            {
                owner.Items.Add(item);
                treeParents[item] = owner;
                parents[id] = parent;
            }
            else if (roots.TryGetValue(window, out var root))
            {
                root.Items.Add(item);
                treeParents[item] = root;
            }
        }
        var errors = new TreeViewItem { Header = Tr("绑定诊断", "Binding diagnostics"), Uid = "diagnostics", IsExpanded = expanded.Contains("diagnostics"), Style = treeRow };
        if (diagnostics is not null)
        {
            foreach (var error in diagnostics)
            {
                string label = $"{((bool?)error["active"] == true ? Tr("失败", "Active") : Tr("已恢复", "Recovered"))} · {error["path"]} · ×{error["occurrences"]}";
                var item = CreateObjectItem(label, $"diagnostic:{error["object"]}:{error["property"]}:{error["path"]}", error);
                item.ToolTip = (string?)error["message"];
                errors.Items.Add(item);
                treeParents[item] = errors;
                if (item.Uid == selected)
                {
                    selection = item;
                }
            }
        }
        tree.Items.Add(errors);
        if (selection is not null)
        {
            Reveal(selection, reveal);
        }
        else
        {
            details.Show(null, english);
        }
    }

    private static string AutomationName(TreeViewItem item)
        => System.Windows.Automation.AutomationProperties.GetName(item);

    private TreeViewItem CreateObjectItem(string label, string id, JToken node)
    {
        var item = new TreeViewItem { Header = label, Uid = id, Tag = node, Style = treeRow };
        System.Windows.Automation.AutomationProperties.SetName(item, label);
        if (TrySource(item, out _, out _))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            string caption = Tr("定位源码", "Go to source");
            var navigate = new Button { ToolTip = caption, Style = (Style)tree.Resources["InspectorSourceButton"] };
            System.Windows.Automation.AutomationProperties.SetName(navigate, caption);
            navigate.Click += (_, _) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                GoToSource(item);
            };
            header.Children.Add(navigate);
            item.Header = header;
        }
        return item;
    }

    private void Reveal(TreeViewItem item, bool scroll)
    {
        var ancestors = new Stack<TreeViewItem>();
        for (var child = item; treeParents.TryGetValue(child, out var ancestor); child = ancestor)
        {
            ancestors.Push(ancestor);
        }
        while (ancestors.Count != 0)
        {
            ancestors.Pop().IsExpanded = true;
        }
        item.IsSelected = true;
        if (scroll)
        {
            tree.UpdateLayout();
            item.BringIntoView();
        }
    }

    private static bool CreatesCycle(string id, string parent, Dictionary<string, string> parents)
    {
        string? ancestor = parent;
        while (ancestor is not null)
        {
            if (ancestor == id)
            {
                return true;
            }
            parents.TryGetValue(ancestor, out ancestor);
        }
        return false;
    }

    private void SelectItem(object sender, RoutedPropertyChangedEventArgs<object> args)
    {
        if (args.NewValue is TreeViewItem item && item.Tag is JToken value)
        {
            selected = item.Uid;
            details.Show(value, english, currentSnapshot?["inspector"]?["models"] as JObject);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        timer.Stop();
        hoverTimer.Stop();
        hoverTimer.Tick -= HoverTick;
        if (!reading)
        {
            DisconnectAsync().Forget();
        }
        timer.Tick -= Tick;
        tree.SelectedItemChanged -= SelectItem;
        language.SelectionChanged -= LanguageSelected;
        LanguageChanged -= ApplyLanguage;
        editor.VisualElement.PreviewKeyDown -= EditorKeyDown;
        developmentLauncher?.Dispose();
    }
}
