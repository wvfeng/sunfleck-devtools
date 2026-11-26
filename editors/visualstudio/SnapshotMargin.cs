using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
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

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container)
    {
        string? pipe = Environment.GetEnvironmentVariable("SUNFLECK_DEBUG_PIPE");
        return string.IsNullOrWhiteSpace(pipe) ? null : new SnapshotMargin(pipe, host.TextView, Documents);
    }
}

internal sealed class SnapshotMargin : IWpfTextViewMargin
{
    private const string Name = "SunfleckSnapshot";
    private readonly string pipe;
    private readonly IWpfTextView editor;
    private readonly ITextDocumentFactoryService documents;
    private readonly DockPanel panel = new() { Width = 300 };
    private readonly TextBlock status = new() { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock refreshStatus = new() { Margin = new Thickness(6, 0, 6, 6) };
    private readonly TreeView tree = new();
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
    private readonly TextBox details = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 160,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
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

    internal SnapshotMargin(string pipe, IWpfTextView editor, ITextDocumentFactoryService documents)
    {
        this.pipe = pipe;
        this.editor = editor;
        this.documents = documents;
        language.Items.Add("简体中文");
        language.Items.Add("English");
        language.SelectionChanged += LanguageSelected;
        DockPanel.SetDock(language, Dock.Top);
        panel.Children.Add(language);
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
        DockPanel.SetDock(details, Dock.Bottom);
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
        panel.Children.Add(details);
        panel.Children.Add(tree);
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
        pick.Content = Tr("拾取运行时对象", "Pick running object");
        sourceButton.Content = Tr("定位源码", "Go to source");
        locate.Content = Tr("查找光标处的对象", "Find objects at caret");
        launch.Content = Tr("运行已配置的开发启动器", "Run configured development launcher");
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
        if (reading && timestamp is long captured) { ShowCaptureTime(captured); }
        RefreshAsync().Forget();
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
        if (documents.TryGetTextDocument(editor.TextBuffer, out var document))
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
        foreach (var item in sourceItems)
        {
            if (matches is not null && !matches.Contains(item.Uid))
            {
                continue;
            }
            if (!TrySourceRange(item, out var range) ||
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
            var option = new MenuItem { Header = $"{item.Header} ({item.Uid})" };
            singleMatch = item;
            string uid = item.Uid;
            option.Click += (_, _) =>
            {
                var current = sourceItems.Find(candidate => candidate.Uid == uid);
                if (current is not null)
                {
                    Reveal(current);
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
            Reveal(singleMatch);
            return;
        }
        menu.IsOpen = true;
    }

    private void GoToSource()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (tree.SelectedItem is not TreeViewItem item || !TrySource(item, out var path, out var range))
        {
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

    private bool TrySourceRange(TreeViewItem item, out JObject range)
        => TrySource(item, out var path, out range) && IsCurrentDocument(path);

    private bool IsCurrentDocument(string path) =>
        documents.TryGetTextDocument(editor.TextBuffer, out var document) &&
        string.Equals(path, document.FilePath, StringComparison.OrdinalIgnoreCase);

    private static bool TrySource(TreeViewItem item, out string path, out JObject range)
    {
        path = "";
        range = null!;
        if (item.Tag is not JObject node || (node["propertySource"] ?? node["source"]) is not JObject source ||
            source["range"] is not JObject candidate || source["uri"]?.Type != JTokenType.String ||
            candidate["line"]?.Type != JTokenType.Integer || candidate["column"]?.Type != JTokenType.Integer)
        {
            return false;
        }
        string uri = (string)source["uri"]!;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile || parsed.IsUnc)
        {
            return false;
        }
        path = parsed.LocalPath;
        range = candidate;
        return Path.IsPathRooted(path);
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or OperationCanceledException)
        {
            status.Text = Tr("无法请求 Sunfleck 拾取", "Sunfleck pick request unavailable");
        }
    }

    private async Task RefreshAsync()
    {
        if (reading || disposed)
        {
            return;
        }
        reading = true;
        try
        {
            var snapshot = await FleckLanguageClient.DebugAsync("sunfleck/debugSnapshot", new { pipe, owner = inspectorOwner });
            if (disposed)
            {
                return;
            }
            if (snapshot is null) { throw new InvalidOperationException("Language service is not connected."); }
            currentSnapshot = snapshot;
            if ((int?)snapshot["version"] != 1 || snapshot["timestamp"]?.Type != JTokenType.Integer ||
                snapshot["nodes"] is not JArray nodes || snapshot["windows"] is not JArray windows)
            {
                status.Text = Tr("不支持的 Sunfleck 快照", "Unsupported Sunfleck snapshot");
                return;
            }
            long captured = (long)snapshot["timestamp"]!;
            ShowCaptureTime(captured);
            refreshStatus.Text = snapshot["fleckRefresh"] is JObject refresh &&
                refresh["elapsedMicroseconds"]?.Type == JTokenType.Integer && refresh["result"]?.Type == JTokenType.Integer
                ? (refresh["pending"]?.Value<bool>() == true ? Tr("Fleck 刷新等待中", "Fleck refresh pending") :
                    (int)refresh["result"]! == 0 ? Tr("Fleck 刷新成功", "Fleck refresh succeeded") :
                    Tr("Fleck 刷新失败", "Fleck refresh failed")) + $" · {(long)refresh["elapsedMicroseconds"]! / 1000.0:F2} ms"
                : "";
            if (timestamp == captured)
            {
                return;
            }
            string? picked = (string?)snapshot["selection"];
            if (picked is not null && picked != "0" && picked != previousRuntimeSelection)
            {
                selected = $"node:{picked}";
            }
            Render(windows, nodes, snapshot["bindingDiagnostics"]?["diagnostics"] as JArray);
            previousRuntimeSelection = picked;
            timestamp = captured;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or OperationCanceledException)
        {
            if (timestamp is long captured)
            {
                ShowCaptureTime(captured);
                status.Text += Tr(" · 应用连接中断或暂停，等待恢复", " · Application disconnected or paused; waiting to reconnect");
            }
            else
            {
                status.Text = Tr("应用连接中断或暂停，等待恢复", "Application disconnected or paused; waiting to reconnect");
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

    private async Task DisconnectAsync()
    {
        try
        {
            await FleckLanguageClient.DebugAsync("sunfleck/debugDisconnect", new { pipe, owner = inspectorOwner });
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
            (DateTimeOffset.UtcNow - time > TimeSpan.FromSeconds(1.5) ? Tr(" · 快照未更新", " · Snapshot idle") : "");
    }

    private void Render(JArray windows, JArray nodes, JArray? diagnostics)
    {
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
        details.Clear();
        var roots = new Dictionary<int, TreeViewItem>();
        var items = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        TreeViewItem? selection = null;
        foreach (var window in windows)
        {
            if ((int?)window["id"] is int id && !roots.ContainsKey(id))
            {
                var item = new TreeViewItem { Header = (string?)window["title"] ?? Tr("窗口", "Window"), Uid = $"window:{id}", Tag = window };
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
            var item = new TreeViewItem { Header = label, Uid = $"node:{id}", Tag = node };
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
                parents[id] = parent;
            }
            else if (roots.TryGetValue(window, out var root))
            {
                root.Items.Add(item);
            }
        }
        var errors = new TreeViewItem { Header = Tr("绑定诊断", "Binding diagnostics"), Uid = "diagnostics", IsExpanded = expanded.Contains("diagnostics") };
        if (diagnostics is not null)
        {
            foreach (var error in diagnostics)
            {
                var item = new TreeViewItem
                {
                    Header = $"{((bool?)error["active"] == true ? Tr("失败", "Active") : Tr("已恢复", "Recovered"))} · {error["path"]} · ×{error["occurrences"]}",
                    Uid = $"diagnostic:{error["object"]}:{error["property"]}:{error["path"]}",
                    ToolTip = (string?)error["message"], Tag = error
                };
                errors.Items.Add(item);
                if (item.Uid == selected)
                {
                    selection = item;
                }
            }
        }
        tree.Items.Add(errors);
        if (selection is not null)
        {
            Reveal(selection);
        }
    }

    private static void Reveal(TreeViewItem item)
    {
        var ancestors = new Stack<TreeViewItem>();
        for (var ancestor = ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem;
            ancestor is not null; ancestor = ItemsControl.ItemsControlFromItemContainer(ancestor) as TreeViewItem)
        {
            ancestors.Push(ancestor);
        }
        while (ancestors.Count != 0)
        {
            ancestors.Pop().IsExpanded = true;
        }
        item.IsSelected = true;
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
            details.Text = value.ToString(Formatting.Indented);
            if (value["bindings"] is JArray bindings && currentSnapshot?["inspector"]?["models"] is JObject models)
            {
                var associated = new JObject();
                foreach (var binding in bindings)
                {
                    string? model = (string?)binding["model"];
                    if (model is not null && models[model] is JToken metadata)
                    {
                        associated[model] = metadata.DeepClone();
                    }
                }
                details.Text += Environment.NewLine + associated.ToString(Formatting.Indented);
            }
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
