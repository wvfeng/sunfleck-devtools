using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Sunfleck.Development;

internal static class FleckFormatter
{
    internal readonly struct Edit(int start, int length, string text)
    {
        internal int Start { get; } = start;
        internal int Length { get; } = length;
        internal string Text { get; } = text;
    }

    internal static List<Edit> GetEdits(FleckDocument document, int tabSize, bool insertSpaces)
    {
        var edits = new List<Edit>();
        XDocument xml;
        try
        {
            using var text = new StringReader(document.Text);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return edits;
        }

        if (xml.Descendants().Any(element => element.Attribute(XNamespace.Xml + "space") is not null ||
            element.Nodes().OfType<XText>().Any(text => text is XCData || !string.IsNullOrWhiteSpace(text.Value))))
        {
            return edits;
        }

        tabSize = Math.Min(16, Math.Max(1, tabSize));
        int firstBreak = document.Text.IndexOfAny(new[] { '\r', '\n' });
        string newline = firstBreak < 0 || document.Text[firstBreak] == '\n' ? "\n" :
            firstBreak + 1 < document.Text.Length && document.Text[firstBreak + 1] == '\n' ? "\r\n" : "\r";
        int depth = 0;
        FleckDocument.Tag? previous = null;
        foreach (var tag in document.Tags)
        {
            if (tag.Closing)
            {
                depth--;
            }

            int start = previous?.End ?? 0;
            var gap = document.Text.Substring(start, tag.Start - start);
            if (string.IsNullOrWhiteSpace(gap))
            {
                string replacement = previous is null || tag.Closing && previous.Start == tag.Parent?.Start
                    ? ""
                    : newline + (insertSpaces ? new string(' ', depth * tabSize) : new string('\t', depth));
                if (gap != replacement)
                {
                    edits.Add(new Edit(start, tag.Start - start, replacement));
                }
            }

            if (!tag.Closing && !tag.SelfClosing)
            {
                depth++;
            }
            previous = tag;
        }
        return edits;
    }

}
