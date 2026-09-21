using System.Text;
using NfaLoader.Localization;

namespace NfaLoader.Services;

internal static class VdfDocument
{
    // Steam never writes its own VDF files with a BOM, and a config.vdf with a BOM is unreadable to Steam, which then resets the whole file
    // (see the SteamConfigService.UpdateConfigVdf comment for how this was tracked down), so writes must use an encoding without a BOM.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Dictionary<string, object> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        try
        {
            return new VdfParser(File.ReadAllText(path, Encoding.UTF8)).Parse();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                Loc.Tf("Steam_Error_VdfReadFailed_Format", Path.GetFileName(path), ex.Message),
                ex);
        }
    }

    // Used by the sign-in write path: a parse failure must not abort the sign-in. Same as the original binary:
    // if it cannot be parsed, carry on with an empty document (at worst the file gets regenerated) instead of throwing out of the whole flow.
    public static Dictionary<string, object> LoadOrEmpty(string path)
    {
        try
        {
            return Load(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to parse {Path.GetFileName(path)}, continuing with an empty document (the file will be regenerated): {ex.Message}");
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }

    public static void Save(string path, Dictionary<string, object> document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write a temp file first, then replace atomically (same as AccountHistoryService.WriteDocument),
        // so an interrupted process cannot leave Steam's core config half-written; the random suffix keeps concurrent saves from clobbering each other's temp file.
        var tempPath = path + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllText(tempPath, Write(document), Utf8NoBom);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // A failed cleanup only leaves a temp file behind and does not touch the real file, so swallow it to keep the original exception.
            }

            throw;
        }
    }

    // ---------- Read helpers ----------
    // VDF key casing is not stable across Steam versions, so every read tries an exact match first, then a case-insensitive scan.
    // The dictionary comparer stays Ordinal: writes must keep the original casing, and changing the comparer would silently merge keys that differ only in case.

    public static object? GetValue(Dictionary<string, object> parent, string key)
    {
        if (parent.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var (candidate, candidateValue) in parent)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return candidateValue;
            }
        }

        return null;
    }

    public static Dictionary<string, object>? GetObject(Dictionary<string, object> parent, string key) =>
        GetValue(parent, key) as Dictionary<string, object>;

    public static string? GetString(Dictionary<string, object> parent, string key) =>
        GetValue(parent, key) as string;

    private static string Write(Dictionary<string, object> document)
    {
        var builder = new StringBuilder();
        WriteObject(builder, document, 0);
        return builder.ToString();
    }

    private static void WriteObject(StringBuilder builder, Dictionary<string, object> obj, int indent)
    {
        foreach (var (key, value) in obj)
        {
            var prefix = new string('\t', indent);
            if (value is Dictionary<string, object> child)
            {
                builder.Append(prefix).Append('"').Append(Escape(key)).AppendLine("\"");
                builder.Append(prefix).AppendLine("{");
                WriteObject(builder, child, indent + 1);
                builder.Append(prefix).AppendLine("}");
            }
            else
            {
                builder.Append(prefix)
                    .Append('"').Append(Escape(key)).Append("\"\t\t\"")
                    .Append(Escape(value?.ToString() ?? string.Empty))
                    .AppendLine("\"");
            }
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private sealed class VdfParser
    {
        private readonly List<string> _tokens;
        private int _position;

        public VdfParser(string text)
        {
            _tokens = Tokenize(text);
        }

        public Dictionary<string, object> Parse()
        {
            var document = ParseObject();
            if (_position != _tokens.Count)
            {
                throw new FormatException(Loc.T("Steam_Error_Vdf_TrailingContent"));
            }

            return document;
        }

        private Dictionary<string, object> ParseObject()
        {
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);

            while (_position < _tokens.Count)
            {
                if (Peek() == "}")
                {
                    break;
                }

                var key = Read();
                if (key is "{" or "}")
                {
                    throw new FormatException(Loc.T("Steam_Error_Vdf_BadKeyPosition"));
                }

                if (_position >= _tokens.Count)
                {
                    throw new FormatException(Loc.T("Steam_Error_Vdf_MissingValue"));
                }

                if (Peek() == "{")
                {
                    Read();
                    obj[key] = ParseObject();
                    Expect("}");
                }
                else
                {
                    obj[key] = Read();
                }
            }

            return obj;
        }

        private string Peek() => _tokens[_position];

        private string Read() => _tokens[_position++];

        private void Expect(string expected)
        {
            if (_position >= _tokens.Count || Read() != expected)
            {
                throw new FormatException(Loc.Tf("Steam_Error_Vdf_MissingExpected_Format", expected));
            }
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
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
                    index += 2;
                    while (index < text.Length && text[index] is not '\r' and not '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (current is '{' or '}')
                {
                    tokens.Add(current.ToString());
                    index++;
                    continue;
                }

                // Valve conditional tag (such as "key" "value" [$WIN32]): skip the whole tag and treat the entry as unconditional,
                // otherwise it is read as the next key, the structure fails to parse and the sign-in aborts.
                if (current == '[')
                {
                    index++;
                    while (index < text.Length && text[index] != ']')
                    {
                        index++;
                    }

                    if (index < text.Length)
                    {
                        index++;
                    }

                    continue;
                }

                // A stray ']' (malformed input such as a broken conditional tag): ReadBare stops in front of it without advancing,
                // and falling into the bare string branch below would loop forever, so skip it here.
                if (current == ']')
                {
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    tokens.Add(ReadQuoted(text, ref index));
                    continue;
                }

                var bare = ReadBare(text, ref index);
                if (bare.Length == 0)
                {
                    // If ReadBare did not advance, it hit a stop character that none of the branches above handle,
                    // so skip it by force to keep tokenizing moving forward on any input.
                    index++;
                    continue;
                }

                tokens.Add(bare);
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
                        _ => escaped
                    });
                    continue;
                }

                builder.Append(current);
            }

            throw new FormatException(Loc.T("Steam_Error_Vdf_UnterminatedString"));
        }

        private static string ReadBare(string text, ref int index)
        {
            var start = index;
            while (index < text.Length &&
                !char.IsWhiteSpace(text[index]) &&
                text[index] is not '{' and not '}' and not '[' and not ']')
            {
                index++;
            }

            return text[start..index];
        }
    }
}
