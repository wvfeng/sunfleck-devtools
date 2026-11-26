using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sunfleck.Development;

// An editor lexer: incomplete tags, attributes and Binding expressions remain queryable.
// Runtime validation continues to use the native parser.
internal sealed class FleckDocument(string text)
{
    internal sealed class Attribute(string name, string value, int start, int valueStart, int end, bool closed)
    {
        internal string Name { get; } = name;
        internal string Value { get; } = value;
        internal int Start { get; } = start;
        internal int ValueStart { get; } = valueStart;
        internal int End { get; } = end;
        internal bool Closed { get; } = closed;
    }
    internal sealed class Tag(string name, int start, int nameStart, bool closing, Tag? parent)
    {
        internal string Name = name;
        internal int Start = start, NameStart = nameStart, End, Extent;
        internal bool Closing = closing, Complete, SelfClosing, HasClosing;
        internal Tag? Parent = parent;
        internal List<Attribute> Attributes = [];
        internal Attribute? Find(string name) => Attributes.FirstOrDefault(attribute => attribute.Name == name);
    }

    internal string Text { get; } = text;
    internal List<Tag> Tags { get; } = Scan(text);
    private readonly int[] lineStarts = IndexLines(text);
    internal string? Model => Tags.FirstOrDefault(tag => tag.Name == "Window" && !tag.Closing)?.Find("Model")?.Value;
    internal bool Theme => Tags.FirstOrDefault(tag => !tag.Closing)?.Name == "Theme";

    private static bool NameChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or ':' or '.' or '-';
    private static int[] IndexLines(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }
    private static List<Tag> Scan(string text)
    {
        var tags = new List<Tag>();
        var stack = new List<Tag>();
        int cursor = 0;
        while (cursor < text.Length)
        {
            int start = text.IndexOf('<', cursor);
            if (start < 0)
            {
                break;
            }
            if (string.CompareOrdinal(text, start, "<!--", 0, 4) == 0)
            {
                int end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
                cursor = end < 0 ? text.Length : end + 3;
                continue;
            }
            if (string.CompareOrdinal(text, start, "<?", 0, 2) == 0)
            {
                int end = text.IndexOf("?>", start + 2, StringComparison.Ordinal);
                cursor = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (start + 1 < text.Length && text[start + 1] == '!')
            {
                int end = text.IndexOf('>', start + 2);
                cursor = end < 0 ? text.Length : end + 1;
                continue;
            }
            cursor = start + 1;
            bool closing = cursor < text.Length && text[cursor] == '/';
            if (closing)
            {
                cursor++;
            }
            int nameStart = cursor;
            while (cursor < text.Length && NameChar(text[cursor]))
            {
                cursor++;
            }
            var tag = new Tag(text.Substring(nameStart, cursor - nameStart), start, nameStart, closing, stack.LastOrDefault());
            while (cursor < text.Length && text[cursor] != '<')
            {
                if (char.IsWhiteSpace(text[cursor]))
                {
                    cursor++;
                    continue;
                }
                if (text[cursor] == '>')
                {
                    tag.Complete = true;
                    cursor++;
                    break;
                }
                if (text[cursor] == '/' && cursor + 1 < text.Length && text[cursor + 1] == '>')
                {
                    tag.Complete = tag.SelfClosing = true;
                    cursor += 2;
                    break;
                }
                int attributeStart = cursor;
                while (cursor < text.Length && NameChar(text[cursor]))
                {
                    cursor++;
                }
                if (cursor == attributeStart)
                {
                    cursor++;
                    continue;
                }
                string name = text.Substring(attributeStart, cursor - attributeStart);
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                {
                    cursor++;
                }
                if (cursor == text.Length || text[cursor] != '=')
                {
                    tag.Attributes.Add(new Attribute(name, "", attributeStart, cursor, cursor, false));
                    continue;
                }
                cursor++;
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                {
                    cursor++;
                }
                if (cursor == text.Length || text[cursor] is not ('\'' or '"'))
                {
                    tag.Attributes.Add(new Attribute(name, "", attributeStart, cursor, cursor, false));
                    continue;
                }
                char quote = text[cursor++];
                int valueStart = cursor;
                while (cursor < text.Length && text[cursor] != quote && text[cursor] != '<')
                {
                    cursor++;
                }
                bool closed = cursor < text.Length && text[cursor] == quote;
                tag.Attributes.Add(new Attribute(name, text.Substring(valueStart, cursor - valueStart), attributeStart, valueStart, cursor, closed));
                if (closed)
                {
                    cursor++;
                }
            }
            tag.End = cursor;
            tag.Extent = tag.SelfClosing ? cursor : text.Length;
            tags.Add(tag);
            if (closing)
            {
                int index = stack.FindLastIndex(open => open.Name == tag.Name);
                if (index >= 0)
                {
                    stack[index].Extent = cursor;
                    stack[index].HasClosing = true;
                    stack.RemoveRange(index, stack.Count - index);
                }
            }
            else if (tag.Complete && !tag.SelfClosing)
            {
                stack.Add(tag);
            }
        }
        return tags;
    }

    internal int Offset(JsonNode position)
    {
        int line = position["line"]!.GetValue<int>(), character = position["character"]!.GetValue<int>();
        if (line < 0 || character < 0)
        {
            throw new ArgumentException("Negative document position.");
        }
        if (line >= lineStarts.Length)
        {
            return Text.Length;
        }
        int offset = lineStarts[line];
        int lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : Text.Length;
        while (lineEnd > offset && Text[lineEnd - 1] is '\r' or '\n')
        {
            lineEnd--;
        }
        return offset + Math.Min(character, lineEnd - offset);
    }

    internal JsonObject Position(int offset)
    {
        offset = Math.Min(Text.Length, Math.Max(0, offset));
        int line = Array.BinarySearch(lineStarts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }
        return new JsonObject { ["line"] = line, ["character"] = offset - lineStarts[line] };
    }

    internal JsonObject Range(int start, int end) => new() { ["start"] = Position(start), ["end"] = Position(end) };
    // The caret before '<' still belongs to an unfinished preceding tag.
    // Including the next tag at its start can select a name after the caret.
    internal Tag? At(int offset) => Tags.LastOrDefault(tag => tag.Start < offset && (offset < tag.End || !tag.Complete && offset == tag.End));
    internal bool InTemplate(Tag tag) => Theme && tag.Name != "Theme" && tag.Name is not ("Resource" or "DockTemplate");
}
