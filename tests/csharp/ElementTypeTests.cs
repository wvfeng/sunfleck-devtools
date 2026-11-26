using Path = System.IO.Path;
using System.Text.Json.Nodes;
using Sunfleck;
using Sunfleck.Development;
using Sunfleck.Elements;

internal static class ElementTypeTests
{
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    internal static void Run()
    {
        var catalog = new LanguageCatalog();
        {
            var contextCatalog = new LanguageCatalog();
            contextCatalog.SetDynamic(JsonNode.Parse("[{\"name\":\"PageModel\",\"properties\":[{\"path\":\"Selected\",\"type\":\"model\",\"writable\":false},{\"path\":\"Caption\",\"type\":\"integer\",\"writable\":false}]}]")!.AsArray());
            var scoped = new FleckDocument("<Window xmlns='urn:sunfleck:v1' Model='PageModel'><StackPanel DataContext='{Binding Selected}'><TextBlock Text='{Binding Caption}'/><TextBlock Text='{Binding ChildOnly}'/></StackPanel></Window>");
            var issues = LanguageFeatures.Diagnostics(contextCatalog, scoped);
            Check(!issues.Any(d => d?["code"]?.GetValue<string>() is "path_missing" or "type_mismatch" or "unknown_attribute"),
                "Nested contexts must not be validated against unrelated page model metadata");
            Check(issues.Any(d => d?["code"]?.GetValue<string>() == "model_uncertain"), "Unknown nested model metadata is explicit");
            var pathDefinition = LanguageFeatures.Definition(contextCatalog, scoped, scoped.Text.IndexOf("ChildOnly", StringComparison.Ordinal) + 2);
            Check(pathDefinition is null, "Unknown nested binding never navigates to an unrelated page declaration");
            var propertyDefinition = LanguageFeatures.Definition(contextCatalog, scoped, scoped.Text.IndexOf("DataContext=", StringComparison.Ordinal) + 2);
            Check(propertyDefinition?["uri"]?.GetValue<string>().EndsWith("Elements.cs", StringComparison.Ordinal) == true,
                "Ordinary DataContext has generated property navigation");
        }
        foreach (var newline in new[] { "\n", "\r\n" }) {
            string xml = "<UserControl xmlns='urn:sunfleck:v1' Class='Example.UserCard' Namespace='urn:tests'>" + newline +
                "  <Card><!-- 中文😀 --><MissingBorder><StackPanel/></MissingBorder></Card>" + newline + "</UserControl>";
            var unknownDocument = new FleckDocument(xml);
            var unknownErrors = LanguageFeatures.Diagnostics(catalog, unknownDocument);
            var unknown = unknownErrors.Single(d => d?["code"]?.GetValue<string>() == "unknown_element")!;
            int start = unknownDocument.Offset(unknown["range"]!["start"]!);
            int end = unknownDocument.Offset(unknown["range"]!["end"]!);
            Check(xml[start..end] == "MissingBorder" && start == xml.IndexOf("<MissingBorder>", StringComparison.Ordinal) + 1, "Unknown tag diagnostic uses exact UTF-16 name span");
            Check(!unknownErrors.Any(d => d?["code"]?.GetValue<string>()?.StartsWith("fleck_", StringComparison.Ordinal) == true), "Unknown tag must not leave a misleading root diagnostic");
            var prefixed = new FleckDocument(xml.Replace("<MissingBorder>", "<x:MissingBorder xmlns:x='urn:missing'>").Replace("</MissingBorder>", "</x:MissingBorder>"));
            var prefixedError = LanguageFeatures.Diagnostics(catalog, prefixed).Single(d => d?["code"]?.GetValue<string>() == "unknown_element")!;
            Check(prefixed.Text[prefixed.Offset(prefixedError["range"]!["start"]!)..prefixed.Offset(prefixedError["range"]!["end"]!)] == "x:MissingBorder", "Unknown qualified name includes prefix");
        }
        foreach (var name in new[] { "Id", "IsEnabled", "Height", "Width", "MinWidth", "MaxWidth", "MinHeight", "MaxHeight", "Margin", "HorizontalAlignment", "VerticalAlignment", "Weight", "Dock" }) {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy;
            Check(typeof(TextBlock).GetField(name + "Property", flags)?.DeclaringType == typeof(VisualElement), "TextBlock inherits " + name);
            Check(typeof(Image).GetField(name + "Property", flags)?.DeclaringType == typeof(VisualElement), "Image inherits " + name);
            Check(ReferenceEquals(typeof(TextBlock).GetField(name + "Property", flags)?.GetValue(null), typeof(Image).GetField(name + "Property", flags)?.GetValue(null)), "Shared property descriptor identity");
        }
        Check(typeof(StackPanel).BaseType == typeof(LayoutElement), "Layout parent");
        Check(typeof(TextBlock).GetField("PaddingProperty") is null, "Non-layout nodes do not acquire layout properties");
        var nativeSchema = JsonNode.Parse(App.GetFleckSchema())!;
        var lines = File.ReadAllLines(BuiltInElementTypes.SourceFile);
        Check(BuiltInElementTypes.All.Count == nativeSchema["elements"]!.AsArray().Count + nativeSchema["documents"]!.AsArray().Count + nativeSchema["templateElements"]!["elements"]!.AsArray().Count, "Every native tag has a code type");
        foreach (var type in BuiltInElementTypes.All) {
            Check(typeof(FrameworkElement).IsAssignableFrom(type.Type), type.Name + " base type");
            Check(lines[type.Line - 1].Contains("class " + type.Type.Name), type.Name + " class location");
            var entries = type.Group switch {
                "Document" => nativeSchema["documents"]!.AsArray(),
                "Theme" => nativeSchema["templateElements"]!["elements"]!.AsArray(),
                _ => nativeSchema["elements"]!.AsArray()
            };
            var schema = entries.Single(e => e!["name"]!.GetValue<string>() == type.Name)!;
            Check(schema["attributes"]!.AsArray().Count == type.Properties.Length, type.Name + " property coverage");
            foreach (var property in type.Properties) {
                var attribute = schema["attributes"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == property.Name)!;
                Check(lines[property.Line - 1].Contains(property.Name.Replace(".", "") + "Property"), type.Name + "." + property.Name + " location");
                Check((attribute["binding"]?.GetValue<bool>() ?? false) == property.Bindable && attribute["type"]!.GetValue<string>() == property.Kind && (attribute["twoWay"]?.GetValue<bool>() ?? false) == property.TwoWay, "Native property metadata agrees with code");
            }
        }
        var builtIn = new FleckDocument("<Window xmlns='urn:sunfleck:v1' xmlns:ui='urn:sunfleck:v1'><ui:TextBox Text='hello'/></Window>");
        const string styledXml = "<Window xmlns='urn:sunfleck:v1'><Window.Resources><Resource Key='extent' Value='88'/><Style Key='card' TargetType='Border'><Setter Property='Width' Value='{StaticResource extent}'/><Setter Property='Opacity' Value='{DynamicResource Fade}'/></Style></Window.Resources><Border Id='card' Style='{StaticResource card}'/></Window>";
        var styledDocument = new FleckDocument(styledXml);
        Check(!LanguageFeatures.Diagnostics(catalog, styledDocument).Any(), "Style resource editor diagnostics");
        int reference = styledXml.LastIndexOf("card}", StringComparison.Ordinal);
        var keyLocation = LanguageFeatures.Definition(catalog, styledDocument, reference + 1, "file:///styles.fleck");
        Check(keyLocation?["range"]?["start"]?["character"]?.GetValue<int>() == styledXml.IndexOf("card'", StringComparison.Ordinal), "StaticResource definition");
        Check(LanguageFeatures.Complete(catalog, styledDocument, reference + 1).Any(item => item?["label"]?.GetValue<string>() == "card"), "Style key completion");
        using (var styledApp = new App(Renderer.Software, headless: true)) {
            styledApp.LoadFleck(styledXml);
            var card = styledApp.FindFleckNode("card");
            styledApp.SetThemeResource("Fade", .25);
            styledApp.SetResource("Fade", .75);
            Check((double)card.GetProperty(PropertyTarget.Opacity)! == .75, "Managed dynamic application resource");
            card.SetResource("Fade", .5);
            Check((double)card.GetProperty(PropertyTarget.Opacity)! == .5, "Managed element resource");
            card.ClearResource("Fade");
            styledApp.ClearResource("Fade");
            Check((double)card.GetProperty(PropertyTarget.Opacity)! == .25, "Managed dynamic theme fallback");
            card.SetResourceReference(PropertyTarget.Opacity,"Missing");
            Check((double)card.GetProperty(PropertyTarget.Opacity)! == .25, "Missing local resource reveals style");
            styledApp.SetResource("Missing", .8);
            Check((double)card.GetProperty(PropertyTarget.Opacity)! == .8, "Programmatic reference resolves a later resource");
            card.ClearProperty(PropertyTarget.Opacity);
            Check((double)card.GetProperty(PropertyTarget.Opacity)! == .25, "Clear programmatic resource restores style");
            Check(card.Width == 88 && card.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Style,
                  "Managed Fleck style applies through the style layer");
        }
        var removedWidth = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><Border PreferredWidth='100'/></Window>");
        Check(LanguageFeatures.Diagnostics(catalog, removedWidth).Any(), "Removed PreferredWidth produces an editor diagnostic");
        foreach (var metric in new[] { "Spacing='0.125' Padding='1.75'", "Spacing='Theme' Padding='0'" }) {
            var page = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><StackPanel " + metric + "/></Window>");
            Check(!LanguageFeatures.Diagnostics(catalog, page).Any(), "Editor accepts fractional metrics and Theme");
        }
        foreach (var metric in new[] { "Spacing='-0.5'", "Spacing='-1'", "Padding='Infinity'", "Padding='NaN'", "Padding='64.5'" }) {
            var page = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><StackPanel " + metric + "/></Window>");
            Check(LanguageFeatures.Diagnostics(catalog, page).Any(d => d?["code"]?.GetValue<string>() == "invalid_property_value"), "Editor rejects invalid metrics");
        }
        Check(!BuiltInElementTypes.All.SelectMany(t => t.Properties).Any(p => p.Name == "PreferredWidth"), "Removed property is absent from SDK completions");
        foreach (var text in new[] { "Window", "ui:TextBox", "Text=" }) {
            var definition = LanguageFeatures.Definition(catalog, builtIn, builtIn.Text.IndexOf(text, StringComparison.Ordinal) + 1);
            Check(definition?["uri"]?.GetValue<string>().EndsWith("Elements.cs", StringComparison.Ordinal) == true, "Built-in F12: " + text);
        }
        using (var app = new App(Renderer.Software, headless: true)) {
            var box = new TextBox(app) { Text = "typed" };
            Check(box.Node.Text == "typed", "Typed native Text");
            box.IsEnabled = false;
            Check(!box.Node.IsEnabled, "Inherited IsEnabled accessor");
            var slider = new Slider(app) { Value = 25 };
            Check(slider.Node.Value == 25 && slider.Value == 25, "Typed native Value");
            int score = 40;
            using var model = app.CreatePropertyModel();
            model.Add("Score", () => score, value => score = value);
            slider.Bind(Slider.ValueProperty, model, "Score");
            Check(slider.Value == 40, "Typed property binding");
            var fontNode = new TextBlock(app) { FontFamily = "Consolas", FontSize = 23.5, FontWeight = FontWeight.Bold, FontStyle = FontStyle.Italic, FontStretch = FontStretch.Condensed };
            Check(fontNode.FontFamily == "Consolas" && fontNode.FontSize == 23.5 && fontNode.FontWeight == FontWeight.Bold && fontNode.FontStyle == FontStyle.Italic && fontNode.FontStretch == FontStretch.Condensed, "Typed font accessors");
            double fontSize = 32;
            model.Add("FontSize", () => fontSize);
            fontNode.Bind(TextBlock.FontSizeProperty, model, "FontSize");
            Check(fontNode.FontSize == 32, "Typed font binding");
            fontNode.Node.ClearProperty(PropertyTarget.FontSize);
            Check(fontNode.FontSize == 14, "Font default restored");
            var fontXml = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><TextBlock FontFamily='Consolas' FontSize='23.5' FontWeight='650' FontStyle='Italic' FontStretch='Condensed'/></Window>");
            Check(!LanguageFeatures.Diagnostics(catalog, fontXml).Any(), "Font literals and numeric weights accepted by editor");
            Check(LanguageFeatures.Definition(catalog, fontXml, fontXml.Text.IndexOf("FontSize=", StringComparison.Ordinal)+2) is not null, "Font property F12");
            var appearance = new TextBlock(app) { Foreground = new Color(0x80335577), TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.WordEllipsis, LineHeight = 32,
                TextDecorations = TextDecorations.Underline, FlowDirection = FlowDirection.RightToLeft };
            Check(appearance.Foreground.Argb == 0x80335577 && appearance.TextAlignment == TextAlignment.Center &&
                appearance.LineHeight == 32 && appearance.TextDecorations == TextDecorations.Underline &&
                appearance.FlowDirection == FlowDirection.RightToLeft, "Typed text appearance accessors");
            if (OperatingSystem.IsWindows()) {
                ulong fontId = app.RegisterFont(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "font-fixture.ttf")));
                Check(app.FontFamilies(fontId).Contains("Noto Sans Mono"), "Managed font registration and family discovery");
                appearance.FontFamily = "Noto Sans Mono";
                app.UnregisterFont(fontId);
            }
            var appearanceXml = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><TextBlock Foreground='#335577' TextAlignment='Center' TextWrapping='NoWrap' TextTrimming='WordEllipsis' LineHeight='32' TextDecorations='Underline' FlowDirection='RightToLeft'/></Window>");
            Check(!LanguageFeatures.Diagnostics(catalog, appearanceXml).Any(), "Text appearance literals recognized by editor");
            foreach (var name in new[] { "Foreground", "TextAlignment", "TextWrapping", "TextTrimming", "LineHeight", "TextDecorations", "FlowDirection" }) {
                Check(LanguageFeatures.Definition(catalog, appearanceXml, appearanceXml.Text.IndexOf(name+"=", StringComparison.Ordinal)+2) is not null, "Text appearance F12: "+name);
            }
            var numericAppearance = new FleckDocument(appearanceXml.Text.Replace("TextAlignment='Center'", "TextAlignment='1'").Replace("TextDecorations='Underline'", "TextDecorations='3'"));
            Check(!LanguageFeatures.Diagnostics(catalog, numericAppearance).Any(), "Numeric text enum literals match native parser");
            var invalidAppearance = new FleckDocument(appearanceXml.Text.Replace("TextAlignment='Center'", "TextAlignment='9'"));
            Check(LanguageFeatures.Diagnostics(catalog, invalidAppearance).Any(d => d?["code"]?.GetValue<string>() == "invalid_property_value"), "Invalid text enum diagnostic");
            var stateNode = new TextBlock(app) { Visibility = Visibility.Hidden, IsHitTestVisible = false };
            var paragraph = new TextBlock(app);
            var inlineSpan = new Sunfleck.Elements.Span(app) { FontWeight = FontWeight.Bold };
            var inlineRun = new Run(app) { Text = "Managed inline", Foreground = new Color(0xff224466) };
            inlineSpan.Node.Append(inlineRun.Node);
            paragraph.Node.Append(inlineSpan.Node);
            Check(paragraph.Node.Text == "Managed inline" && inlineRun.FontWeight == FontWeight.Bold, "Managed Run/Span tree and inheritance");
            var inlineXml = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><TextBlock><Span FontStyle='Italic'><Run Text='Hello'/></Span></TextBlock></Window>");
            Check(!LanguageFeatures.Diagnostics(catalog, inlineXml).Any(), "Inline syntax diagnostics");
            Check(LanguageFeatures.Definition(catalog, inlineXml, inlineXml.Text.IndexOf("<Run", StringComparison.Ordinal)+2) is not null, "Run type F12");
            Check(stateNode.Visibility == Visibility.Hidden && !stateNode.IsHitTestVisible,"Typed visual state properties");
            var visibility = Visibility.Collapsed;
            model.Add("Mode", () => visibility);
            stateNode.Bind(TextBlock.VisibilityProperty, model, "Mode");
            Check(stateNode.Visibility == Visibility.Collapsed,"Typed enum binding");
            stateNode.Node.ClearProperty(PropertyTarget.Visibility);
            Check(stateNode.Visibility == Visibility.Visible,"Visibility default restored");
            Check(BuiltInPropertyDescriptor.Describe(NodeKind.Border,PropertyTarget.BorderThickness).DefaultValue is Thickness t && t.Left == 0,
                "Typed descriptor geometry default");
            var border = new Border(app) { Background = new Color(0x80443322), BorderBrush = new Color(0xff112233),
                BorderThickness = new Thickness(1, 2, 3, 4), CornerRadius = new CornerRadius(5, 6, 7, 8), Padding = 3 };
            Check(border.Background.Argb == 0x80443322 && border.BorderThickness.Right == 3 && border.CornerRadius.BottomLeft == 8,
                "Typed Border geometry/color accessors");
            var borderXml = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><Border Background='#112233' BorderThickness='1,2,3,4' CornerRadius='6'><TextBlock/></Border></Window>");
            Check(!LanguageFeatures.Diagnostics(catalog, borderXml).Any(), "Border literals recognized by editor");
            Check(LanguageFeatures.Definition(catalog, borderXml, borderXml.Text.IndexOf("BorderThickness=", StringComparison.Ordinal)+2) is not null,
                "Border property F12");
            var label = new TextBlock(app) { Height = 10 };
            Check(label.Height == 10 && label.Node.Height == 10, "Inherited native Height accessor");
            label.Bind(TextBlock.HeightProperty, model, "Score");
            Check(label.Height == 40, "Height binding");
            score = 24; model.NotifyPropertyChanged("Score");
            Check(label.Height == 24, "Height binding notification");
            label.Width = 120; label.MinWidth = 50; label.MaxWidth = 150;
            label.MinHeight = 10; label.MaxHeight = 30;
            label.Margin = new Thickness(2,3,4,5); label.HorizontalAlignment = "Right"; label.VerticalAlignment = "Center";
            Check(label.Width == 120 && label.Node.MinWidth == 50 && label.Node.MaxWidth == 150 && label.MinHeight == 10 && label.MaxHeight == 30, "Typed layout size accessors");
            Check(label.Margin == new Thickness(2,3,4,5) && label.Node.HorizontalAlignment == "Right" && label.VerticalAlignment == "Center", "Typed margin and alignment accessors");
            label.Bind(TextBlock.WidthProperty, model, "Score");
            Check(label.Width == 24, "Typed Width binding stores requested value independent of effective minimum");
            var inset = new Thickness(8,12,8,12); string align = "Left";
            model.Add("Inset", () => inset); model.Add("Align", () => align);
            label.Bind(TextBlock.MarginProperty, model, "Inset"); label.Bind(TextBlock.HorizontalAlignmentProperty, model, "Align");
            Check(label.Margin == inset && label.HorizontalAlignment == align, "Typed Thickness and string alignment bindings");
            inset = new Thickness(4,4,4,4); align = "Center"; model.NotifyPropertyChanged();
            Check(label.Margin == inset && label.HorizontalAlignment == align, "Layout binding updates");
            inset = new Thickness(.25,-.5,.75,1.25); model.NotifyPropertyChanged("Inset");
            Check(label.Margin == inset,"Typed Margin binding retains fractional and negative values");
            try { label.Margin = new Thickness(double.NaN,0,0,0); throw new InvalidOperationException("NaN Margin accepted"); }
            catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument,"Invalid fractional Margin rejected"); }
            Check(label.Margin == inset,"Failed Margin assignment retains existing binding value");
            var fractional = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><Border Margin='0.25,-0.5,0.75,1.25'/></Window>");
            Check(!LanguageFeatures.Diagnostics(catalog,fractional).Any(d => d?["code"]?.GetValue<string>() == "invalid_property_value"),"Editor accepts fractional Thickness margins");
            label.Node.ClearBinding(PropertyTarget.Width);
            label.Width = 120.25; label.Height = new Length(20.5); label.MaxWidth = Length.Infinity;
            Check(label.Width == new Length(120.25) && label.Height == 20.5 && label.MaxWidth == Length.Infinity,"Typed fractional sizes and Infinity");
            label.Height = Length.Auto;
            Check(label.Height == Length.Auto,"Managed explicit Auto");
            var lengthPage = new FleckDocument("<Window xmlns='urn:sunfleck:v1'><Border Width='120.25' Height='Auto' MaxWidth='Infinity' MinHeight='0.5'/></Window>");
            Check(!LanguageFeatures.Diagnostics(catalog,lengthPage).Any(d => d?["code"]?.GetValue<string>() == "invalid_property_value"),"Editor accepts fractional lengths and explicit units");
        }
        var directory = Path.Combine(Path.GetTempPath(), "sunfleck-source-types-中文 🌱 %20-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            // Switch languages in one catalog, as a multi-root editor does.
            // Every tag/property in each generated map must point to its own declaration.
            var repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(BuiltInElementTypes.SourceFile)!, "../.."));
            var sdk = Path.Combine(directory, "sdk");
            Directory.CreateDirectory(sdk);
            var navigationCard = Path.Combine(directory, "NavigationCard.fleck");
            File.WriteAllText(navigationCard, "<UserControl xmlns='urn:sunfleck:v1' Namespace='urn:navigation' Class='Navigation.Card'><TextBlock/></UserControl>");
            foreach (var language in new[] { "c", "cpp", "csharp", "c" }) {
                var manifestFile = Path.Combine(repository, "bindings", "types", language + ".json");
                var manifest = JsonNode.Parse(File.ReadAllText(manifestFile))!;
                string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestFile)!, manifest["source"]!.GetValue<string>()));
                var sourceLines = File.ReadAllLines(source);
                foreach (var type in manifest["types"]!.AsArray()) {
                    string identifier = type!["className"]!.GetValue<string>().Split(new[] { '.', ':' }, StringSplitOptions.RemoveEmptyEntries).Last();
                    Check(sourceLines[type["line"]!.GetValue<int>() - 1].Contains(identifier), "Native/public type source map");
                    foreach (var property in type["properties"]!.AsArray()) {
                        Check(sourceLines[property!["line"]!.GetValue<int>() - 1].Contains(property["name"]!.GetValue<string>().Replace(".", "") + "Property"), "Native/public property source map");
                        if (type["group"]!.GetValue<string>() == "" && property["name"]!.GetValue<string>() == "Id") {
                            Check(property["declaringType"]!.GetValue<string>().EndsWith("VisualElement", StringComparison.Ordinal), "Common attribute navigates to visual base");
                        }
                        if (type["group"]!.GetValue<string>() == "" && property["name"]!.GetValue<string>() == "Padding") {
                            Check(property["declaringType"]!.GetValue<string>().EndsWith("LayoutElement", StringComparison.Ordinal), "Layout attribute navigates to layout base");
                        }
                    }
                }
                manifest["source"] = Path.GetFileName(source);
                File.Copy(source, Path.Combine(sdk, Path.GetFileName(source)), true);
                foreach (var attribute in manifest["componentAttributes"]!.AsArray()) {
                    string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestFile)!, attribute!["source"]!.GetValue<string>()));
                    Check(File.ReadAllLines(file)[attribute["line"]!.GetValue<int>() - 1].Contains(attribute["name"]!.GetValue<string>() + "Property"), "Component base declaration source map");
                    File.Copy(file, Path.Combine(sdk, Path.GetFileName(file)), true);
                    attribute["source"] = Path.GetFileName(file);
                }
                File.WriteAllText(Path.Combine(sdk, "fleck.types.json"), manifest.ToJsonString());
                File.WriteAllText(Path.Combine(directory, "fleck.project.json"), new JsonObject { ["language"] = language, ["sdk"] = "sdk" }.ToJsonString());
                catalog.LoadComponents(RuntimeModels.SourceUri(Path.Combine(directory, "main.fleck")));
                foreach (var text in new[] { "Window", "ui:TextBox", "Text=" }) {
                    var definition = LanguageFeatures.Definition(catalog, builtIn, builtIn.Text.IndexOf(text, StringComparison.Ordinal) + 1);
                    Check(definition?["uri"]?.GetValue<string>() == RuntimeModels.SourceUri(Path.Combine(sdk, Path.GetFileName(source))), "Language-specific SDK definition: " + language + " " + text);
                }
                var custom = new FleckDocument("<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:navigation'><c:Card Id='card' Anchor='anchor' DataContext='{Binding User}' Template='c:Default' IsEnabled='true' Height='24' Width='100' MinWidth='20' MaxWidth='200' MinHeight='10' MaxHeight='100' Margin='4' HorizontalAlignment='Center' VerticalAlignment='Top' Weight='1' Dock='Left'/></Window>");
                foreach (var name in new[] { "Id", "DataContext", "Template", "IsEnabled", "Height", "Width", "MinWidth", "MaxWidth", "MinHeight", "MaxHeight", "Margin", "HorizontalAlignment", "VerticalAlignment", "Weight", "Dock" }) {
                    var definition = LanguageFeatures.Definition(catalog, custom, custom.Text.IndexOf(name + "=", StringComparison.Ordinal) + 1);
                    Check(definition is not null, "Custom component common attribute F12: " + language + " " + name);
                    string file = RuntimeModels.SourcePath(definition!["uri"]!.GetValue<string>())!;
                    int line = definition["range"]!["start"]!["line"]!.GetValue<int>();
                    Check(file.StartsWith(sdk, StringComparison.OrdinalIgnoreCase) && File.ReadAllLines(file)[line].Contains(name + "Property"), "Common property stays in selected language SDK");
                }
                var component = custom.Tags.Single(t => t.Name == "c:Card");
                Check(catalog.Attribute(custom, component, "DataContext")?["binding"]?.GetValue<bool>() == true, "DataContext remains bindable");
                Check(catalog.Attribute(custom, component, "Template")?["binding"]?.GetValue<bool>() == false, "Template is not a model binding target");
                Check(catalog.Attribute(custom, component, "Width")?["type"]?.GetValue<string>() == "length", "Host property type matches native schema");
            }
            File.WriteAllText(Path.Combine(directory, "fleck.project.json"), "{\"language\":\"cpp\",\"sdk\":\"sdk\"}");
            catalog.LoadComponents(RuntimeModels.SourceUri(Path.Combine(directory, "main.fleck")));
            Check(LanguageFeatures.Definition(catalog, builtIn, builtIn.Text.IndexOf("ui:TextBox", StringComparison.Ordinal) + 1) is null, "Mismatched SDK must not jump into another language");
            catalog.Errors.Clear();
            File.Delete(Path.Combine(directory, "fleck.project.json"));
            Directory.Delete(sdk, true);
            File.Delete(navigationCard);
            File.WriteAllText(Path.Combine(directory, "Test.csproj"), "<Project Sdk='Microsoft.NET.Sdk'/>");
            File.WriteAllText(Path.Combine(directory, "Card.fleck"), "<UserControl xmlns='urn:sunfleck:v1' Namespace='urn:tests' Class='Tests.Card'><TextBlock/></UserControl>");
            File.WriteAllText(Path.Combine(directory, "Empty.fleck"), "<UserControl xmlns='urn:sunfleck:v1' Namespace='urn:tests' Class='Tests.Empty'><TextBlock/></UserControl>");
            File.WriteAllText(Path.Combine(directory, "Card.cs"), """
                using Sunfleck;
                namespace Tests;
                public class Card : UserControl {
                    public Card() => throw new System.Exception("Must not construct during discovery");
                    [FleckProperty(Mode = PropertyBindingMode.TwoWay)]
                    public int Score { get => throw new System.Exception(); set => throw new System.Exception(); }
                    [FleckProperty(Default = "2.5,4", Effects = PropertyEffects.Bindable | PropertyEffects.Animatable | PropertyEffects.AffectsMeasure)]
                    public Point Offset { get => Get<Point>("Offset"); set => Set("Offset", value); }
                }
                """);
            string uri = RuntimeModels.SourceUri(Path.Combine(directory, "Main.fleck"));
            catalog.LoadComponents(uri);
            Check(catalog.Errors.Count == 0, string.Join("\n", catalog.Errors));
            var document = new FleckDocument("<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:tests'><c:Card Score='3'/><c:Empty/></Window>");
            var card = document.Tags.Single(t => t.Name == "c:Card");
            Check(catalog.Attribute(document, card, "Score")?["defaultMode"]?.GetValue<string>() == "TwoWay", "Code-only property metadata before build");
            Check(catalog.Attribute(document, card, "Offset")?["type"]?.GetValue<string>() == "point", "Typed component metadata before build");
            Check(catalog.Attribute(document, card, "Offset")?["animatable"]?.GetValue<bool>() == true &&
                catalog.Attribute(document, card, "Offset")?["twoWay"]?.GetValue<bool>() == false &&
                catalog.Attribute(document, card, "Offset")?["propertyFlags"]?.GetValue<int>() == 69, "Declared effects flow through generator and editor metadata");
            foreach (var text in new[] { "c:Card", "Score=" }) {
                var definition = LanguageFeatures.Definition(catalog, document, document.Text.IndexOf(text, StringComparison.Ordinal) + 1);
                Check(definition?["uri"]?.GetValue<string>().EndsWith("Card.cs", StringComparison.Ordinal) == true, "Code-only F12: " + text);
            }
            // Match vscode.Uri.file(...).toString(), not only System.Uri's
            // unescaped drive-colon representation used by earlier tests.
            if (OperatingSystem.IsWindows()) {
                string editorUri = uri[..9] + "%3A" + uri[10..];
                Check(editorUri.Contains("%2520", StringComparison.Ordinal), "Literal percent is encoded exactly once");
                catalog.LoadComponents(editorUri);
                Check(LanguageFeatures.Definition(catalog, document, document.Text.IndexOf("Score=", StringComparison.Ordinal) + 1)?["uri"]?.GetValue<string>().EndsWith("Card.cs", StringComparison.Ordinal) == true, "VS Code encoded drive component discovery");
            }
            var generated = LanguageFeatures.Definition(catalog, document, document.Text.IndexOf("c:Empty", StringComparison.Ordinal) + 1);
            Check(generated is not null && File.ReadAllText(new Uri(generated["uri"]!.GetValue<string>()).LocalPath).Contains("class Empty : global::Sunfleck.UserControl"), "Generated empty type F12");
            var classDocument = new FleckDocument(File.ReadAllText(Path.Combine(directory, "Card.fleck")));
            Check(LanguageFeatures.Definition(catalog, classDocument, classDocument.Text.IndexOf("Tests.Card", StringComparison.Ordinal) + 1)?["uri"]?.GetValue<string>().EndsWith("Card.cs", StringComparison.Ordinal) == true, "Class F12");
            var template = new FleckDocument("<ControlTemplate xmlns='urn:sunfleck:v1' xmlns:c='urn:tests' Class='Tests.Default' Namespace='urn:tests' TargetType='c:Card'><TextBlock Text='{TemplateBinding Score}'/></ControlTemplate>");
            var invalidAttributes = new FleckDocument(template.Text.Replace("<TextBlock", "<TextBlock Heigth='10' Height='10000001'"));
            var errors = LanguageFeatures.Diagnostics(catalog, invalidAttributes, uri);
            Check(errors.Any(d => d?["code"]?.GetValue<string>() == "unknown_attribute") && errors.Any(d => d?["code"]?.GetValue<string>() == "invalid_property_value"), "All local property errors remain visible despite dependency errors");
            var invalidLayout = new FleckDocument(template.Text.Replace("<TextBlock", "<TextBlock Margin='1,2,3' HorizontalAlignment='Bottom'"));
            Check(LanguageFeatures.Diagnostics(catalog, invalidLayout, uri).Count(d => d?["code"]?.GetValue<string>() == "invalid_property_value") >= 2, "Margin and alignment diagnostics are independent");
            Check(LanguageFeatures.Definition(catalog, template, template.Text.IndexOf("Score", StringComparison.Ordinal) + 1)?["uri"]?.GetValue<string>().EndsWith("Card.cs", StringComparison.Ordinal) == true, "TemplateBinding F12");
            Check(LanguageFeatures.Complete(catalog, template, template.Text.IndexOf("Score", StringComparison.Ordinal) + 2).Any(i => i?["label"]?.GetValue<string>() == "Score"), "TemplateBinding completion");
            var self = new FleckDocument("<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:tests'><c:Card Score='{Binding Score, RelativeSource=Self}'/></Window>");
            Check(LanguageFeatures.Definition(catalog, self, self.Text.IndexOf("Binding Score", StringComparison.Ordinal) + 9)?["uri"]?.GetValue<string>().EndsWith("Card.cs", StringComparison.Ordinal) == true, "Self binding F12");
            // Reanalyze an edited source without rebuilding or retaining a stale property.
            var code = Path.Combine(directory, "Card.cs");
            File.WriteAllText(code, File.ReadAllText(code).Replace("Score", "NewScore"));
            catalog.LoadComponents(uri);
            Check(catalog.Attribute(document, card, "NewScore") is not null && catalog.Attribute(document, card, "Score") is null, "Source refresh");
            File.WriteAllText(code, File.ReadAllText(code).Replace(": UserControl", ""));
            catalog.LoadComponents(uri);
            Check(LanguageFeatures.Diagnostics(catalog, document, uri).Any(d => d?["code"]?.GetValue<string>() == "FLK001" && d?["relatedInformation"] is JsonArray), "Invalid component base diagnostic with source location");
        } finally { Directory.Delete(directory, true); }
    }
}

