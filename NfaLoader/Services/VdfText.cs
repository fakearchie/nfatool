using System.Text;
using NfaLoader.Localization;

namespace NfaLoader.Services;

/// <summary>
/// Changes a Steam VDF file as text: only the entries the loader needs are changed or added, and every other byte
/// stays exactly as Steam wrote it. Rebuilding a whole file from a parsed copy loses what the parser drops, such as
/// conditional tags and repeated keys. Keys are found the way Steam reads them: case does not matter and the first
/// match wins, so a value is always written where Steam will look for it.
/// </summary>
internal sealed class VdfText
{
    private string _text;
    private Node _root;
    private readonly string _newLine;

    private VdfText(string text)
    {
        _root = ParseTree(text);
        _text = text;
        _newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    public string Text => _text;

    /// <exception cref="FormatException">The text is not valid VDF.</exception>
    public static VdfText Parse(string text) => new(text);

    public static bool TryParse(string text, out VdfText document)
    {
        try
        {
            document = new VdfText(text);
            return true;
        }
        catch (FormatException)
        {
            document = Empty();
            return false;
        }
    }

    public static VdfText Empty() => new(string.Empty);

    // ---------- Reading ----------

    /// <summary>The value at the path, where the last key names the value, or null.</summary>
    public string? GetValue(params string[] path)
    {
        var parent = Find(path[..^1]);
        return parent is null ? null : FindChild(parent, path[^1], wantObject: false)?.Value;
    }

    /// <summary>The plain values directly inside the object at the path. For a repeated key the first one wins.</summary>
    public Dictionary<string, string> GetValues(params string[] path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Find(path) is { } parent)
        {
            foreach (var child in parent.Children!)
            {
                if (!child.IsObject)
                {
                    values.TryAdd(child.Key, child.Value!);
                }
            }
        }

        return values;
    }

    /// <summary>The keys of the objects directly inside the object at the path, in file order.</summary>
    public IReadOnlyList<string> GetObjectKeys(params string[] path) =>
        Find(path)?.Children!.Where(child => child.IsObject).Select(child => child.Key).ToList() ?? [];

    /// <summary>The path as the file spells it, for the log, or null when part of it is missing.</summary>
    public string? DescribePath(params string[] path)
    {
        var names = new List<string>();
        var current = _root;
        foreach (var key in path)
        {
            if (FindChild(current, key, wantObject: true) is not { } child)
            {
                return null;
            }

            names.Add(child.Key);
            current = child;
        }

        return string.Join('/', names);
    }

    /// <summary>
    /// How many objects along the path Steam never reads, because an earlier object at the same level has the same
    /// name apart from case.
    /// </summary>
    public int CountShadowedObjects(params string[] path)
    {
        var shadowed = 0;
        var current = _root;
        foreach (var key in path)
        {
            shadowed += Math.Max(0, current.Children!.Count(child => child.IsObject && KeyEquals(child.Key, key)) - 1);
            if (FindChild(current, key, wantObject: true) is not { } child)
            {
                break;
            }

            current = child;
        }

        return shadowed;
    }

    // ---------- Writing ----------

    /// <summary>Sets a value inside the object at the path, adding the objects and the value when they are missing.</summary>
    public void SetValue(string[] objectPath, string key, string value)
    {
        var parent = EnsureObject(objectPath);
        if (FindChild(parent, key, wantObject: false) is { } existing)
        {
            if (existing.Value != value)
            {
                Apply([new Edit(existing.ValueStart, existing.End - existing.ValueStart, Quote(value))]);
            }

            return;
        }

        Apply([InsertBlock(parent, Indent(parent.Depth + 1) + Quote(key) + "\t\t" + Quote(value))]);
    }

    /// <summary>Adds a value inside the object at the path only when it is not there yet.</summary>
    public void SetValueIfMissing(string[] objectPath, string key, string value)
    {
        if (Find(objectPath) is not { } parent || FindChild(parent, key, wantObject: false) is null)
        {
            SetValue(objectPath, key, value);
        }
    }

    /// <summary>
    /// Sets the value with this key in each object directly inside the object at the path, except the one named
    /// <paramref name="exceptKey"/>. Only values that are already there are changed; none are added.
    /// </summary>
    public void SetValueInEachObject(string[] parentPath, string key, string value, string? exceptKey = null)
    {
        if (Find(parentPath) is not { } parent)
        {
            return;
        }

        var edits = new List<Edit>();
        foreach (var child in parent.Children!)
        {
            if (!child.IsObject || (exceptKey is not null && KeyEquals(child.Key, exceptKey)))
            {
                continue;
            }

            if (FindChild(child, key, wantObject: false) is { } existing && existing.Value != value)
            {
                edits.Add(new Edit(existing.ValueStart, existing.End - existing.ValueStart, Quote(value)));
            }
        }

        Apply(edits);
    }

