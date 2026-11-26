using Sunfleck;

internal static class DockTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void Run()
    {
        using (var app = new App(Renderer.Software, headless: true))
        {
            app.SetWindowOptions("Dock managed tests", 800, 600);
            var host = app.CreateNode(NodeKind.DockHost);
            app.SetRoot(host);
            var dock = app.CreateDock(host);
            app.ApplyTheme(BuiltinTheme.Material3, "<Resource Key='dock.tab_height' Value='31'/>");
            app.LoadTheme(System.Text.Encoding.UTF8.GetBytes("FLKTH\u0001<Theme Version='1' Base='Fluent2'/>"));
            var content = app.CreateNode(NodeKind.TextArea);
            content.Text = "Managed content survives docking";
            var editor = dock.Register("editor.文档", "编辑器", content);
            editor.SetIcon("D");
            var peer = dock.Register("peer", "Peer", app.CreateNode(NodeKind.Panel));
            var tool = dock.Register("tool", "Explorer", app.CreateNode(NodeKind.TextArea), DockKind.Tool);
            dock.CaptureDefault();
            app.AdvanceAnimationClock(0);
            content.StartAnimation(PropertyTarget.Opacity,new Timeline(
                [new KeyFrame(0,1.0),new KeyFrame(1,0.8)],1000));
            app.OpenWindow();
            app.Poll();
            Assert(editor.Id == "editor.文档", "Dock ID UTF-8 roundtrip failed.");
            var saved = dock.Save();
            bool veto = true;
            EventHandler<DockEventArgs> handler = (_, args) =>
            {
                if (args.Kind == DockEventKind.Closing)
                {
                    args.Cancel = veto;
                }
            };
            dock.Event += handler;
            Assert(!editor.Close() && editor.State == DockState.Docked, "Close veto was ignored.");
            veto = false;
            Assert(editor.Close() && editor.State == DockState.Closed, "Document did not close.");
            editor.Show();
            tool.AutoHide(DockEdge.Right);
            Assert(tool.State == DockState.AutoHidden, "Tool did not auto hide.");
            tool.Pin();
            editor.Float(new DockRectangle(90, 90, 420, 300));
            app.AdvanceAnimationClock(500);
            app.Poll();
            Assert((double)content.GetProperty(PropertyTarget.Opacity)! == .9,"Floating content retains its active timeline");
            Assert(dock.Windows().Length == 1 && dock.ReadPixels(dock.Windows()[0]).Width == 420, "Floating surface did not render.");
            content.ControlAnimation(PropertyTarget.Opacity,AnimationAction.Stop);
            editor.Move(peer);
            Assert(content.Text == "Managed content survives docking", "Moving lost document content.");
            dock.Restore(saved);
            Assert(dock.Save() == saved, "Layout roundtrip changed the layout.");
            try
            {
                dock.Restore("{bad json");
                throw new InvalidOperationException("Invalid layout was accepted.");
            }
            catch (FleckException)
            {
                Assert(dock.Save() == saved, "Failed restore mutated the layout.");
            }
            DrawContext? retained = null;
            app.RegisterDockSkin("Managed", (draw, part) =>
            {
                retained = draw;
                if (part.Part != DockPart.Tab)
                {
                    return false;
                }
                if (part.ItemId == "editor.文档")
                {
                    Assert(part.Icon == "D", "Dock icon did not reach managed paint context.");
                }
                draw.PushClip(part.Bounds);
                draw.Fill(part.Bounds, 0x334455, radius: 3);
                draw.Stroke(part.Bounds, 0x8899aa);
                draw.Text(part.Bounds, part.Title, part.Foreground);
                draw.Image(new DockRectangle(part.Bounds.X, part.Bounds.Y, 2, 2), [255, 0, 0, 255], 1, 1);
                draw.PopClip();
                return true;
            });
            app.LoadTheme("<Theme Base='Material3' Dark='true' Skin='Managed'/>");
            GC.Collect();
            app.Poll();
            Assert(retained is not null, "Managed painter was not invoked.");
            try
            {
                retained!.Fill(new DockRectangle(0, 0, 1, 1), 0);
                throw new InvalidOperationException("A retained drawing context remained valid.");
            }
            catch (ObjectDisposedException)
            {
            }
            app.RegisterDockSkin("Managed", (_, _) => throw new ArithmeticException("Paint callback"));
            try
            {
                app.Poll();
                throw new InvalidOperationException("Managed paint exception was lost.");
            }
            catch (ArithmeticException)
            {
            }
            app.RegisterDockSkin("Managed", null);
            app.LoadTheme("<Theme Base='Radix'><DockTemplate Part='Title'><Stack Role='Drag'><Text Text='{Title}'/><Button Role='Close' Text='X' Width='30'/></Stack></DockTemplate></Theme>");
            app.Poll();
            dock.Event -= handler;
            dock.Event += (_, args) =>
            {
                if (args.Kind == DockEventKind.Closing)
                {
                    throw new ArithmeticException("Closing callback");
                }
            };
            try
            {
                editor.Close();
                throw new InvalidOperationException("Managed close exception was lost.");
            }
            catch (ArithmeticException)
            {
                Assert(editor.State == DockState.Docked, "Throwing close callback removed the document.");
            }
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            app.LoadFleck("<Window xmlns='urn:sunfleck:v1'><DockHost Id='dock'><DockGroup><DockDocument Id='doc' Title='Document'><TextArea Text='Fleck content'/></DockDocument></DockGroup></DockHost></Window>");
            var dock = app.CreateDock(app.FindFleckNode("dock"));
            app.OpenWindow();
            app.Poll();
            Assert(dock.Find("doc").State == DockState.Docked, "Managed Fleck Dock was not initialized.");
        }
    }
}
