using Sunfleck;

internal static class FleckFileTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sunfleck-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "main.fleck");
        const string page = "<Window xmlns='urn:sunfleck:v1'><TextBlock Id='label' Text='Before'/></Window>";
        File.WriteAllText(file, page);
        try
        {
            using var app = new App(Renderer.Software, headless: true);
            async Task<T> OnUi<T>(Func<T> read)
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                app.Post(() =>
                {
                    try { completion.SetResult(read()); }
                    catch (Exception error) { completion.SetException(error); }
                });
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            async Task Until(Func<bool> check)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!await OnUi(check))
                {
                    if (DateTime.UtcNow >= deadline) { throw new TimeoutException("Fleck file update failed."); }
                    await Task.Delay(20);
                }
            }
            var edits = Task.Run(async () =>
            {
                try
                {
                    await Until(() => app.FindFleckNode("label").Text == "Before");
                    File.WriteAllText(file, page.Replace("Before", "After", StringComparison.Ordinal));
                    await Until(() => app.FindFleckNode("label").Text == "After");
                    File.WriteAllText(file, "<Window><");
                    await Until(() => app.GetFleckRefreshStatus().Result != Result.Ok);
                    if (!await OnUi(() => app.FindFleckNode("label").Text == "After"))
                    {
                        throw new InvalidOperationException("Invalid page replaced the last working page.");
                    }
                    File.Delete(file);
                    await Task.Delay(350);
                    File.WriteAllText(file, page);
                    await Until(() => app.FindFleckNode("label").Text == "Before");
                    string palette = Path.Combine(directory, "palette.fleck");
                    const string dictionary = "<ResourceDictionary xmlns='urn:sunfleck:v1'><Resource Key='Label' Value='Palette A'/></ResourceDictionary>";
                    File.WriteAllText(palette,dictionary);
                    File.WriteAllText(file,"<Window xmlns='urn:sunfleck:v1'><Window.Resources><ResourceDictionary Source='palette.fleck'/></Window.Resources><TextBlock Id='label' Text='{StaticResource Label}'/></Window>");
                    await Until(() => app.FindFleckNode("label").Text == "Palette A");
                    File.WriteAllText(palette,dictionary.Replace("Palette A","Palette B",StringComparison.Ordinal));
                    await Until(() => app.FindFleckNode("label").Text == "Palette B");
                    File.WriteAllText(palette,"<ResourceDictionary broken");
                    await Until(() => app.GetFleckRefreshStatus().Result != Result.Ok);
                    if (!await OnUi(() => app.FindFleckNode("label").Text == "Palette B")) { throw new InvalidOperationException("Invalid palette replaced the active page."); }
                    File.WriteAllText(palette,dictionary);
                    await Until(() => app.FindFleckNode("label").Text == "Palette A");
                    string componentFile = Path.Combine(directory, "Card.fleck");
                    const string card = "<UserControl xmlns='urn:sunfleck:v1' Class='Card' Namespace='urn:test'><TextBlock Id='name' Text='Original'/></UserControl>";
                    File.WriteAllText(componentFile, card);
                    File.WriteAllText(file, "<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:test'><c:Card Id='card'/></Window>");
                    await Until(() => { try { return app.FindComponent("card").FindNode("name").Text == "Original"; } catch (SunfleckException) { return false; } });
                    File.WriteAllText(componentFile, card.Replace("Original", "Updated", StringComparison.Ordinal));
                    await Until(() => app.FindComponent("card").FindNode("name").Text == "Updated");
                    File.WriteAllText(componentFile, "<UserControl broken");
                    await Until(() => app.GetFleckRefreshStatus().Result != Result.Ok);
                    if (!await OnUi(() => app.FindComponent("card").FindNode("name").Text == "Updated")) { throw new InvalidOperationException("Invalid component destroyed the active page."); }
                    File.WriteAllText(componentFile, card);
                    await Until(() => app.GetFleckRefreshStatus().Result == Result.Ok && app.FindComponent("card").FindNode("name").Text == "Original");
                    File.Delete(componentFile);
                    await Until(() => app.GetFleckRefreshStatus().Result != Result.Ok);
                    if (!await OnUi(() => app.FindComponent("card").FindNode("name").Text == "Original")) { throw new InvalidOperationException("Missing component destroyed the active page."); }
                    File.WriteAllText(componentFile, card.Replace("Original", "Restored", StringComparison.Ordinal));
                    await Until(() => app.GetFleckRefreshStatus().Result == Result.Ok && app.FindComponent("card").FindNode("name").Text == "Restored");
                }
                finally { app.Post(app.RequestClose); }
            });
            app.RunFleckFile(file);
            edits.GetAwaiter().GetResult();
            Console.WriteLine("Fleck file: page/component update, rollback, deletion and recovery passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