    /// <summary>Sets every value with this key anywhere in the file. Returns how many were changed.</summary>
    public int SetValueEverywhere(string key, string value)
    {
        var edits = new List<Edit>();
        Collect(_root);
        Apply(edits);
        return edits.Count;

        void Collect(Node node)
        {
            foreach (var child in node.Children!)
            {
                if (child.IsObject)
                {
                    Collect(child);
                }
                else if (KeyEquals(child.Key, key) && child.Value != value)
                {
                    edits.Add(new Edit(child.ValueStart, child.End - child.ValueStart, Quote(value)));
                }
            }
        }
    }

    /// <summary>
    /// Older loader versions looked keys up with exact case. On PCs where Steam spells a key differently (for example
    /// "valve" instead of "Valve") they added a second object next to Steam's own, which Steam never reads because it
    /// takes the first match. This moves whatever such later objects along the path hold into the first one, which
    /// Steam does read, and removes them. Where both hold the same value, the first one's stays. Returns how many
    /// objects were merged.
    /// </summary>
    public int MergeShadowedObjects(params string[] path)
    {
        var merged = 0;
        for (var level = 0; level < path.Length; level++)
        {
            if (Find(path[..level]) is not { } parent)
            {
                break;
            }

            var matches = parent.Children!.Where(child => child.IsObject && KeyEquals(child.Key, path[level])).ToList();
            if (matches.Count < 2)
            {
                continue;
            }

            var edits = new List<Edit>();
            foreach (var duplicate in matches.Skip(1))
            {
                MergeInto(matches[0], duplicate, edits);
                edits.Add(Remove(duplicate));
            }

            Apply(edits);
            merged += matches.Count - 1;
        }

        return merged;
    }

    private void MergeInto(Node target, Node source, List<Edit> edits)
    {
        foreach (var child in source.Children!)
        {
            var match = FindChild(target, child.Key, child.IsObject);
            if (match is null)
            {
                // Both objects sit at the same depth, so the copied lines already have the right indentation.
                edits.Add(InsertBlock(target, Indent(target.Depth + 1) + _text[child.Start..child.End]));
            }
            else if (child.IsObject)
            {
                MergeInto(match, child, edits);
            }
        }
    }

    private Node? Find(string[] path)
    {
        var current = _root;
        foreach (var key in path)
        {
            if (FindChild(current, key, wantObject: true) is not { } child)
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    private Node EnsureObject(string[] path)
    {
        var current = _root;
        for (var level = 0; level < path.Length; level++)
        {
            if (FindChild(current, path[level], wantObject: true) is { } child)
            {
                current = child;
                continue;
            }

            // Add the rest of the path in one go, then walk it again in the new text.
            Apply([InsertBlock(current, BuildObjects(path[level..], current.Depth + 1))]);
            return Find(path) ?? throw new FormatException(Loc.T("Steam_Error_Vdf_MissingValue"));
        }

        return current;
    }

    private string BuildObjects(string[] keys, int depth)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < keys.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(_newLine);
            }

            builder.Append(Indent(depth + index)).Append(Quote(keys[index])).Append(_newLine)
                .Append(Indent(depth + index)).Append('{');
        }

        for (var index = keys.Length - 1; index >= 0; index--)
        {
            builder.Append(_newLine).Append(Indent(depth + index)).Append('}');
        }

