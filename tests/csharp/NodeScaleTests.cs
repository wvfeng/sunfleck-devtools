using Sunfleck;

internal static class NodeScaleTests
{
    internal static void Run(string xmlPath, string binaryPath)
    {
        foreach (int count in new[] { 511, 512, 513, 4096 })
        {
            using (var app = new App(Renderer.Software, headless: true))
            {
                var list = app.CreateNode(NodeKind.ListView);
                for (int i = 0; i < count; i++)
                {
                    list.Append(app.CreateNode(NodeKind.ListItem));
                }
                list.Value = (uint)count - 1;
                if (list.Value != count - 1)
                {
                    throw new InvalidOperationException("Large list selection failed.");
                }
                var batch = app.CreateNode(NodeKind.ComboBox);
                batch.SetItems(Enumerable.Repeat("Item", count).ToArray());
                batch.Value = (uint)count - 1;
                app.SetRoot(list);
                app.OpenWindow();
                app.Poll();
            }
        }
        foreach (bool binary in new[] { false, true })
        {
            using (var app = new App(Renderer.Software, headless: true))
            {
                if (binary)
                {
                    app.LoadFleckResource(File.ReadAllBytes(binaryPath));
                }
                else
                {
                    app.LoadFleck(File.ReadAllText(xmlPath));
                }
                if (app.FindFleckNode("list").Value != 4095)
                {
                    throw new InvalidOperationException("Large Fleck page selection failed.");
                }
                app.OpenWindow();
                app.Poll();
            }
        }
    }
}
