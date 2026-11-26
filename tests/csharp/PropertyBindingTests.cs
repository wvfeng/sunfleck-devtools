using System.ComponentModel;
using Sunfleck;

internal static class PropertyBindingTests
{
    private sealed class Model : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? changed;
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { changed += value; ++Subscribers; }
            remove { changed -= value; --Subscribers; }
        }
        public int Subscribers { get; private set; }
        public int Count { get; set; }
        public string Caption { get; set; } = "Increment";
        public bool Enabled { get; set; } = true;
        public string Name { get; set; } = "Initial";
        public int Volume { get; set; } = 25;
        public Command Increment { get; set; } = null!;
        public void Notify(string? path) => changed?.Invoke(this, new PropertyChangedEventArgs(path));
    }
    private sealed class BrokenModel : INotifyPropertyChanged
    {
        public string Caption => throw new ArithmeticException("Getter failure");
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
    private const string Page = """
        <Window xmlns="urn:sunfleck:v1" Width="480" Height="460">
          <StackPanel>
            <Button Id="button" Content="{Binding Caption}" IsEnabled="{Binding Enabled}" Command="{Binding Increment}"/>
            <TextBlock Id="count" Text="{Binding Count, StringFormat='Count: {0}'}"/>
            <TextBlock Id="other" Text="{Binding Count, StringFormat='Total, {0}'}"/>
            <TextBlock Id="snapshot" Text="{Binding Count, Mode=OneTime}"/>
            <TextBox Id="editor" Text="{Binding Name}"/>
            <Slider Id="slider" Value="{Binding Volume}"/>
          </StackPanel>
        </Window>
        """;

    private static void Check(bool condition, string message)
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
            var model = new Model();
            using (var command = app.CreateCommand(_ =>
            {
                ++model.Count;
                model.Notify(nameof(Model.Count));
            }))
            {
                model.Increment = command;
                using (var adapter = app.CreatePropertyModel(model))
                {
                    Check(model.Subscribers == 1, "Model was not subscribed.");
                    app.LoadFleck(Page, adapter);
                    var button = app.FindFleckNode("button");
                    var count = app.FindFleckNode("count");
                    Check(button.Text == "Increment" && button.IsEnabled && count.Text == "Count: 0", "Initial binding failed.");
                    app.OpenWindow();
                    app.Poll();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    app.ActivateButton();
                    Check(count.Text == "Count: 1" && app.FindFleckNode("other").Text == "Total, 1" &&
                        app.FindFleckNode("snapshot").Text == "0", "Formats or OneTime failed.");
                    adapter.BeginUpdate();
                    model.Count = 2;
                    model.Notify(nameof(Model.Count));
                    model.Count = 3;
                    model.Notify(nameof(Model.Count));
                    Check(count.Text == "Count: 1", "Batch was applied early.");
                    adapter.EndUpdate();
                    Check(count.Text == "Count: 3", "Batch was not applied.");
                    model.Caption = "Custom";
                    model.Enabled = false;
                    model.Notify(null);
                    Check(button.Text == "Custom" && !button.IsEnabled, "Multiple button properties did not update.");
                    app.Poll();
                    try
                    {
                        app.ActivateButton();
                        throw new InvalidOperationException("Disabled button was available for activation.");
                    }
                    catch (SunfleckException error)
                    {
                        Check(error.Result == Result.InvalidState, "Unexpected disabled activation status.");
                    }
                    Check(model.Count == 3, "Disabled button executed.");
                    Task.Run(() =>
                    {
                        model.Name = "From worker";
                        model.Notify(nameof(Model.Name));
                    }).GetAwaiter().GetResult();
                    app.Poll();
                    Check(app.FindFleckNode("editor").Text == "From worker", "Worker notification was not posted.");
                    model.Notify("NotExported");
                    button.Text = "Literal";
                    model.Caption = "Ignored";
                    model.Enabled = true;
                    model.Notify(null);
                    Check(button.Text == "Literal" && button.IsEnabled, "Literal override detached other properties.");
                    adapter.Dispose();
                    Check(model.Subscribers == 0, "Model event was not unsubscribed.");
                    model.Count = 9;
                    model.Notify(null);
                    Check(count.Text == "", "Disposed context must clear its value and stop receiving updates.");
                }
            }
        }
        using (var app = new App(Renderer.Software, headless: true))
        {
            using (var adapter = app.CreatePropertyModel(new BrokenModel()))
            {
                try
                {
                    app.LoadFleck("<Window xmlns='urn:sunfleck:v1'><Button Content='{Binding Caption}'/></Window>", adapter);
                    throw new InvalidOperationException("Getter exception was lost.");
                }
                catch (ArithmeticException error)
                {
                    Check(error.Message == "Getter failure", "Getter exception was wrapped.");
                }
            }
        }
        var lifetimeApp = new App(Renderer.Software, headless: true);
        var lifetimeModel = new Model();
        var lifetimeAdapter = lifetimeApp.CreatePropertyModel(lifetimeModel);
        lifetimeApp.Dispose();
        Check(lifetimeModel.Subscribers == 0, "App disposal did not unsubscribe models.");
        lifetimeAdapter.Dispose();
    }
}
