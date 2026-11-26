using System.Diagnostics;
using System.Runtime.InteropServices;
using Sunfleck;

internal static class Program
{
    private const string Page = """
        <Window xmlns="urn:sunfleck:v1" Title="Binding test" Width="400" Height="240">
          <StackPanel>
            <TextBlock Id="count" Text="{Binding CountText}"/>
            <TextBox Id="editor" Text="{Binding CountText, Mode=TwoWay}"/>
            <TextBlock Id="snapshot" Text="{Binding CountText, Mode=OneTime}"/>
            <Button Id="button" Command="{Binding IncrementCount}" Text="Increment"/>
          </StackPanel>
        </Window>
        """;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static T Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void VerifyLifetime()
    {
        Node label;
        using (var app = new App(Renderer.Software, headless: true))
        {
            using (var other = new App(Renderer.Software, headless: true))
            {
                label = app.CreateNode(NodeKind.TextBlock);
                label.Text = "中文 / café / 🌱";
                Assert(label.Text == "中文 / café / 🌱", "UTF-8 round trip failed.");
                Assert(Expect<SunfleckException>(() => app.SetWindowOptions("Invalid", 0, 100)).Result ==
                    Result.InvalidArgument, "Native status was lost.");
                Assert(Expect<SunfleckException>(() => other.SetRoot(label)).Result ==
                    Result.InvalidArgument, "Cross-app handle was accepted.");
                var error = Task.Run(() => Expect<SunfleckException>(() => _ = label.Text)).GetAwaiter().GetResult();
                Assert(error.Result == Result.WrongThread, "Thread affinity was not enforced.");
                var image = new Sunfleck.Elements.Image(app);
                app.RegisterImageRgba("sample", [12, 34, 56, 255], 1, 1);
                image.Source = "sample";
                Assert(image.Source == "sample", "Typed Image.Source did not round-trip.");
                var imageKey = "sample";
                using (var model = app.CreatePropertyModel().Add("ImageKey", () => imageKey))
                {
                    image.Bind(Sunfleck.Elements.Image.SourceProperty, model, "ImageKey");
                    imageKey = "";
                    model.NotifyPropertyChanged("ImageKey");
                    Assert(image.Source == "", "Typed Image.Source binding did not update.");
                }
                app.SetRoot(label);
                app.OpenWindow();
                Assert(app.Poll() == Result.Ok, "Initial frame missing.");
                var frame = app.ReadPixels();
                Assert(frame.Width > 0 && frame.Height > 0 &&
                    frame.Rgba.Length == (int)(frame.Width * frame.Height * 4), "Invalid pixel frame.");
                Assert(app.Poll() == Result.Timeout, "Static view kept producing frames.");
                app.RequestClose();
                Assert(app.Poll() == Result.Exited, "Close did not exit the event loop.");
            }
        }
        Expect<ObjectDisposedException>(() => _ = label.Text);
    }

