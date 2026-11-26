using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunfleck;
using Sunfleck.Development;

internal static class InspectorDesktopTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref Point point);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    // The service is a separate process. Only the UI owner thread pumps the App;
    // pipe reads run on a worker so a protocol response never blocks native Poll.
    private sealed class Service : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> stderr;
        private readonly Action pump;
        private readonly StreamWriter transcript;
        private int sequence;

        internal Service(Action pump, string directory)
        {
            this.pump = pump;
            transcript = new StreamWriter(Path.Combine(directory, "protocol.jsonl"));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(LspServer).Assembly.Location);
            start.ArgumentList.Add("lsp");
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the shared service.");
            stderr = process.StandardError.ReadToEndAsync();
        }

        internal JsonObject Request(string method, JsonObject? parameters = null, bool pumpWindow = true)
        {
            int id = ++sequence;
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
            transcript.WriteLine(request.ToJsonString());
            LspServer.Write(process.StandardInput.BaseStream, request);
            var until = Stopwatch.StartNew();
            while (until.Elapsed < TimeSpan.FromSeconds(8))
            {
                var reading = Task.Run(() => LspServer.Read(process.StandardOutput.BaseStream));
                while (!reading.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(8))
                {
                    if (pumpWindow)
                    {
                        pump();
                    }
                    Thread.Sleep(2);
                }
                Check(reading.IsCompleted, "Shared service timed out: " + method);
                var response = reading.GetAwaiter().GetResult() ?? throw new InvalidOperationException("Shared service exited: " +
                    (stderr.IsCompleted ? stderr.GetAwaiter().GetResult() : "no stderr"));
                transcript.WriteLine(response.ToJsonString());
                transcript.Flush();
                if (response["id"]?.GetValue<int>() != id)
                {
                    continue;
                }
                Check(response["error"] is null, "Shared service rejected " + method + ": " + response["error"]);
                return response["result"] as JsonObject ?? new JsonObject();
            }
            throw new InvalidOperationException("Shared service response did not arrive: " + method);
        }

        internal void Shutdown()
        {
            Request("shutdown");
            LspServer.Write(process.StandardInput.BaseStream, new JsonObject { ["method"] = "exit" });
            Check(process.WaitForExit(3000) && process.ExitCode == 0, "Shared service did not exit normally.");
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(); // This exact child belongs to this test.
                process.WaitForExit(3000);
            }
            transcript.Dispose();
            process.Dispose();
        }
    }

    internal static void Run(string output)
    {
        Check(OperatingSystem.IsWindows(), "This input driver requires Windows.");
        string directory = Path.Combine(Path.GetFullPath(output), "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var result = new StreamWriter(Path.Combine(directory, "result.log")) { AutoFlush = true };
        Console.WriteLine("Inspector protocol evidence: " + directory);
        try
        {
            RunWindows(directory, result);
            result.WriteLine("PASS: real stdio service / runtime mailbox / Windows message input");
        }
        catch (Exception error)
        {
            result.WriteLine("FAIL: " + error);
            throw;
        }
    }

    private static void RunWindows(string directory, StreamWriter result)
    {
        string title = "Inspector protocol " + Environment.ProcessId;
        string main = $"<Window xmlns='urn:sunfleck:v1' Model='Live' Title='{title}' Width='640' Height='420'>" +
            "<DockHost Id='dock'><DockGroup><DockDocument Id='main' Title='Main'><StackPanel Id='scroll'>" +
            "<TextBox Id='editor' Text='{Binding Name, Mode=TwoWay}'/><Button Id='button' Content='Run' Command='{Binding Run}'/>" +
            string.Concat(Enumerable.Range(0, 40).Select(index => $"<TextBlock Text='Row {index}'/>")) +
            "</StackPanel></DockDocument><DockDocument Id='floating' Title='Floating'><TextBox Id='floating-editor' Text='Floating draft'/>" +
            "</DockDocument></DockGroup></DockHost></Window>";
        string secondary = $"<Window xmlns='urn:sunfleck:v1' Model='Live' Title='{title} secondary' Width='420' Height='300'>" +
            "<StackPanel><Button Id='button' Content='Secondary run' Command='{Binding Run}'/><ComboBox Id='combo'>" +
            "<ListItem Text='First'/><ListItem Text='Second'/><ListItem Text='Third'/></ComboBox></StackPanel></Window>";
        string source = Path.Combine(directory, "main.fleck"), otherSource = Path.Combine(directory, "secondary.fleck");
        File.WriteAllText(source, main);
        File.WriteAllText(otherSource, secondary);
        File.WriteAllText(Path.Combine(directory, "secondary-initial.fleck"), secondary);
        using var app = new App(Renderer.Software);
        int calls = 0, writes = 0;
        string business = "Initial";
        using var command = app.CreateCommand(_ => calls++);
        using var model = app.CreatePropertyModel().Add("Name", () => business, value => { writes++; business = value; }).AddCommand("Run", command);
        app.LoadFleck(main, model);
        app.SetFleckSourceUri(new Uri(source).AbsoluteUri);
        var editor = app.FindFleckNode("editor");
        var dock = app.CreateDock(app.FindFleckNode("dock"));
        var second = app.CreateWindow();
        Node? combo = null;
        second.WithContext(current =>
        {
            current.LoadFleck(secondary, model);
            current.SetFleckSourceUri(new Uri(otherSource).AbsoluteUri);
            combo = current.FindFleckNode("combo");
            combo.Value = 0;
        });
        app.OpenWindow();
        second.Open();
        void Pump() => Check(app.Poll(5) != Result.Exited, "Runtime exited before the test completed.");
        for (int index = 0; index < 4; index++)
        {
            Pump();
        }
        dock.Find("floating").Float(new DockRectangle(120, 120, 360, 240));
        string layout = dock.Save();
        bool primaryClosed = false;
        string pipe = "sunfleck-desktop-" + Guid.NewGuid().ToString("N");
        app.EnableDebugServer(pipe);
        using var service = new Service(Pump, directory);
        service.Request("initialize");
        JsonObject Parameters() => new() { ["pipe"] = pipe, ["owner"] = "desktop-regression" };
        JsonObject Snapshot() => service.Request("sunfleck/debugSnapshot", Parameters());
        JsonObject Wait(Func<JsonObject, bool> accept)
        {
            var until = Stopwatch.StartNew();
            JsonObject? last = null;
            while (until.Elapsed < TimeSpan.FromSeconds(6))
            {
                Pump();
                var snapshot = Snapshot();
                last = snapshot;
                if (accept(snapshot))
                {
                    return snapshot;
                }
                Thread.Sleep(5);
            }
            File.WriteAllText(Path.Combine(directory, "timeout-snapshot.json"), last?.ToJsonString() ?? "null");
            throw new InvalidOperationException("Runtime snapshot did not reach the expected state.");
        }
        JsonObject Submit(string method, JsonNode? node = null, bool pump = true)
        {
            var parameters = Parameters();
            parameters["method"] = method;
            parameters["object"] = node?["id"]?.DeepClone();
            parameters["page"] = node?["page"]?.DeepClone();
            return service.Request("sunfleck/debugRequest", parameters, pump);
        }
        JsonObject Await(JsonObject reply, Result expected = Result.Ok)
        {
            Check(reply["result"]!.GetValue<int>() == (int)expected, "Unexpected command result: " + reply);
            return Snapshot();
        }
        JsonObject Capture(string name)
        {
            var snapshot = Snapshot();
            File.WriteAllText(Path.Combine(directory, name + ".json"), snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            result.WriteLine(name + ": selection=" + snapshot["selection"] + ", picking=" + snapshot["picking"] + ", calls=" + calls + ", writes=" + writes);
            return snapshot;
        }
        JsonNode Node(JsonObject snapshot, string id, uint owner = 0) => snapshot["nodes"]!.AsArray().Single(node =>
            node!["fleckId"]?.GetValue<string>() == id && snapshot["windows"]!.AsArray().Any(window =>
                window!["id"]!.GetValue<int>() == node["window"]!.GetValue<int>() && window["ownerWindow"]!.GetValue<uint>() == owner))!;
        nint Window(JsonObject snapshot, JsonNode node)
        {
            string caption = snapshot["windows"]!.AsArray().Single(window => window!["id"]!.GetValue<int>() == node["window"]!.GetValue<int>())!["title"]!.GetValue<string>();
            nint window = 0;
            while ((window = FindWindowEx(0, window, "SunfleckPreview", caption)) != 0)
            {
                GetWindowThreadProcessId(window, out uint process);
                if (process == Environment.ProcessId)
                {
                    return window;
                }
            }
            throw new InvalidOperationException("Native input target is not owned by this test: " + caption);
        }
        static nint Coordinates(int x, int y) => (nint)((y << 16) | (x & 0xffff));
        void Click(JsonObject snapshot, JsonNode node)
        {
            var clip = node["clip"]!.AsArray();
            int left = clip[0]!.GetValue<int>(), top = clip[1]!.GetValue<int>(), right = clip[2]!.GetValue<int>(), bottom = clip[3]!.GetValue<int>();
            Check(right > left && bottom > top, "Input target is clipped: " + node["fleckId"]);
            int x = left + Math.Min(8, (right - left) / 2), y = (top + bottom) / 2;
            nint window = Window(snapshot, node);
            SendMessageW(window, 0x200, 0, Coordinates(x, y));
            Pump();
            SendMessageW(window, 0x201, 1, Coordinates(x, y));
            SendMessageW(window, 0x202, 0, Coordinates(x, y));
        }
        JsonObject StartPick()
        {
            var snapshot = Await(Submit("pick"));
            Check(snapshot["picking"]!.GetValue<bool>(), "Pick reply did not enable picking.");
            return snapshot;
        }
        void Pick(string id, uint owner, string evidence, string? instance = null)
        {
            var snapshot = StartPick();
            var target = instance is null ? Node(snapshot, id, owner) : snapshot["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == instance)!;
            Click(snapshot, target);
            var selected = Wait(next => next["selection"]!.GetValue<string>() == target["id"]!.GetValue<string>() && !next["picking"]!.GetValue<bool>());
            Capture(evidence);
            Check(calls == 1 && (primaryClosed || dock.Save() == layout), "Picking executed a command or changed Dock layout.");
            Check(selected["inspector"]!["parents"] is JsonObject, "Shared service did not project the picked object.");
        }

        var initial = Wait(snapshot => snapshot["windows"]!.AsArray().Count == 3);
        Capture("initial");
        Check(initial["windows"]!.AsArray().Select(window => window!["id"]!.GetValue<int>()).Distinct().Count() == 3, "Independent/floating window identities collided.");
        var mainEditor = Node(initial, "editor");
        nint mainWindow = Window(initial, mainEditor);
        SetFocus(mainWindow);
        Click(initial, mainEditor);
        SendMessageW(mainWindow, 0x102, 'A', 0); // WM_CHAR, normal business write.
        Capture("normal-input");
        Check(writes == 1 && business == editor.Text && business.Contains('A'), "Normal keyboard input did not reach the model.");
        SendMessageW(mainWindow, 0x10d, 0, 0); // WM_IME_STARTCOMPOSITION
        Await(Submit("pick"), Result.InvalidState);
        Check(!Capture("ime-busy-rejected")["picking"]!.GetValue<bool>(), "Picking started during an active IME composition.");
        SendMessageW(mainWindow, 0x10e, 0, 0); // WM_IME_ENDCOMPOSITION
        string before = editor.Text;
        var picking = StartPick();
        SendMessageW(mainWindow, 0x102, 'X', 0);
        SendMessageW(mainWindow, 0x100, 8, 0); // Backspace
        SendMessageW(mainWindow, 0x101, 8, 0);
        Capture("picking-keyboard");
        Check(editor.Text == before && writes == 1, "Picking leaked character/keyboard input into the bound editor.");
        var point = new Point { X = 30, Y = 180 };
        Check(ClientToScreen(mainWindow, ref point), "Cannot convert wheel coordinates.");
        SendMessageW(mainWindow, 0x20a, unchecked((nuint)(uint)(-120 << 16)), Coordinates(point.X, point.Y));
        var afterWheel = Capture("picking-wheel");
        Check(JsonNode.DeepEquals(Node(picking, "button")["bounds"], Node(afterWheel, "button")["bounds"]), "Picking scrolled business content.");
        Click(afterWheel, Node(afterWheel, "button"));
        Wait(snapshot => !snapshot["picking"]!.GetValue<bool>() && snapshot["selection"]!.GetValue<string>() == Node(afterWheel, "button")["id"]!.GetValue<string>());
        Check(calls == 0, "Picking invoked the primary business command.");
        Capture("picked-main");
        Click(afterWheel, Node(afterWheel, "button"));
        Pump();
        Check(calls == 1, "Normal command input did not recover after picking.");
        Pick("floating-editor", 0, "picked-floating");
        Pick("button", second.Id, "picked-secondary");
        Check(editor.Text == before && writes == 1, "Cross-window picking changed model/editor state.");

        var popup = Capture("before-popup");
        var comboNode = Node(popup, "combo", second.Id);
        Click(popup, comboNode);
        Pump();
        var popupOpen = Capture("popup-open");
        var popupItem = popupOpen["nodes"]!.AsArray().Single(node => node!["parent"]!.GetValue<string>() == comboNode["id"]!.GetValue<string>() && node["text"]!.GetValue<string>() == "Second")!;
        Pick("combo", second.Id, "picked-popup", popupItem["id"]!.GetValue<string>());
        Check(combo!.Value == 0, "Picking selected a business ComboBox item.");
        nint secondWindow = Window(popup, comboNode);
        SendMessageW(secondWindow, 0x100, 27, 0);
        SendMessageW(secondWindow, 0x101, 27, 0);
        StartPick();
        Await(Submit("cancelPick"));
        Check(!Capture("cancelled-protocol")["picking"]!.GetValue<bool>(), "Protocol cancel did not stop picking.");
        StartPick();
        SendMessageW(secondWindow, 0x100, 27, 0);
        SendMessageW(secondWindow, 0x101, 27, 0);
        Check(!Capture("cancelled-escape")["picking"]!.GetValue<bool>(), "Escape did not stop picking.");

        app.RequestClose();
        primaryClosed = true;
        Pump();
        Pick("button", second.Id, "picked-after-primary-close");
        service.Request("sunfleck/debugDisconnect", Parameters());
        service.Shutdown();
        app.EnableDebugServer(null);
        second.RequestClose();
        Check(app.Poll() == Result.Exited, "The test left an independent window running.");
        Check(calls == 1 && writes == 1, "Protocol operations changed business state.");
    }
}
