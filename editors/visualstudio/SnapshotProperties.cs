using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Sunfleck.VisualStudio;

internal sealed class SnapshotProperties : DockPanel
{
    private readonly TextBlock title = new() { Margin = new Thickness(6, 4, 6, 0), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { Margin = new Thickness(6, 3, 6, 5), TextWrapping = TextWrapping.Wrap };
    private readonly GridView columns = new();
    private readonly ListView values = new() { IsTabStop = false };
    private readonly GridViewColumn property = new() { Width = 130, DisplayMemberBinding = new Binding(nameof(Row.Name)) };
    private readonly GridViewColumn current = new() { Width = 135, DisplayMemberBinding = new Binding(nameof(Row.Value)) };
    private readonly GridViewColumn fallback = new() { Width = 95, DisplayMemberBinding = new Binding(nameof(Row.DefaultValue)) };
    private sealed class Row
    {
        public string Name { get; }
        public string Value { get; }
        public string DefaultValue { get; }

        internal Row(string name, JToken? value, JToken? defaultValue = null)
        {
            Name = name;
            Value = Display(value);
            DefaultValue = Display(defaultValue);
        }
    }

    internal SnapshotProperties()
    {
        SetDock(title, Dock.Top);
        SetDock(summary, Dock.Top);
        Children.Add(title);
        Children.Add(summary);
        Children.Add(values);
        columns.Columns.Add(property);
        columns.Columns.Add(current);
        columns.Columns.Add(fallback);
        values.View = columns;
        UseTheme(values);
        values.ItemContainerStyle = RowStyle(typeof(ListViewItem), ListViewItem.IsSelectedProperty);
        ScrollViewer.SetHorizontalScrollBarVisibility(values, ScrollBarVisibility.Auto);
        var header = new Style(typeof(GridViewColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBackgroundBrushKey)));
        header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
        header.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBorderBrushKey)));
        columns.ColumnHeaderContainerStyle = header;
    }

    internal static void UseTheme(Control control)
    {
        control.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        control.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        control.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
    }

    internal static Style RowStyle(Type itemType, DependencyProperty isSelected)
    {
        var style = new Style(itemType);
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
        var selected = new Trigger { Property = isSelected, Value = true };
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
        style.Triggers.Add(selected);
        return style;
    }

    internal void Show(JToken? node, bool english, JObject? models = null)
    {
        title.Text = english ? "Element properties (read only)" : "元素属性（只读）";
        property.Header = english ? "Property" : "属性";
        current.Header = english ? "Current" : "当前值";
        fallback.Header = english ? "Default" : "默认值";
        if (node is not JObject data)
        {
            summary.Text = english ? "Select an object" : "选择对象以查看属性";
            values.ItemsSource = null;
            return;
        }
        var rows = new List<Row>();
        summary.Text = (string?)data["fleckId"] is string name && name.Length > 0
            ? $"{name} · {data["type"]}"
            : (string?)data["type"] ?? (string?)data["title"] ?? (string?)data["path"] ?? "";
        if (data["bounds"] is JArray bounds && bounds.Count == 4)
        {
            double width = (double)bounds[2] - (double)bounds[0];
            double height = (double)bounds[3] - (double)bounds[1];
            summary.Text += $"\n{(english ? "Actual size" : "实际尺寸")}  {width} × {height} px";
            rows.Add(new Row("Bounds (px)", bounds));
            rows.Add(new Row("Clip (px)", data["clip"]));
        }
        if (data["properties"] is JArray properties)
        {
            foreach (var entry in properties)
            {
                if ((string?)entry["name"] is string key)
                {
                    rows.Add(new Row(key, entry["value"], entry["defaultValue"]));
                }
            }
        }
        foreach (var entry in data.Properties())
        {
            if (entry.Name is not ("properties" or "bounds" or "clip"))
            {
                rows.Add(new Row(entry.Name, entry.Value));
            }
        }
        if (data["bindings"] is JArray bindings && models is not null)
        {
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                if ((string?)binding["model"] is string model && shown.Add(model) && models[model] is JToken metadata)
                {
                    rows.Add(new Row($"Model {model}", metadata));
                }
            }
        }
        values.ItemsSource = rows;
    }

    private static string Display(JToken? value) => value?.Type switch
    {
        null or JTokenType.Null => "—",
        JTokenType.String => (string)value!,
        _ => value!.ToString(Formatting.None)
    };
}