    private static void VerifyBindings()
    {
        using (var app = new App(Renderer.Software, headless: true))
        {
            using (var first = app.CreateTextSource("First"))
            {
                using (var second = app.CreateTextSource("Second"))
                {
                    int firstCalls = 0, secondCalls = 0;
                    using (var firstCommand = app.CreateCommand(_ => ++firstCalls))
                    {
                        using (var secondCommand = app.CreateCommand(_ => second.Set($"Count: {++secondCalls}")))
                        {
                            app.LoadFleck(Page, new FleckDataContext().AddText("CountText", first)
                                .AddCommand("IncrementCount", firstCommand));
                            var original = app.FindFleckNode("count");
                            app.LoadFleck(Page, new FleckDataContext().AddText("CountText", second)
                                .AddCommand("IncrementCount", secondCommand));

                            var diagnostic = Expect<FleckException>(() => app.LoadFleck(Page, new FleckDataContext()));
                            Assert(diagnostic.Error == FleckError.Value && diagnostic.Line > 0 && diagnostic.Column > 0,
                                "Missing context export was not diagnosed.");
                            var label = app.FindFleckNode("count");
                            var editor = app.FindFleckNode("editor");
                            var snapshot = app.FindFleckNode("snapshot");
                            var button = app.FindFleckNode("button");
                            first.Set("Independent");
                            Assert(original.Text == "Independent" && label.Text == "Second", "Contexts are not isolated.");

                            app.OpenWindow();
                            app.Poll();
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            app.ActivateButton();
                            Assert(firstCalls == 0 && secondCalls == 1, "Wrong command or collected callback.");
                            Assert(label.Text == "Count: 1" && editor.Text == "Count: 1" && snapshot.Text == "Second",
                                "Text binding modes did not behave as expected.");
                            Assert(Expect<SunfleckException>(() => second.Set("A\nB")).Result == Result.InvalidArgument,
                                "Single-line TwoWay source accepted line breaks.");

                            int calls = 0;
                            button.SetAction(node =>
                            {
                                ++calls;
                                node.DeferAction();
                            });
                            app.ActivateButton();
                            app.ActivateButton();
                            Assert(calls == 1, "SingleFlight accepted a second activation.");
                            button.CompleteAction();
                            app.ActivateButton();
                            Assert(calls == 2 && secondCalls == 1, "Activation did not resume after completion.");
                            button.CompleteAction();
                            button.SetAction(_ => throw new ApplicationException("Callback failure"));
                            Assert(Expect<ApplicationException>(app.ActivateButton).Message == "Callback failure",
                                "Callback exception was not propagated.");
                            label.Text = "Literal";
                            second.Set("Replacement");
                            Assert(label.Text == "Literal" && editor.Text == "Replacement", "Literal override failed.");
                            second.Dispose();
                            Assert(editor.Text == "Replacement", "Detached TwoWay editor did not retain text.");
                        }
                    }
                }
            }
        }
    }

    private static void VerifyEditor()
    {
        using (var app = new App(Renderer.Software))
        {
            using (var source = app.CreateTextSource("Initial"))
            {
                using (var command = app.CreateCommand(_ => { }))
                {
                    app.LoadFleck(Page, new FleckDataContext().AddText("CountText", source)
                        .AddCommand("IncrementCount", command));
                    app.OpenWindow();
                    app.Poll();
                    var window = FindWindow(null, "Binding test");
                    Assert(window != 0, "Managed editor window did not open.");
                    GetWindowThreadProcessId(window, out var processId);
                    Assert(processId == (uint)Environment.ProcessId, "Found an unrelated editor window.");
                    var point = (nint)((Scale(window, 90) << 16) | Scale(window, 35));
                    Assert(PostMessage(window, 0x0202, 0, point) &&
                        PostMessage(window, 0x0102, 1, 0) && PostMessage(window, 0x0102, 'X', 0),
                        "Could not send input to the editor.");
                    app.Poll();
                    Assert(app.FindFleckNode("editor").Text == "X" &&
                        app.FindFleckNode("count").Text == "X" &&
                        app.FindFleckNode("snapshot").Text == "Initial", "User input did not write back.");
                    app.RequestClose();
                    Assert(app.Poll() == Result.Exited, "Editor window did not close.");
                }
            }
        }
    }

    private static void VerifyWindow(string executable, string title, bool waitForAsyncClick)
    {
        var readyFile = Path.Combine(Path.GetTempPath(), $"sunfleck-{Guid.NewGuid():N}.ready");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--smoke");
        start.ArgumentList.Add(readyFile);
        using (var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start counter window."))
        {
            try
            {
                nint window = 0;
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(10) && !process.HasExited)
                {
                    var candidate = FindWindow(null, title + " [GPU]");
                    if (candidate == 0)
                    {
                        candidate = FindWindow(null, title + " [CPU]");
                    }
                    if (candidate != 0)
                    {
                        GetWindowThreadProcessId(candidate, out var processId);
                        if (processId == (uint)process.Id && File.Exists(readyFile))
                        {
                            window = candidate;
                            break;
                        }
                    }
                    Thread.Sleep(20);
                }
                Assert(window != 0, $"Counter window did not open: {title}.");
                var point = (nint)((Scale(window, 106) << 16) | Scale(window, 35));
                Assert(PostMessage(window, 0x0201, 1, point) &&
                    PostMessage(window, 0x0202, 0, point), "Could not dispatch pointer events.");
                if (waitForAsyncClick)
                {
                    timer.Restart();
                    while (timer.Elapsed < TimeSpan.FromSeconds(5) && !File.Exists(readyFile + ".clicked") && !process.HasExited)
                    {
                        Thread.Sleep(20);
                    }
                    Assert(File.Exists(readyFile + ".clicked"), "Fleck async click did not complete.");
                }
                Assert(PostMessage(window, 0x0010, 0, 0), "Could not dispatch close event.");
                Assert(process.WaitForExit(10000), "Counter window did not close.");
                Assert(process.ExitCode == 0, $"Counter click failed (exit {process.ExitCode}).");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
                File.Delete(readyFile);
                File.Delete(readyFile + ".clicked");
            }
        }
    }

