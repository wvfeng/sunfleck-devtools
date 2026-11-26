using Sunfleck;

internal static class DocumentTests
{
    public static void Run()
    {
        using var app = new App(Renderer.Software, headless: true);
        app.LoadFleck("""<Window xmlns="urn:sunfleck:v1"><DocumentView Id="article"/></Window>""");
        var article = app.FindFleckNode("article");
        article.LoadDocument("""{"version":1,"blocks":[{"kind":"heading","id":"hello","level":1,"spans":[{"text":"Hello document"}]}]}""");
        app.OpenWindow();
        app.Poll();
        article.SelectDocument();
        if (!article.DocumentSelection.Contains("Hello document", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Document selection did not round-trip.");
        }
        article.ScrollToHeading("hello");
        article.DocumentScroll = 0;
        if (article.DocumentScroll != 0 || article.TakeDocumentEvent().Event != DocumentEvent.None)
        {
            throw new InvalidOperationException("Unexpected document state.");
        }
        try
        {
            article.LoadDocument("{}");
            throw new InvalidOperationException("Invalid document accepted.");
        }
        catch (SunfleckException)
        {
            if (!article.DocumentSelection.Contains("Hello document", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid document damaged the old content.");
            }
        }
        app.RefreshFleck("""<Window xmlns="urn:sunfleck:v1"><TextBlock Id="replacement" Text="New page"/></Window>""");
        try
        {
            _ = article.DocumentScroll;
            throw new InvalidOperationException("Removed DocumentView handle was accepted.");
        }
        catch (SunfleckException) { }
    }
}