        return builder.ToString();
    }

    // Adds whole lines at the end of an object, right before its closing brace, or at the end of the file.
    private Edit InsertBlock(Node parent, string block)
    {
        if (parent.Close < 0)
        {
            var needsNewLine = _text.Length > 0 && _text[^1] != '\n';
            return new Edit(_text.Length, 0, (needsNewLine ? _newLine : "") + block + _newLine);
        }

        var lineStart = parent.Close;
        while (lineStart > 0 && _text[lineStart - 1] is ' ' or '\t')
        {
            lineStart--;
        }

        if (lineStart == 0 || _text[lineStart - 1] == '\n')
        {
            return new Edit(lineStart, 0, block + _newLine);
        }

        // The closing brace shares its line with something else, as in "{}": give it a line of its own.
        return new Edit(parent.Close, 0, _newLine + block + _newLine + Indent(parent.Depth));
    }

    // Removes a key with its value or object, and its line when nothing else is on it.
    private Edit Remove(Node node)
    {
        var start = node.Start;
        var end = node.End;
        var lineStart = start;
        while (lineStart > 0 && _text[lineStart - 1] is ' ' or '\t')
        {
            lineStart--;
        }

        if (lineStart == 0 || _text[lineStart - 1] == '\n')
        {
            var lineEnd = end;
            while (lineEnd < _text.Length && _text[lineEnd] is ' ' or '\t')
            {
                lineEnd++;
            }

            if (lineEnd < _text.Length && _text[lineEnd] == '\r')
            {
                lineEnd++;
            }

            if (lineEnd < _text.Length && _text[lineEnd] == '\n')
            {
                start = lineStart;
                end = lineEnd + 1;
            }
        }

        return new Edit(start, end - start, string.Empty);
    }

    // Applies edits that were all measured against the current text, from the last position backwards so earlier
    // positions stay valid. Edits at the same position keep the order they were added in. The result is parsed again,
    // so an edit that broke the file throws here, before anything is written to disk.
    private void Apply(List<Edit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        var text = _text;
        foreach (var edit in edits.Select((edit, order) => (edit, order))
                     .OrderByDescending(item => item.edit.Position)
                     .ThenByDescending(item => item.order)
                     .Select(item => item.edit))
        {
            text = string.Concat(text.AsSpan(0, edit.Position), edit.Insert, text.AsSpan(edit.Position + edit.Length));
        }

        _root = ParseTree(text);
        _text = text;
    }

    private static Node? FindChild(Node parent, string key, bool wantObject) =>
        parent.Children!.FirstOrDefault(child => child.IsObject == wantObject && KeyEquals(child.Key, key));

    private static bool KeyEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Indent(int depth) => new('\t', Math.Max(0, depth));

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private readonly record struct Edit(int Position, int Length, string Insert);

    private sealed class Node
    {
        public string Key = string.Empty;
        public int Depth;
        public int Start;
        public int End;
        public string? Value;
        public int ValueStart;
        public int Close = -1;
        public List<Node>? Children;

        public bool IsObject => Children is not null;
    }

    // ---------- Parsing ----------

    private enum TokenKind
    {
        Text,
        Open,
        Close,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Start, int End);

    private static Node ParseTree(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var root = new Node { Depth = -1, Children = [] };
        ParseChildren(root, tokens, ref position);
        if (position != tokens.Count)
        {
            throw new FormatException(Loc.T("Steam_Error_Vdf_TrailingContent"));
        }

        return root;
    }

    private static void ParseChildren(Node parent, List<Token> tokens, ref int position)
    {
        while (position < tokens.Count && tokens[position].Kind != TokenKind.Close)
        {
            var key = tokens[position++];
            if (key.Kind != TokenKind.Text)
            {
                throw new FormatException(Loc.T("Steam_Error_Vdf_BadKeyPosition"));
            }

            if (position >= tokens.Count)
            {
                throw new FormatException(Loc.T("Steam_Error_Vdf_MissingValue"));
            }

            var node = new Node { Key = key.Text, Depth = parent.Depth + 1, Start = key.Start };
            var next = tokens[position++];
            if (next.Kind == TokenKind.Open)
            {
                node.Children = [];
                ParseChildren(node, tokens, ref position);
                if (position >= tokens.Count)
                {
                    throw new FormatException(Loc.Tf("Steam_Error_Vdf_MissingExpected_Format", "}"));
                }

                var close = tokens[position++];
                node.Close = close.Start;
                node.End = close.End;
            }
            else if (next.Kind == TokenKind.Text)
            {
                node.Value = next.Text;
                node.ValueStart = next.Start;
                node.End = next.End;
            }
            else
            {
                throw new FormatException(Loc.T("Steam_Error_Vdf_MissingValue"));
            }

            parent.Children!.Add(node);
        }
    }

    // Follows VdfDocument's parser, but every token keeps its position in the text.
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (current is '{' or '}')
            {
                tokens.Add(new Token(current == '{' ? TokenKind.Open : TokenKind.Close, current.ToString(), index, index + 1));
                index++;
                continue;
            }

            // A conditional tag such as [$WIN32] after a value. It stays in the text, but it is not a key or a value.
            if (current == '[')
            {
                while (index < text.Length && text[index] != ']')
                {
                    index++;
                }

                index = Math.Min(index + 1, text.Length);
                continue;
            }

            if (current == ']')
            {
                index++;
                continue;
            }

            var start = index;
            if (current == '"')
            {
                tokens.Add(new Token(TokenKind.Text, ReadQuoted(text, ref index), start, index));
                continue;
            }

            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not '{' and not '}' and not '[' and not ']' and not '"')
            {
                index++;
            }

            if (index == start)
            {
                index++;
                continue;
            }

            tokens.Add(new Token(TokenKind.Text, text[start..index], start, index));
        }

        return tokens;
    }

    private static string ReadQuoted(string text, ref int index)
    {
        var builder = new StringBuilder();
        index++;
        while (index < text.Length)
        {
            var current = text[index++];
            if (current == '"')
            {
                return builder.ToString();
            }

            if (current == '\\' && index < text.Length)
            {
                var escaped = text[index++];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escaped,
                });
                continue;
            }

            builder.Append(current);
        }

        throw new FormatException(Loc.T("Steam_Error_Vdf_UnterminatedString"));
    }
}
