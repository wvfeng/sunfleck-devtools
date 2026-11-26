using Sunfleck;
using Sunfleck.Development;

internal static class ComponentTests
{
    private sealed class TestControl : Control
    {
        internal static int Initialized, Destroyed, Mounted;
        protected override void OnInitialize() { ++Initialized; }
        protected override void OnMounted() { ++Mounted; }
        protected override void OnDestroyed() { ++Destroyed; }
    }
    internal static void Run()
    {
        static void Check(bool condition) { if (!condition) { throw new InvalidOperationException("Component assertion failed."); } }
        const string resource = "<UserControl xmlns='urn:sunfleck:v1' Class='Tests.Card' Namespace='urn:tests'><StackPanel><TextBlock Id='label' Text='{Binding Title, RelativeSource=Self}'/><TextBox Id='editor'/></StackPanel></UserControl>";
        const string page = "<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:tests'><StackPanel><c:Card Id='first' FontSize='26' FontWeight='Bold' Title='one' Offset='2.5,-4' Insets='1,2,3,4' Ink='#80aabbcc' Extent='Auto'/><c:Card Id='second' Title='two'/></StackPanel></Window>";
        TestControl.Initialized = TestControl.Destroyed = TestControl.Mounted = 0;
        using (var app = new App(Renderer.Software, headless: true)) {
            app.RegisterComponent<TestControl>("urn:tests", "Card", [new("Title", typeof(string), "default"), new("Model", typeof(PropertyModel)), new("Action", typeof(Command)), new("Offset", typeof(Point), new Point(1, 2)), new("Insets", typeof(Thickness)), new("Ink", typeof(Color)), new("Extent", typeof(Length))], events: ["Changed"]);
            app.RegisterFleckResources([new KeyValuePair<string, string>("card.fleck", resource)]);
            Check(app.ValidateFleckWithComponents(page) is null);
            Check(TestControl.Initialized == 0); // Validation never runs application code.
            app.LoadFleck(page);
            var first = app.FindComponent("first"); var second = app.FindComponent("second");
            Check((double)first.FindNode("label").GetProperty(PropertyTarget.FontSize)! == 26);
            Check((long)first.FindNode("label").GetProperty(PropertyTarget.FontWeight)! == 700);
            first.Node.SetProperty(PropertyTarget.FontSize, 32.0);
            Check((double)first.FindNode("label").GetProperty(PropertyTarget.FontSize)! == 32);
            first.Node.ClearProperty(PropertyTarget.FontSize);
            Check((double)first.FindNode("label").GetProperty(PropertyTarget.FontSize)! == 14);
            Check(first.Get<Point>("Offset") == new Point(2.5, -4));
            Check(first.Get<Thickness>("Insets") == new Thickness(1, 2, 3, 4));
            Check(first.Get<Color>("Ink") == new Color(0x80aabbcc));
            Check(first.Get<Length>("Extent") == Length.Auto);
            var offset = new Point(7, 9);
            using var typedModel = app.CreatePropertyModel().Add("Offset", () => offset, value => offset = value);
            first.Bind("Offset", typedModel, "Offset", PropertyBindingMode.TwoWay);
            Check(first.Get<Point>("Offset") == offset);
            first.Set("Offset", new Point(10, 11));
            Check(offset == new Point(10, 11));
            offset = new Point(-2, 6); typedModel.NotifyPropertyChanged("Offset");
            Check(first.Get<Point>("Offset") == offset);
            try { first.Set("Offset", new Point(double.NaN, 0)); throw new InvalidOperationException("NaN accepted"); }
            catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument); }
            Check(first.Get<Point>("Offset") == offset);
            first.SetLayer("Offset", ValuePrecedence.Animation, new Point(100, 200));
            offset = new Point(3, 4); typedModel.NotifyPropertyChanged("Offset");
            Check(first.Get<Point>("Offset") == new Point(100, 200) && first.GetBaseValue<Point>("Offset") == offset);
            Check(first.GetValueSource("Offset") == PropertyValueSource.Animation);
            first.ClearLayer("Offset", ValuePrecedence.Animation);
            Check(first.Get<Point>("Offset") == offset && first.GetValueSource("Offset") == PropertyValueSource.Binding);
            first.ClearLayer("Offset");
            Check(first.Get<Point>("Offset") == new Point(1, 2) && first.GetValueSource("Offset") == PropertyValueSource.Default);
            Check(first.DescribeProperty("Offset").Effects.HasFlag(PropertyEffects.Animatable));
            using var subscription = first.Subscribe("Changed", (_, _) => { });
            Check(first.FindNode("label").Text == "one" && second.FindNode("label").Text == "two");
            using var reference = app.CreatePropertyModel();
            using var command = app.CreateCommand(_ => { });
            first.Set("Model", reference); first.Set("Action", command);
            Check(ReferenceEquals(first.Get<PropertyModel>("Model"), reference));
            Check(ReferenceEquals(first.Get<Command>("Action"), command));
            first.Set<PropertyModel?>("Model", null); first.Set<Command?>("Action", null);
            Check(first.Get<PropertyModel?>("Model") is null && first.Get<Command?>("Action") is null);
            first.FindNode("editor").Text = "draft";
            first.Set("Title", "edited");
            app.OpenWindow();
            for (int i = 0; i < 5; ++i) {
                app.RefreshFleck(page);
                Check(app.FindComponent("first").Get<string>("Title") == "edited");
                Check(app.FindComponent("first").FindNode("editor").Text == "draft");
                Check(app.FindComponent("second").FindNode("label").Text == "two");
            }
            Check(TestControl.Initialized == 12 && TestControl.Mounted == 12 && TestControl.Destroyed == 10);
            Check(app.ValidateFleckWithComponents(page.Replace("c:Card", "c:Missing")) is not null);
            Check(app.FindComponent("first").Get<string>("Title") == "edited");
            app.RequestClose();
        }
        Check(TestControl.Destroyed == TestControl.Initialized);
        using (var app = new App(Renderer.Software, headless: true)) {
            app.RegisterComponent<TestControl>("urn:tests", "Rating", [new("Extent", typeof(int), 120), new("Inset", typeof(Thickness), new Thickness(3,5,3,5))], template: "Default", parts: [new("PART_Value", NodeKind.Slider)]);
            const string template = "<ControlTemplate xmlns='urn:sunfleck:v1' xmlns:c='urn:tests' Class='Default' Namespace='urn:tests' TargetType='c:Rating'><Slider Id='PART_Value' Width='{TemplateBinding Extent}' Margin='{TemplateBinding Inset}' HorizontalAlignment='Right'/></ControlTemplate>";
            app.RegisterFleckResources([new("default.fleck", template), new("alternate.fleck", template.Replace("Class='Default'", "Class='Alternate'"))]);
            app.LoadFleck("<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:tests'><c:Rating Id='rating' Width='200' MinWidth='150' MaxWidth='240' Height='60' Margin='10,20'/></Window>");
            var rating = (Control)app.FindComponent("rating");
            var retired = rating.FindTemplatePart("PART_Value");
            Check(rating.Width == 200 && rating.MinWidth == 150 && rating.MaxWidth == 240 && rating.Margin == new Thickness(10,20,10,20));
            Check(retired.Width == 120 && retired.Margin == new Thickness(3,5,3,5) && retired.HorizontalAlignment == "Right");
            rating.Set("Extent", 130); rating.Set("Inset", new Thickness(8,8,8,8));
            Check(retired.Width == 130 && retired.Margin == new Thickness(8,8,8,8));
            rating.ApplyTemplate("urn:tests", "Alternate");
            bool rejected = false;
            try { _ = retired.DebugId; } catch (SunfleckException) { rejected = true; }
            Check(rejected && rating.FindTemplatePart("PART_Value").DebugId != 0);
            Check(rating.Width == 200 && rating.FindTemplatePart("PART_Value").Width == 130 && rating.FindTemplatePart("PART_Value").Margin == new Thickness(8,8,8,8));
        }
        var directory = Path.Combine(Path.GetTempPath(), "sunfleck-components-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            File.WriteAllText(Path.Combine(directory, "Card.fleck"), resource);
            File.WriteAllText(Path.Combine(directory, "Default.fleck"), "<ControlTemplate xmlns='urn:sunfleck:v1' xmlns:c='urn:tests' Class='Default' Namespace='urn:tests' TargetType='c:Rating'><Slider/></ControlTemplate>");
            File.WriteAllText(Path.Combine(directory, "types.fleckcomponents.json"), """
                {"version":1,"types":[{"namespace":"urn:tests","name":"Card","properties":[{"name":"Title","type":"string"},{"name":"Offset","type":"point"},{"name":"Insets","type":"thickness"},{"name":"Ink","type":"color"},{"name":"Extent","type":"length"}]},{"namespace":"urn:tests","name":"Rating","template":"Default","properties":[{"name":"Value","type":"int","mode":"TwoWay"}]}]}
                """);
            var catalog = new LanguageCatalog();
            catalog.LoadComponents(new Uri(Path.Combine(directory, "main.fleck")).AbsoluteUri);
            const string prefix = "<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:tests'>";
            void Completes(string suffix, string expected) {
                var text = prefix + suffix;
                Check(LanguageFeatures.Complete(catalog, new FleckDocument(text), text.Length).Any(item => item?["label"]?.GetValue<string>() == expected));
            }
            Completes("<c:Ca", "c:Card"); Completes("<c:Card Ti", "Title"); Completes("<c:Card DataContext='n", "null"); Completes("<c:Rating Template='c:D", "c:Default");
            Check(catalog.ValidateComponents(new FleckDocument(page)) is null);
            var scopedDocument = new FleckDocument(prefix + "<StackPanel><TextBlock xmlns:c='urn:unrelated'/><c:Card Title='value'/></StackPanel></Window>");
            var scopedTag = scopedDocument.Tags.First(tag => tag.Name == "c:Card");
            Check(catalog.Attribute(scopedDocument, scopedTag, "Title") is not null);
            Check(catalog.ValidateComponents(new FleckDocument(prefix + "<Card xmlns='urn:tests' Title='value'/></Window>")) is null);
            var document = new FleckDocument(prefix + "<c:Card/></Window>");
            Check(LanguageFeatures.Definition(catalog, document, prefix.Length + 3)?["uri"]?.GetValue<string>().EndsWith("Card.fleck", StringComparison.Ordinal) == true);
            File.WriteAllText(Path.Combine(directory, "Card.fleck"), resource.Replace("<TextBox Id='editor'/>", "<c:Missing xmlns:c='urn:tests'/>"));
            catalog.LoadComponents(new Uri(Path.Combine(directory, "main.fleck")).AbsoluteUri);
            var diagnostics = LanguageFeatures.Diagnostics(catalog, document, new Uri(Path.Combine(directory, "main.fleck")).AbsoluteUri);
            Check(diagnostics[0]?["relatedInformation"]?[0]?["location"]?["uri"]?.GetValue<string>().EndsWith("Card.fleck", StringComparison.Ordinal) == true);
        } finally { Directory.Delete(directory, recursive: true); }
    }
}
