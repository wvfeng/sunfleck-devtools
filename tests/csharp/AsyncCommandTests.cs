using System.ComponentModel;
using System.Diagnostics;
using Sunfleck;

internal static class AsyncCommandTests
{
    private sealed class Model : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Caption { get; set; } = "Initial";
        public int Count { get; set; }
        [CommandModes(CommandExecutionModes.Async)]
        public Command Run { get; set; } = null!;
        public Command Sync { get; set; } = null!;
        public void Notify(string? path = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(path));
    }
    private const string Page = """
        <Window xmlns="urn:sunfleck:v1"><StackPanel>
          <StackPanel.CommandBindings><CommandBinding Command="Run" Executed="{Binding Run}"/></StackPanel.CommandBindings>
          <Button Id="run" Content="{Binding Caption}" Command="{RoutedCommand Run}" CommandMode="Async"/>
          <Button Id="sync" Content="Sync" Command="{Binding Sync}"/>
          <TextBlock Id="count" Text="{Binding Count}"/>
          <TextBlock Id="once" Text="{Binding Caption, Mode=OneTime}"/>
          <TextBlock Id="queued" Text="Before"/>
        </StackPanel></Window>
        """;
    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
    private static void Pump(App app, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(5)) { app.Poll(); Thread.Yield(); }
        Check(done(), "Async command timed out.");
    }
    internal static void Run()
    {
        OriginalModelAndIndependentCommands();
        foreach (string stage in new[] { "before-await", "after-await", "ui" }) { ErrorsAndRetry(stage); }
        foreach (string target in new[] { "command", "model", "app", "close", "cancel" }) { CancelPendingInvoke(target); }
        DispatcherCompletion();
        CancellationCallbackFailure();
        RejectSyncOverride();
        Console.WriteLine("Async commands: original resources, UI dispatch, independent commands, failure/retry, cancellation and shutdown passed.");
    }
    private static void OriginalModelAndIndependentCommands()
    {
        using var app = new App(Renderer.Software, headless: true);
        using var resource = new MemoryStream(); // The original resource is never cloned.
        var model = new Model();
        using var properties = app.CreatePropertyModel(model);
        int uiThread = Environment.CurrentManagedThreadId, calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = app.CreateCommand(_ => { model.Count++; model.Notify(nameof(Model.Count)); });
        using var command = app.CreateAsyncCommand(async token =>
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "Delegate started on UI.");
            Check(SynchronizationContext.Current is null, "Command captured a UI context.");
            Interlocked.Increment(ref calls);
            await resource.WriteAsync(new byte[] { 42 }, token);
            await app.InvokeAsync(() =>
            {
                Check(Environment.CurrentManagedThreadId == uiThread, "UI callback ran on a worker.");
                model.Caption = "Working"; model.Notify(nameof(Model.Caption));
            });
            await gate.Task.WaitAsync(token);
            Check(Environment.CurrentManagedThreadId != uiThread, "Await resumed on UI.");
            await app.InvokeAsync(() => { model.Count += (int)resource.Length; model.Caption = "Done"; model.Notify(); });
        });
        model.Run = command; model.Sync = sync;
        properties.AddCommand("RegisteredRun", command);
        Check(command.ExecutionModes == CommandExecutionModes.Async &&
            properties.Describe("Async").Contains("\"commandModes\":[\"Async\"]", StringComparison.Ordinal), "Async metadata mismatch.");
        app.LoadFleck(Page, properties); app.OpenWindow(); app.Poll();
        app.AdvanceAnimationClock(0);
        var runButton = app.FindFleckNode("run");
        runButton.RegisterVisualState("Common","Normal");
        runButton.RegisterVisualState("Common","Busy","<Storyboard><Timeline Property='Opacity' DurationMs='100'><KeyFrame Position='0' Value='0.3'/><KeyFrame Position='1' Value='0.8'/></Timeline></Storyboard>");
        runButton.GoToVisualState("Common","Busy");
        Check((double)runButton.GetProperty(PropertyTarget.Opacity)! == .3,"Visual state applies its first key frame");
        runButton.GoToVisualState("Common","Normal");
        Check((double)runButton.GetProperty(PropertyTarget.Opacity)! == 1,"Leaving state restores opacity");
        var eventRoot = app.GetFleckRoot();
        var route = new List<string>();
        eventRoot.SetActivationHandler(EventPhase.Preview,(_,_) => { route.Add("preview"); return false; });
        runButton.SetActivationHandler(EventPhase.Direct,(_,_) => { route.Add("direct"); return false; });
        eventRoot.SetActivationHandler(EventPhase.Bubble,(_,_) => { route.Add("bubble"); return true; });
        app.ActivateButton();
        Check(route.SequenceEqual(["preview","direct","bubble"]) && calls == 0,"Handled activation precedes command execution");
        eventRoot.SetActivationHandler(EventPhase.Preview,null);
        runButton.SetActivationHandler(EventPhase.Direct,null);
        eventRoot.SetActivationHandler(EventPhase.Bubble,null);
        runButton.StartAnimation(PropertyTarget.Opacity,new Timeline(
            [new KeyFrame(0,1.0),new KeyFrame(1,0.5)],1000));
        app.ActivateButton(); app.ActivateButton();
        Pump(app, () => model.Caption == "Working");
        app.AdvanceAnimationClock(500); app.Poll();
        Check((double)runButton.GetProperty(PropertyTarget.Opacity)! == .75,"Animation advances during asynchronous command");
        Check(command.IsRunning && calls == 1 && resource.Length == 1, "Single-flight or resource identity failed.");
        Check(app.FindFleckNode("once").Text == "Initial", "OneTime binding changed.");
        // Activate another command of the same model through a temporary root.
        var root = app.GetFleckRoot();
        var syncButton = app.CreateNode(NodeKind.Button);
        properties.Bind(syncButton, PropertyTarget.Command, nameof(Model.Sync));
        app.SetRoot(syncButton); app.Poll(); app.ActivateButton();
        Check(model.Count == 1, "Unrelated command on the same model was blocked.");
        app.SetRoot(root); app.Poll();
        model.Count = 10; model.Notify(nameof(Model.Count));
        Check(app.FindFleckNode("count").Text == "10", "UI notification was blocked while async work ran.");
        app.QueueFleckRefresh(Page.Replace("Text=\"Before\"", "Text=\"After\"", StringComparison.Ordinal), properties);
        app.Poll();
        Check(app.GetFleckRefreshStatus().Pending, "Running command's page was retired.");
        gate.SetResult();
        Pump(app, () => !command.IsRunning && !app.GetFleckRefreshStatus().Pending);
        Check(model.Count == 11 && model.Caption == "Done" && app.FindFleckNode("queued").Text == "After", "Completion or Fleck refresh failed.");
        app.ActivateButton();
        Pump(app, () => !command.IsRunning);
        Check(calls == 2 && resource.Length == 2, "Completed command could not execute again.");
    }
    private static void ErrorsAndRetry(string stage)
    {
        using var app = new App(Renderer.Software, headless: true);
        var model = new Model();
        using var properties = app.CreatePropertyModel(model);
        using var sync = app.CreateCommand(_ => { });
        bool fail = true;
        using var command = app.CreateAsyncCommand(async token =>
        {
            if (fail && stage == "before-await") { throw new ArithmeticException(stage); }
            await Task.Delay(1, token);
            if (fail && stage == "after-await") { throw new ArithmeticException(stage); }
            await app.InvokeAsync(() =>
            {
                model.Count++; model.Notify(nameof(Model.Count));
                if (fail && stage == "ui") { throw new ArithmeticException(stage); }
            });
        });
        model.Run = command; model.Sync = sync;
        app.LoadFleck(Page, properties); app.OpenWindow(); app.Poll(); app.ActivateButton();
        bool caught = false;
        try { Pump(app, () => !command.IsRunning); }
        catch (ArithmeticException error) when (error.Message == stage) { caught = true; }
        Check(caught && !command.IsRunning, "Failure was lost or command stayed busy.");
        Check(model.Count == (stage == "ui" ? 1 : 0), "Failure unexpectedly rolled back business changes.");
        fail = false;
        app.ActivateButton(); Pump(app, () => !command.IsRunning);
        Check(model.Count == (stage == "ui" ? 2 : 1), "Retry failed.");
    }
    private static void CancelPendingInvoke(string target)
    {
        using var app = new App(Renderer.Software, headless: true);
        var model = new Model();
        using var properties = app.CreatePropertyModel(model);
        using var sync = app.CreateCommand(_ => { });
        using var queued = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var command = app.CreateAsyncCommand(async token =>
        {
            try
            {
                // No explicit token: InvokeAsync inherits the originating command's cancellation.
                var pending = app.InvokeAsync(() => { model.Count++; model.Notify(); });
                app.Post(() => model.Count += 100);
                queued.Set();
                await pending;
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { exited.Set(); }
        });
        model.Run = command; model.Sync = sync;
        app.LoadFleck(Page, properties); app.OpenWindow(); app.Poll(); app.ActivateButton();
        Check(queued.Wait(TimeSpan.FromSeconds(5)), "Worker did not enqueue UI work.");
        switch (target)
        {
            case "command": command.Dispose(); break;
            case "model": properties.Dispose(); break;
            case "app": app.Dispose(); break;
            case "close": app.RequestClose(); app.Poll(); break;
            default: command.Cancel(); Pump(app, () => !command.IsRunning); break;
        }
        Check(exited.Wait(TimeSpan.FromSeconds(5)), "Cancellation left worker awaiting UI.");
        if (target is not ("app" or "close")) { app.Poll(); }
        Check(model.Count == 0, "Cancelled UI work touched the model.");
    }
    private static void DispatcherCompletion()
    {
        using var app = new App(Renderer.Software, headless: true);
        app.LoadFleck("<Window xmlns='urn:sunfleck:v1'><TextBlock/></Window>"); app.OpenWindow();
        int calls = 0;
        var direct = app.InvokeAsync(() => calls++);
        Check(direct.IsCompletedSuccessfully && calls == 1, "UI invocation did not finish inline.");
        using var cancel = new CancellationTokenSource();
        Task? pending = null;
        Task.Run(() => { pending = app.InvokeAsync(() => calls++, cancel.Token); }).GetAwaiter().GetResult();
        cancel.Cancel(); app.Poll();
        Check(pending!.IsCanceled && calls == 1, "Cancelled queued callback executed.");
        var failure = Task.Run(async () => await app.InvokeAsync(() => throw new ArithmeticException("dispatch")));
        Pump(app, () => failure.IsCompleted);
        Check(failure.IsFaulted && failure.Exception!.InnerException is ArithmeticException, "UI exception was not returned to caller.");
        // App disposal cancels ordinary pending invocations even without a command.
        Task.Run(() => { pending = app.InvokeAsync(() => calls++); }).GetAwaiter().GetResult();
        app.Dispose();
        Check(pending!.IsCanceled && calls == 1, "App disposal stranded an ordinary UI invocation.");
    }
    private static void CancellationCallbackFailure()
    {
        using var app = new App(Renderer.Software, headless: true);
        var model = new Model();
        using var properties = app.CreatePropertyModel(model);
        using var sync = app.CreateCommand(_ => { });
        using var ready = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        using var command = app.CreateAsyncCommand(async token =>
        {
            using var registration = token.Register(() => throw new ArithmeticException("cancel handler"));
            try { ready.Set(); await Task.Delay(Timeout.Infinite, token); }
            finally { exited.Set(); }
        });
        model.Run = command; model.Sync = sync;
        app.LoadFleck(Page, properties); app.OpenWindow(); app.Poll(); app.ActivateButton();
        Check(ready.Wait(TimeSpan.FromSeconds(5)), "Cancellation test did not start.");
        bool caught = false;
        try { app.Dispose(); }
        catch (AggregateException error) { caught = error.Flatten().InnerExceptions.Any(e => e is ArithmeticException); }
        Check(caught && exited.IsSet, "A cancellation handler exception skipped shutdown or was lost.");
        app.Dispose(); // Cleanup completed despite the user callback failure.
    }
    private static void RejectSyncOverride()
    {
        using var app = new App(Renderer.Software, headless: true);
        var model = new Model();
        using var properties = app.CreatePropertyModel(model);
        using var command = app.CreateAsyncCommand(_ => Task.CompletedTask);
        using var sync = app.CreateCommand(_ => { });
        model.Run = command; model.Sync = sync;
        bool rejected = false;
        try { app.LoadFleck(Page.Replace("CommandMode=\"Async\"", "CommandMode=\"Sync\"", StringComparison.Ordinal), properties); }
        catch (SunfleckException) { rejected = true; }
        Check(rejected, "Sync override could block UI waiting for asynchronous dispatch.");
    }
}
