using System.ComponentModel;
using Sunfleck;

using var app = new App(Renderer.Software);
app.EnableDebugServer(Environment.GetEnvironmentVariable("SUNFLECK_DEBUG_PIPE") ?? "sunfleck-desktop-audit");
var model = new DesktopModel();
using var properties = app.CreatePropertyModel(model);
using var toggle = app.CreateCommand(_ => { model.Fail = !model.Fail; model.Notify(); });
model.ToggleFailure = toggle;
using var command = app.CreateAsyncCommand(async cancellation =>
{
    await app.InvokeAsync(() => { model.Status = "Working"; model.Notify(); });
    await Task.Delay(4000, cancellation);
    await app.InvokeAsync(() => { model.Status = "Complete"; model.Notify(); });
});
model.Run = command;
string page = Path.Combine(AppContext.BaseDirectory, "main.fleck");
string source = File.ReadAllText(page);
app.LoadFleck(source, properties);
app.SetFleckSourceUri(new Uri(page).AbsoluteUri);
app.OpenWindow();
while (true)
{
    try { if (app.Poll(50) == Result.Exited) { break; } }
    catch (InvalidOperationException error) when (error.Message == "Acceptance getter failure")
    {
        // Native callbacks surface the original business exception at the Poll boundary.
        // Keep this expected failure recoverable so the IDE can inspect its diagnostic.
    }
    string updated;
    try { updated = File.ReadAllText(page); }
    catch (IOException) { continue; }
    if (updated != source) { app.QueueFleckRefresh(updated, properties); source = updated; }
}

public sealed class DesktopModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string name = "Draft ";
    public string Name { get => name; set { name = value; Notify(); } }
    public string Status { get; set; } = "Ready";
    public bool Fail { get; set; }
    public string Probe => Fail ? throw new InvalidOperationException("Acceptance getter failure") : "Getter healthy";
    public Command ToggleFailure { get; set; } = null!;
    [CommandModes(CommandExecutionModes.Async)]
    public Command Run { get; set; } = null!;
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
