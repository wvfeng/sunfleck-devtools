using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(CounterHotReload))]

// Keep the original getter delegate alive across a supported property deletion.
// Invoking it after deletion raises the runtime rude edit used by dotnet watch.
var model = new Counter();
var getter = (Func<string>)typeof(Counter).GetProperties().Single()
    .GetMethod!.CreateDelegate(typeof(Func<string>), model);
CounterHotReload.Getter = getter;
File.WriteAllText(Environment.GetEnvironmentVariable("SUNFLECK_PDB_REPRO_PID")!, Environment.ProcessId.ToString());
while (true)
{
    CounterHotReload.Refresh();
    Thread.Sleep(16);
}

public class Counter
{
    public string Caption => "baseline";
}

internal static class CounterHotReload
{
    internal static Func<string>? Getter;
    private static int pending;
    internal static void Refresh()
    {
        if (Interlocked.Exchange(ref pending, 0) == 0) { return; }
        try { _ = Getter?.Invoke(); }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().FullName); }
    }
    public static void UpdateApplication(Type[]? _) => Interlocked.Exchange(ref pending, 1);
}