    private static int Scale(nint window, int coordinate)
        => (int)((coordinate * GetDpiForWindow(window) + 48) / 96);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindow(string? className, string title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--components") { ComponentTests.Run(); ElementTypeTests.Run(); }
            else if (args.Length == 1 && args[0] == "--async-commands")
            {
                AsyncCommandTests.Run();
            }
            else if (args.Length == 1 && args[0] == "--fleck-file")
            {
                FleckFileTests.Run();
            }
            else if (args.Length == 2 && args[0] == "--inspector-desktop")
            {
                InspectorDesktopTests.Run(args[1]);
            }
            else if (args.Length == 4 && args[0] == "--language-metadata")
            {
                DevelopmentLanguageTests.NativeModels(args[1..]);
            }
            else if (args.Length == 3 && args[0] == "--node-scale")
            {
                NodeScaleTests.Run(args[1], args[2]);
            }
            else if (args.Length == 3 && args[0] == "--window")
            {
                VerifyWindow(args[1], args[2], waitForAsyncClick: args[2].Contains("Fleck", StringComparison.Ordinal));
            }
            else if (args.Length == 1 && args[0] == "--editor")
            {
                VerifyEditor();
            }
            else
            {
                ComponentTests.Run();
                using (var host = new App(Renderer.Software, headless: true)) {
                    using var rows = host.CreatePropertyModel();
                    var source = new System.Collections.ObjectModel.ObservableCollection<string> { "a", "a" };
                    rows.ObserveTextCollection(source);
                    host.LoadFleck("<Window xmlns='urn:sunfleck:v1'><TextBlock Id='count' Text='{Binding Count}'/></Window>", rows);
                    var list = host.CreateNode(NodeKind.ListView);
                    list.SetItemsSource(rows);
                    using var pageModel = host.CreatePropertyModel().Add("Rows", () => rows);
                    host.LoadFleck("<Window xmlns='urn:sunfleck:v1'><ListView Id='view' ItemsSource='{Binding Rows}'/></Window>", pageModel);
                    host.OpenWindow();
                    host.Poll(0);
                    var oldRow = list.GetChild(TreeKind.Visual,0);
                    var boundList = host.FindFleckNode("view");
                    if (boundList.GetChildCount(TreeKind.Visual) != 2 ||
                        boundList.GetChild(TreeKind.Visual,0).ItemIdentity != oldRow.ItemIdentity) {
                        throw new Exception("Fleck ItemsSource did not present the model collection");
                    }
                    var identity = rows.GetItem(0).Identity;
                    rows.ObserveTextCollection(source);
                    if (rows.GetItem(0).Identity != identity) {
                        throw new Exception("Observing the same collection regenerated stable identities");
                    }
                    var invalidSource = new System.Collections.ObjectModel.ObservableCollection<string> { "invalid\0text" };
                    try {
                        rows.ObserveTextCollection(invalidSource);
                        throw new Exception("Invalid source accepted");
                    } catch (ArgumentException) { }
                    source.Move(0,1);
                    source.Insert(0,"新");
                    host.Poll(0);
                    if (rows.GetItem(2).Identity != identity || boundList.GetChildCount(TreeKind.Visual) != 3) {
                        throw new Exception("Collection move lost identity or count binding");
                    }
                    source[2] = "changed";
                    if (rows.GetItem(2).Identity == identity || rows.GetItem(2).Text != "changed") {
                        throw new Exception("Replacing a collection item did not assign its new identity");
                    }
                    source.RemoveAt(0);
                    source.Clear();
                    host.Poll(0);
                    try {
                        _ = oldRow.Text;
                        throw new Exception("Removed row was still accessible");
                    } catch (SunfleckException) { }
                    host.Poll(0);
                    if (rows.ItemCount != 0 || boundList.GetChildCount(TreeKind.Visual) != 0) {
                        throw new Exception("Collection reset did not reach Fleck");
                    }
                    rows.Dispose();
                    source.Add("detached");
                }
                using (var host = new App(Renderer.Software, headless: true)) {
                    string caption = "Alice";
                    using var record = host.CreatePropertyModel().Add("Caption", () => caption, value => caption=value);
                    using var rows = host.CreatePropertyModel();
                    rows.EnableTextCollection();
                    rows.SpliceModels(0,0,("A",record));
                    using var model = host.CreatePropertyModel().Add("Rows", () => rows);
                    host.LoadFleck("""
                        <Window xmlns="urn:sunfleck:v1"><ListView Id="list" ItemsSource="{Binding Rows}">
                          <ListView.ItemTemplate><DataTemplate>
                            <Border><TextBlock Text="{Binding Caption}"/></Border>
                          </DataTemplate></ListView.ItemTemplate>
                        </ListView></Window>
                        """,model);
                    host.OpenWindow();
                    host.Poll(0);
                    var row = host.FindFleckNode("list").GetChild(TreeKind.Visual,0);
                    var label = row.GetChild(TreeKind.Visual,0).GetChild(TreeKind.Visual,0);
                    if (label.Text != "Alice") { throw new Exception("DataTemplate lost the item context"); }
                    caption = "Bob";
                    record.NotifyPropertyChanged("Caption");
                    if (label.Text != "Bob") { throw new Exception("Template item notification was lost"); }
                    try {
                        host.RefreshFleck("""
                            <Window xmlns="urn:sunfleck:v1"><ListView ItemsSource="{Binding Rows}">
                              <ListView.ItemTemplate><DataTemplate><TextBlock Text="{Binding Missing}"/></DataTemplate></ListView.ItemTemplate>
                            </ListView></Window>
                            """,model);
                        throw new Exception("Refresh committed a broken row template");
                    } catch (FleckException) { }
                    if (label.Text != "Bob") { throw new Exception("Failed template refresh retired the active row"); }
                    host.RefreshFleck("""
                        <Window xmlns="urn:sunfleck:v1"><ListView Id="keyed" ItemsSource="{Binding Rows}">
                          <ListView.ItemTemplateSelector>
                            <DataTemplate Key="A"><TextBlock Text="Matched"/></DataTemplate>
                          </ListView.ItemTemplateSelector>
                        </ListView></Window>
                        """,model);
                    host.Poll(0);
                    var keyed = host.FindFleckNode("keyed");
                    if (keyed.GetChild(TreeKind.Visual,0).GetChild(TreeKind.Visual,0).Text != "Matched") {
                        throw new Exception("Expected the template matching the project's item label");
                    }
                    keyed.SetItemTemplate("A",null);
                    if (keyed.GetChild(TreeKind.Visual,0).Text != "A") {
                        throw new Exception("Clearing a template selector lost the item label");
                    }
                    using var children = host.CreatePropertyModel();
                    children.EnableTextCollection();
                    children.SpliceItems(0,0,"child");
                    record.Add("Children", () => children);
                    host.RefreshFleck("""
                        <Window xmlns="urn:sunfleck:v1"><TreeView Id="tree" ItemsSource="{Binding Rows}">
                          <TreeView.ItemTemplate><HierarchicalDataTemplate ItemsSource="{Binding Children}">
                            <TextBlock Text="{Binding Caption}"/>
                          </HierarchicalDataTemplate></TreeView.ItemTemplate>
                        </TreeView></Window>
                        """,model);
                    host.Poll(0);
                    var branch = host.FindFleckNode("tree").GetChild(TreeKind.Visual,0);
                    if (branch.Text != "Bob" || branch.GetChildCount(TreeKind.Visual) != 0) {
                        throw new Exception("Collapsed tree row lost its header or generated children");
                    }
                    branch.Value = 1;
                    host.Poll(0);
                    if (branch.GetChild(TreeKind.Visual,0).Text != "child") {
                        throw new Exception("Expanded child collection was not presented");
                    }
                    using var other = host.CreatePropertyModel().Add("Caption", () => "Ada");
                    rows.SpliceModels(1,0,("B",other));
                    host.RefreshFleck("""
                        <Window xmlns="urn:sunfleck:v1"><DataGrid Id="grid" ItemsSource="{Binding Rows}">
                          <DataGrid.Columns><DataGridTextColumn Header="Name" Width="150" Binding="{Binding Caption}"/></DataGrid.Columns>
                        </DataGrid></Window>
                        """,model);
                    host.Poll(0);
                    var grid = host.FindFleckNode("grid");
                    Node Cell(uint i) => grid.GetChild(TreeKind.Visual,i).GetChild(TreeKind.Visual,0).GetChild(TreeKind.Visual,0);
                    if (Cell(0).Text != "Bob") { throw new Exception("DataGrid column did not present the object property"); }
                    grid.SortColumn(0);
                    host.Poll(0);
                    if (Cell(0).Text != "Ada") { throw new Exception("DataGrid column sort failed"); }
                    grid.SetColumnWidth(0,180);
                    host.Poll(0);
                    var editor = Cell(1);
                    editor.FinishCellEdit(false);
                    if (editor.Text != "Bob") { throw new Exception("Cancelling a grid cell lost its value"); }
                    caption = "Aaron";
                    record.NotifyPropertyChanged("Caption");
                    host.Poll(0);
                    if (Cell(0).Text != "Aaron" || Cell(1).Text != "Ada") {
                        throw new Exception("Changing a sort key did not reorder the grid");
                    }
                    PropertyModel selected = record;
                    model.Add("Selected", () => selected);
                    host.RefreshFleck("<Window xmlns='urn:sunfleck:v1'><TextBlock Id='nested' Text='{Binding Selected.Caption}'/></Window>",model);
                    var nested=host.FindFleckNode("nested");
                    caption="New";
                    record.NotifyPropertyChanged("Caption");
                    if (nested.Text!="New") { throw new Exception("Nested model did not notify its leaf"); }
                    selected=other;
                    model.NotifyPropertyChanged("Selected");
                    record.NotifyPropertyChanged("Caption");
                    if (nested.Text!="Ada") { throw new Exception("Nested model failed to switch subscriptions"); }
                    model.Add("Prefix", () => "Person")
                        .AddConverter("Label", values => $"{values[0]}: {values[1]}")
                        .AddMultiBinding("Display", "Label", "Prefix", "Selected.Caption");
                    model.Bind(nested, PropertyTarget.Text, "Display");
                    if (nested.Text != "Person: Ada") { throw new Exception("MultiBinding input order changed"); }
                    selected = record;
                    model.NotifyPropertyChanged("Selected");
                    if (nested.Text != "Person: New") { throw new Exception("MultiBinding missed nested source replacement"); }
                    host.RefreshFleck("""
                        <Window xmlns="urn:sunfleck:v1"><TextBlock Id="combined">
                          <TextBlock.Text><MultiBinding Converter="Label">
                            <Binding Path="Prefix"/><Binding Path="Selected.Caption"/>
                          </MultiBinding></TextBlock.Text>
                        </TextBlock></Window>
                        """,model);
                    var combined = host.FindFleckNode("combined");
                    if (combined.Text != "Person: New") { throw new Exception("Fleck MultiBinding lost its converter inputs"); }
                    selected = other;
                    model.NotifyPropertyChanged("Selected");
                    if (combined.Text != "Person: Ada") { throw new Exception("Fleck MultiBinding lost nested updates"); }
                    host.RefreshFleck("""
                        <Window xmlns="urn:sunfleck:v1"><StackPanel FontSize="23">
                          <TextBlock Id="copy" Text="{Binding Text, ElementName=input}"/>
                          <TextBox Id="input" Text="initial"/>
                          <TextBlock Id="ancestor" Text="{Binding FontSize, RelativeSource=FindAncestor, AncestorType=StackPanel}"/>
                        </StackPanel></Window>
                        """,model);
                    var input = host.FindFleckNode("input");
                    var copy = host.FindFleckNode("copy");
                    input.Text = "updated";
                    if (copy.Text != "updated" || !host.FindFleckNode("ancestor").Text.StartsWith("23",StringComparison.Ordinal)) {
                        throw new Exception("ElementName or FindAncestor did not resolve a visual property");
                    }
                }
                ElementTypeTests.Run();
                DocumentTests.Run();
                VerifyLifetime();
                VerifyBindings();
                DevelopmentTests.Run();
                FleckFileTests.Run();
                MultiWindowTests.Run();
                DevelopmentLanguageTests.Run();
                PropertyBindingTests.Run();
                PropertySystemTests.Run();
                AsyncCommandTests.Run();
                DockTests.Run();
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
