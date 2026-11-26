using Sunfleck;

// Call Register before app.LoadTheme(File.ReadAllText("theme.fleck")).
internal static class AccentTabs
{
    internal static void Register(App app)
    {
        app.RegisterDockSkin("AccentTabs", (draw, part) =>
        {
            if (part.Part != DockPart.Tab)
            {
                return false;
            }
            draw.Fill(part.Bounds, part.Background, radius: 4 * part.Dpi / 96);
            var underline = part.Bounds with { Y = part.Bounds.Y + part.Bounds.Height - 3, Height = 3 };
            draw.Fill(underline, 0xd4a373);
            draw.Text(part.Bounds, part.Title, part.Foreground);
            return true;
        });
    }
}
