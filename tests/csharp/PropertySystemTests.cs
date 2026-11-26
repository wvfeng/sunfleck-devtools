using System.Text.Json;
using Sunfleck;
using Sunfleck.Elements;

internal static class PropertySystemTests
{
    public sealed class MetadataControl : Sunfleck.Control { }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    public static void Run()
    {
        Check(PropertyValues.Parse<Thickness>("1,2") == new Thickness(1, 2, 1, 2), "Shared typed literal parser");
        Check(PropertyValues.Parse<Color>("#abcdef") == new Color(0xffabcdef), "Opaque RGB literal");
        Check(PropertyValues.Parse<Length>("Infinity") == Length.Infinity, "Explicit infinite length");
        try { _ = PropertyValues.Parse<Point>("NaN,2"); throw new InvalidOperationException("Invalid geometry accepted"); }
        catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument, "Invalid geometry rejected"); }
        var descriptor = BuiltInPropertyDescriptor.Describe(NodeKind.TextBox, PropertyTarget.Text);
        Check(descriptor.Name == "Text" && descriptor.Type == PropertyValueType.String &&
            descriptor.Effects.HasFlag(PropertyEffects.TwoWay), "Native metadata layout and flags");
        using var app = new App(Renderer.Software, headless: true);
        var contextRoot = app.CreateNode(NodeKind.StackPanel);
        var contextChild = app.CreateNode(NodeKind.TextBlock);
        contextRoot.Append(contextChild);
        contextChild.BindContext(PropertyTarget.Text, "Caption", PropertyBindingMode.OneWay);
        string caption = "inherited";
        using var contextModel = app.CreatePropertyModel().Add("Caption", () => caption);
        contextRoot.DataContext = contextModel;
        Check(contextChild.Text == caption && contextChild.DataContext == contextModel &&
            contextChild.DataContextSource == PropertyValueSource.Inherited, "Managed inherited DataContext");
        contextChild.DataContext = null;
        caption = "latest"; contextModel.NotifyPropertyChanged("Caption");
        Check(contextChild.Text == "" && contextChild.DataContextSource == PropertyValueSource.Local, "Managed explicit null blocks inheritance");
        contextChild.ClearDataContext();
        Check(contextChild.Text == caption, "Managed clear restores inherited binding");
        contextModel.Dispose();
        Check(contextChild.Text == "" && contextChild.DataContext is null &&
            contextRoot.DataContextSource == PropertyValueSource.Local, "Disposed model clears bound values and preserves null boundary");
        using var replacementContext = app.CreatePropertyModel().Add("Caption", () => "replacement");
        contextRoot.DataContext = replacementContext;
        Check(contextChild.Text == "replacement", "Disposed context declaration can reactivate");
        Check(ReferenceEquals(contextRoot.GetProperty(PropertyTarget.DataContext), replacementContext), "Generic model value getter");
        contextChild.SetProperty(PropertyTarget.DataContext, null);
        Check(contextChild.Text == "", "Generic nullable model setter");
        contextChild.ClearProperty(PropertyTarget.DataContext);
        Check(contextChild.Text == "replacement" &&
            BuiltInPropertyDescriptor.Describe(NodeKind.TextBlock, PropertyTarget.DataContext).Effects.HasFlag(PropertyEffects.Inherits),
            "Generic clear and inheritance metadata");
        using (var nestedApp = new App(Renderer.Software, headless: true)) {
            using var firstContext = nestedApp.CreatePropertyModel().Add("Caption", () => "first");
            using var secondContext = nestedApp.CreatePropertyModel().Add("Caption", () => "second");
            PropertyModel? selectedContext = firstContext;
            using var outerContext = nestedApp.CreatePropertyModel().Add("Selected", () => selectedContext);
            nestedApp.LoadFleck("<Window xmlns='urn:sunfleck:v1'><StackPanel><TextBlock Id='selected' Text='{Binding Caption}' DataContext='{Binding Selected}'/></StackPanel></Window>", outerContext);
            var selectedNode = new TextBlock(nestedApp.FindFleckNode("selected"));
            Check(selectedNode.Text == "first" && ReferenceEquals(selectedNode.DataContext, firstContext), "Managed Fleck context binding and typed accessor");
            selectedContext = secondContext; outerContext.NotifyPropertyChanged("Selected");
            Check(selectedNode.Text == "second" && selectedNode.GetValueSource(TextBlock.DataContextProperty) == PropertyValueSource.Binding,
                "Managed context binding updates and reports source");
            selectedContext = null; outerContext.NotifyPropertyChanged("Selected");
            Check(selectedNode.Text == "", "Managed nullable model binding creates a null boundary");
            selectedContext = firstContext; outerContext.NotifyPropertyChanged("Selected");
            outerContext.Dispose();
            Check(selectedNode.Text == "" && selectedNode.DataContext is null, "Destroying context source clears derived descendants");
        }
        var node = app.CreateNode(NodeKind.TextBlock);
        var metricsPanel = new StackPanel(app.CreateNode(NodeKind.StackPanel));
        metricsPanel.Spacing = 3.25;
        metricsPanel.Padding = 5.5;
        metricsPanel.Width = 100;
        metricsPanel.Weight = 2;
        Check(metricsPanel.Spacing == 3.25 && metricsPanel.Padding == 5.5 && metricsPanel.Width == 100 && metricsPanel.Weight == 2, "Generated typed metric accessors");
        double gap = 8.25;
        using var metricModel = app.CreatePropertyModel().Add("Gap", () => gap);
        metricsPanel.Bind(StackPanel.SpacingProperty, metricModel, "Gap");
        gap = 9.75; metricModel.NotifyPropertyChanged("Gap");
        Check(metricsPanel.Spacing == 9.75, "Generated metric binding");
        metricsPanel.ClearValue(StackPanel.SpacingProperty);
        Check(metricsPanel.Spacing == -1, "Generated metric clear");
        int integerGap = 2;
        using var integerMetrics = app.CreatePropertyModel().Add("Gap", () => integerGap);
        metricsPanel.Bind(StackPanel.SpacingProperty, integerMetrics, "Gap");
        integerGap = 6; integerMetrics.NotifyPropertyChanged("Gap");
        Check(metricsPanel.Spacing == 6.0, "Integer sources normalize to fractional metric properties");
        node.SetProperty(PropertyTarget.Text, "中文 🌱");
        Check((string)node.GetProperty(PropertyTarget.Text)! == "中文 🌱", "Generic string copy");
        node.SetProperty(PropertyTarget.Width, 123);
        Check(node.Width == 123 && node.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Local, "Managed generic write");
        node.ClearProperty(PropertyTarget.Width);
        Check(node.Width == Length.Auto && node.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Default, "Managed clear");
        int width = 95;
        using var model = app.CreatePropertyModel().Add("Width", () => width);
        model.Bind(node, PropertyTarget.Width, "Width", PropertyBindingMode.OneWay);
        Check(node.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Binding, "Managed binding origin");
        node.StartAnimation(PropertyTarget.Width, new Timeline(
            [new KeyFrame(0, new Length(10)), new KeyFrame(1, new Length(50))], 1000, HoldEnd: true));
        node.ControlAnimation(PropertyTarget.Width, AnimationAction.Pause);
        node.ControlAnimation(PropertyTarget.Width, AnimationAction.Seek, 500);
        Check(node.Width == 30 && width == 95, "Timeline uses an independent presentation value");
        width = 125;
        model.NotifyPropertyChanged("Width");
        node.ControlAnimation(PropertyTarget.Width, AnimationAction.Stop);
        Check(node.Width == 125, "Stopping reveals the latest binding value");
        width = 95;
        model.NotifyPropertyChanged("Width");
        try { node.SetProperty(PropertyTarget.Width, long.MaxValue); throw new InvalidOperationException("Overflow accepted"); }
        catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument, "Overflow result"); }
        Check(node.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Binding, "Invalid write preserves binding");
        node.ClearProperty(PropertyTarget.Width);
        width = 170; model.NotifyPropertyChanged("Width");
        Check((Length)node.GetProperty(PropertyTarget.Width)! == Length.Auto, "Cleared binding is disconnected");

        node.SetPropertyLayer(PropertyTarget.Width, ValuePrecedence.Style, 50);
        model.Bind(node, PropertyTarget.Width, "Width", PropertyBindingMode.OneWay);
        node.SetPropertyLayer(PropertyTarget.Width, ValuePrecedence.Animation, 200);
        width = 210; model.NotifyPropertyChanged("Width");
        Check(node.Width == 200 && (Length)node.GetBaseProperty(PropertyTarget.Width)! == 210, "Managed base updates beneath animation");
        node.ClearPropertyLayer(PropertyTarget.Width, ValuePrecedence.Animation);
        Check(node.Width == 210 && node.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Binding, "Managed overlay removal");
        node.ClearProperty(PropertyTarget.Width);
        Check(node.Width == 50 && node.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Style, "Managed style fallback");
        node.ClearPropertyLayer(PropertyTarget.Width, ValuePrecedence.Style);

        app.LoadFleck("<Window xmlns='urn:sunfleck:v1'><StackPanel IsEnabled='false'><TextBlock Id='label' IsEnabled='true' Width='Auto' Text='🌱'/></StackPanel></Window>");
        var label = app.FindFleckNode("label");
        Check(label.GetPropertySource(PropertyTarget.Width) == PropertyValueSource.Local, "Fleck explicit Auto source");
        Check(!(bool)label.GetProperty(PropertyTarget.IsEnabled)!, "Effective disabled ancestry");
        using (var snapshot = JsonDocument.Parse(app.GetDebugSnapshot())) {
            var item = snapshot.RootElement.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("fleckId").GetString() == "label");
            var property = item.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Width");
            Check(property.GetProperty("valueSource").GetString() == "Local", "Inspector value source");
        }
        var typed = new TextBlock(label);
        typed.ClearValue(TextBlock.WidthProperty);
        Check(typed.GetValueSource(TextBlock.WidthProperty) == PropertyValueSource.Default, "Typed clear and source");

        app.OpenWindow(); app.Poll(0);
        (long Measure, long Arrange) LayoutWork()
        {
            using var snapshot = JsonDocument.Parse(app.GetDebugSnapshot());
            var item = snapshot.RootElement.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("fleckId").GetString() == "label");
            var work = item.GetProperty("layoutWork");
            return (work.GetProperty("measure").GetInt64(), work.GetProperty("arrange").GetInt64());
        }
        var beforeAppearance = LayoutWork();
        label.IsEnabled = false; app.Poll(0);
        Check(LayoutWork() == beforeAppearance && beforeAppearance.Arrange > 0, "Managed inspector exposes layout reuse on appearance updates");

        using var metadataApp = new App(Renderer.Software, headless: true);
        int callbacks = 0;
        metadataApp.RegisterComponent<MetadataControl>("urn:metadata", "MetadataControl", [
            new("Amount", typeof(int), 1, Effects: PropertyEffects.Bindable | PropertyEffects.Animatable | PropertyEffects.AffectsRender,
                Validate: value => { callbacks++; return (int)value! >= 0; }, Coerce: value => Math.Min((int)value!, 10)),
            new("Caption", typeof(string), "default", Effects: PropertyEffects.None,
                Coerce: value => ((string)value!).ToUpperInvariant())
        ], template: "MetadataTemplate");
        metadataApp.RegisterFleckResources([new KeyValuePair<string, string>("metadata.fleck", "<ControlTemplate xmlns='urn:sunfleck:v1' xmlns:c='urn:metadata' Class='MetadataTemplate' Namespace='urn:metadata' TargetType='c:MetadataControl'><TextBlock Text='{TemplateBinding Caption}'/></ControlTemplate>")]);
        const string metadataPage = "<Window xmlns='urn:sunfleck:v1' xmlns:c='urn:metadata'><StackPanel Id='contextRoot'><c:MetadataControl Id='control'/></StackPanel></Window>";
        Check(metadataApp.ValidateFleckWithComponents(metadataPage) is null && callbacks == 0, "Static validation excludes application metadata callbacks");
        metadataApp.LoadFleck(metadataPage);
        var control = metadataApp.FindComponent("control");
        var registeredAmount = control.Node.ResolveProperty("Amount");
        var registeredDescriptor = control.Node.DescribeProperty(registeredAmount);
        Check(registeredDescriptor.OwnerNamespace == "urn:metadata" && registeredDescriptor.OwnerName == "MetadataControl" &&
            registeredDescriptor.Type == PropertyValueType.Int && !registeredDescriptor.ReadOnly, "Managed registered descriptor ABI");
        control.Node.SetProperty(registeredAmount, 100);
        Check((long)control.Node.GetProperty(registeredAmount)! == 10 && (long)control.Node.GetBaseProperty(registeredAmount)! == 100,
            "Registered coercion and base value");
        control.Node.ClearProperty(registeredAmount);
        using var amountModel = metadataApp.CreatePropertyModel().Add("Amount", () => 8);
        var componentContextRoot = metadataApp.FindFleckNode("contextRoot");
        componentContextRoot.DataContext = amountModel;
        control.Node.BindContext(registeredAmount, "Amount");
        Check((long)control.Node.GetProperty(registeredAmount)! == 8 && control.Node.DataContext == amountModel,
            "Custom registered binding uses inherited context");
        using var invalidAmountModel = metadataApp.CreatePropertyModel().Add("Amount", () => -1);
        try { componentContextRoot.DataContext = invalidAmountModel; throw new InvalidOperationException("Invalid component context accepted"); }
        catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument, "Context preflight applies component validator"); }
        Check(componentContextRoot.DataContext == amountModel && (long)control.Node.GetProperty(registeredAmount)! == 8,
            "Rejected component context preserves parent and effective value");
        control.SetDataContext(null);
        Check((long)control.Node.GetProperty(registeredAmount)! == 1 && control.Node.DataContext is null,
            "Component setter establishes shared null boundary");
        control.ClearDataContext();
        Check((long)control.Node.GetProperty(registeredAmount)! == 8 && control.Node.DataContextSource == PropertyValueSource.Inherited,
            "Managed component clear restores inherited binding");
        control.Node.ClearProperty(registeredAmount);
        Check(control.Node.GetPropertySource(registeredAmount) == PropertyValueSource.Default, "Registered clear restores default");
        control.Node.BindProperty(registeredAmount, amountModel, "Amount");
        Check((long)control.Node.GetProperty(registeredAmount)! == 8 && control.Node.GetPropertySource(registeredAmount) == PropertyValueSource.Binding,
            "Managed registered binding options ABI");
        control.Node.ClearProperty(registeredAmount);
        Check(control.Get<string>("Caption") == "DEFAULT", "Default string coercion copied from managed callback");
        control.Set("Caption", "hello");
        Check(control.Get<string>("Caption") == "HELLO", "Managed string callback storage");
        control.Set("Amount", 100);
        Check(control.Get<int>("Amount") == 10 && control.GetBaseValue<int>("Amount") == 100, "Managed effective coercion and base value");
        try { control.Set("Amount", -1); throw new InvalidOperationException("Invalid metadata value accepted"); }
        catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument, "Managed metadata validation"); }
        Check(control.Get<int>("Amount") == 10, "Failed managed metadata update preserves state");
        Check(control.DescribeProperty("Caption").Effects == PropertyEffects.None, "Explicit zero metadata flags");
        using var captionModel = metadataApp.CreatePropertyModel().Add("Caption", () => "bound");
        try { control.Bind("Caption", captionModel, "Caption"); throw new InvalidOperationException("Unbindable property accepted"); }
        catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument, "Explicit binding flag enforced"); }
        try { control.SetLayer("Caption", ValuePrecedence.Animation, "no"); throw new InvalidOperationException("Nonanimatable property accepted"); }
        catch (SunfleckException error) { Check(error.Result == Result.InvalidArgument, "Managed animation flag enforced"); }
        Check(callbacks > 0, "Managed validation called");
        using var inheritanceApp = new App(Renderer.Software, headless: true);
        inheritanceApp.RegisterComponent<MetadataControl>("urn:inherit", "Scope", [new("Level", typeof(int), 7,
            Effects: PropertyEffects.Bindable | PropertyEffects.TwoWay | PropertyEffects.Animatable | PropertyEffects.Inherits)], template: "ScopeTemplate");
        inheritanceApp.RegisterFleckResources([new("scope.fleck", "<ControlTemplate xmlns='urn:sunfleck:v1' xmlns:c='urn:inherit' Class='ScopeTemplate' Namespace='urn:inherit' TargetType='c:Scope'><StackPanel Id='children'><TextBlock Id='level' Text='{TemplateBinding Level}'/></StackPanel></ControlTemplate>")]);
        var parentScope = inheritanceApp.CreateComponent("urn:inherit", "Scope");
        var childScope = inheritanceApp.CreateComponent("urn:inherit", "Scope");
        Check(parentScope is MetadataControl && childScope is MetadataControl, "Programmatic component creation retains registered managed type");
        parentScope.Set("Level", 10);
        parentScope.FindNode("children").Append(childScope.Node);
        var level = childScope.Node.ResolveProperty("Level");
        Check(childScope.Get<int>("Level") == 10 && childScope.FindNode("level").Text == "10" &&
            childScope.Node.GetPropertySource(level) == PropertyValueSource.Inherited &&
            childScope.DescribeProperty("Level").Effects.HasFlag(PropertyEffects.Inherits), "Managed inherited metadata, value, source and template");
        childScope.SetLayer("Level", ValuePrecedence.Style, 5); parentScope.Set("Level", 11);
        Check(childScope.Get<int>("Level") == 5 && childScope.GetValueSource("Level") == PropertyValueSource.Style, "Style masks inherited changes");
        childScope.ClearLayer("Level", ValuePrecedence.Style);
        Check(childScope.Get<int>("Level") == 11, "Clearing style reveals latest inherited value");
        int inheritedValue = 9;
        using var inheritedModel = inheritanceApp.CreatePropertyModel().Add("Level", () => inheritedValue, value => inheritedValue = value);
        parentScope.Node.DataContext = inheritedModel;
        parentScope.Node.BindContext(parentScope.Node.ResolveProperty("Level"), "Level", PropertyBindingMode.TwoWay);
        Check(childScope.Get<int>("Level") == 9, "Context binding supplies an inheritable base value");
        inheritedValue = 8; inheritedModel.NotifyPropertyChanged("Level");
        childScope.SetDataContext(null);
        Check(childScope.Get<int>("Level") == 8, "DataContext null boundary does not block other inheritable properties");
        inheritedModel.Dispose();
        Check(childScope.Get<int>("Level") == 7 && childScope.GetValueSource("Level") == PropertyValueSource.Default,
            "Model destruction removes inherited binding values");
    }
}
