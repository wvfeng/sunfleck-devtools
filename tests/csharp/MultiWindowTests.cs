using System.Text.Json;
using Sunfleck;

internal static class MultiWindowTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Retired(Node node)
    {
        try
        {
            _ = node.Text;
        }
        catch (SunfleckException)
        {
            return;
        }
        throw new InvalidOperationException("A retired window node remained accessible.");
    }

    internal static void Run()
    {
        const string first = "<Window xmlns='urn:sunfleck:v1'><TextBox Id='editor' Text='Original'/></Window>";
        const string next = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='editor' Text='Replacement'/></Window>";
        var app = new App(Renderer.Software, headless: true);
        try
        {
            app.LoadFleck(first);
            var primary = app.FindFleckNode("editor");
            var second = app.CreateWindow();
            var third = app.CreateWindow();
            Check(second.Id != third.Id, "Independent windows reused an identity.");
            Node? secondary = null;
            int capturesA = 0, capturesB = 0;
            app.SetFleckRefreshStateCallback((phase, _) =>
            {
                if (phase == FleckRefreshPhase.Capture)
                {
                    ++capturesA;
                }
                return Result.Ok;
            });
            second.WithContext(current =>
            {
                current.LoadFleck(first);
                secondary = current.FindFleckNode("editor");
                current.SetFleckRefreshStateCallback((phase, result) =>
                {
                    if (phase == FleckRefreshPhase.Capture)
                    {
                        ++capturesB;
                    }
                    else if (result == Result.Ok)
                    {
                        Retired(secondary!);
                        Check(current.FindFleckNode("editor").Text == "Replacement", "State hook ran in the wrong window.");
                    }
                    return Result.Ok;
                });
                second.WithContext(nested => Check(nested.GetFleckRoot().DebugId == secondary.DebugId, "Same-session nesting failed."));
            });
            var expected = new InvalidOperationException("Window callback failure");
            try
            {
                second.WithContext(_ => throw expected);
                throw new InvalidOperationException("Window callback exception was lost.");
            }
            catch (InvalidOperationException failure) when (ReferenceEquals(failure, expected))
            {
            }
            Check(app.FindFleckNode("editor").DebugId == primary.DebugId, "Exception did not restore the primary window.");
            app.OpenWindow();
            second.Open();
            app.QueueFleckRefresh(next);
            second.WithContext(current => current.QueueFleckRefresh(next));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            app.Poll();
            Check(capturesA == 1 && capturesB == 1, "Per-window state delegates were lost or mixed.");
            Retired(primary);
            Retired(secondary!);
            Check(!app.GetFleckRefreshStatus().Pending, "Primary refresh tracking remained pending.");
            second.WithContext(current => Check(!current.GetFleckRefreshStatus().Pending, "Secondary refresh tracking remained pending."));
            using (var snapshot = JsonDocument.Parse(app.GetDebugSnapshot()))
            {
                var windows = snapshot.RootElement.GetProperty("windows").EnumerateArray().ToArray();
                Check(windows.Length == 3 && windows.Select(window => window.GetProperty("id").GetUInt32()).Distinct().Count() == 3,
                    "Managed snapshot omitted or merged independent windows.");
                Check(windows.Single(window => window.GetProperty("id").GetUInt32() == second.Id)
                    .GetProperty("fleckRefresh").GetProperty("sequence").GetString() == "1", "Snapshot lost a window's refresh status.");
            }
            app.RequestClose();
            Check(app.Poll() != Result.Exited, "Primary close terminated a live secondary session.");
            secondary = null;
            second.WithContext(current =>
            {
                current.SetFleckRefreshStateCallback(null);
                current.RefreshFleck(first);
                secondary = current.FindFleckNode("editor");
            });
            secondary!.Text = "Still alive";
            Check(secondary.Text == "Still alive", "Secondary node access failed after primary close.");
            second.RequestClose();
            Check(app.Poll() == Result.Exited, "An unopened session kept the App alive.");
            app.Dispose();
            try
            {
                second.Open();
                throw new InvalidOperationException("Window handle outlived its disposed App.");
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            app.Dispose();
        }
    }
}
