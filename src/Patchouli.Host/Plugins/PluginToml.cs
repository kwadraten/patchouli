using System.Globalization;
using System.Text;

namespace Patchouli.Host.Plugins;

/// <summary>Raised when manifest TOML cannot be parsed by the strict supported subset.</summary>
public sealed class PluginTomlException(int line, string message)
    : Exception($"TOML line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// Strict, fail-closed TOML subset reader for patchouli-plugin.toml. It supports the TOML
/// 1.0 grammar the manifest schema uses — comments, [table]/[[array]] headers, dotted keys,
/// basic/literal single-line strings, integers, floats, booleans, arrays and inline tables —
/// and rejects everything else (multi-line strings, dates/times, bare-word values, duplicate
/// keys) with a line-numbered diagnostic instead of guessing. This is the interim parser used
/// until the intended Tomlyn package can be restored (see Patchouli.Host.csproj); the public
/// surface is a plain dictionary model so the swap is local.
/// </summary>
public static class PluginToml
{
    public static IReadOnlyDictionary<string, object?> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Parser parser = new(text);
        return parser.ParseDocument();
    }

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _position;
        private int _line = 1;

        private char? Current => _position < _text.Length ? _text[_position] : null;

        private char? Peek(int ahead)
        {
            return _position + ahead < _text.Length ? _text[_position + ahead] : null;
        }

        private char Advance()
        {
            char value = _text[_position++];
            if (value == '\n')
            {
                _line++;
            }

            return value;
        }

        private PluginTomlException Fail(string message)
        {
            return new PluginTomlException(_line, message);
        }

        private void SkipInlineWhitespace()
        {
            while (Current is ' ' or '\t')
            {
                Advance();
            }
        }

        private void SkipWhitespaceCommentsAndNewlines()
        {
            while (true)
            {
                if (Current is ' ' or '\t' or '\r' or '\n')
                {
                    Advance();
                }
                else if (Current == '#')
                {
                    while (Current is not (null or '\n'))
                    {
                        Advance();
                    }
                }
                else
                {
                    return;
                }
            }
        }

        public IReadOnlyDictionary<string, object?> ParseDocument()
        {
            Dictionary<string, object?> root = new(StringComparer.Ordinal);
            Dictionary<string, object?> current = root;
            SkipWhitespaceCommentsAndNewlines();
            while (Current is not null)
            {
                if (Current == '[')
                {
                    current = ParseHeader(root);
                }
                else
                {
                    ParseKeyValue(current);
                }

                SkipWhitespaceCommentsAndNewlines();
            }

            return root;
        }

        private Dictionary<string, object?> ParseHeader(Dictionary<string, object?> root)
        {
            Advance(); // '['
            bool array = Current == '[';
            if (array)
            {
                Advance();
            }

            SkipInlineWhitespace();
            string[] key = ParseKey();
            SkipInlineWhitespace();
            if (Current != ']')
            {
                throw Fail("Expected ']' after a table header.");
            }

            Advance();
            if (array)
            {
                if (Current != ']')
                {
                    throw Fail("Expected ']]' after an array-of-tables header.");
                }

                Advance();
            }

            RequireEndOfStatement();
            return array ? NavigateArrayTable(root, key) : NavigateTable(root, key);
        }

        private Dictionary<string, object?> NavigateTable(Dictionary<string, object?> root, string[] key)
        {
            Dictionary<string, object?> table = root;
            for (int index = 0; index < key.Length; index++)
            {
                string part = key[index];
                if (!table.TryGetValue(part, out object? existing))
                {
                    if (index < key.Length - 1)
                    {
                        Dictionary<string, object?> next = new(StringComparer.Ordinal);
                        table[part] = next;
                        table = next;
                        continue;
                    }

                    Dictionary<string, object?> created = new(StringComparer.Ordinal);
                    table[part] = created;
                    return created;
                }

                switch (existing)
                {
                    case Dictionary<string, object?> nested:
                        if (index == key.Length - 1)
                        {
                            return nested; // Reopening a supertable declared implicitly is legal.
                        }

                        table = nested;
                        break;
                    case List<object?> array when array.Count > 0 &&
                                                  array[^1] is Dictionary<string, object?> last:
                        // [a.b] under an existing [[a]] array addresses its last element.
                        table = last;
                        if (index == key.Length - 1)
                        {
                            return table;
                        }

                        break;
                    default:
                        throw Fail($"Key '{string.Join('.', key)}' conflicts with an existing value.");
                }
            }

            return table;
        }

        private Dictionary<string, object?> NavigateArrayTable(Dictionary<string, object?> root, string[] key)
        {
            Dictionary<string, object?> table = root;
            for (int index = 0; index < key.Length - 1; index++)
            {
                if (!table.TryGetValue(key[index], out object? existing))
                {
                    Dictionary<string, object?> next = new(StringComparer.Ordinal);
                    table[key[index]] = next;
                    table = next;
                    continue;
                }

                switch (existing)
                {
                    case Dictionary<string, object?> nested:
                        table = nested;
                        break;
                    case List<object?> array when array.Count > 0 &&
                                                  array[^1] is Dictionary<string, object?> last:
                        table = last;
                        break;
                    default:
                        throw Fail($"Key '{string.Join('.', key)}' conflicts with an existing value.");
                }
            }

            string lastKey = key[^1];
            List<object?> list;
            if (!table.TryGetValue(lastKey, out object? slot))
            {
                list = [];
                table[lastKey] = list;
            }
            else if (slot is List<object?> existingList)
            {
                list = existingList;
            }
            else
            {
                throw Fail($"Key '{string.Join('.', key)}' is not an array of tables.");
            }

            Dictionary<string, object?> element = new(StringComparer.Ordinal);
            list.Add(element);
            return element;
        }

        private void ParseKeyValue(Dictionary<string, object?> table)
        {
            string[] key = ParseKey();
            SkipInlineWhitespace();
            if (Current != '=')
            {
                throw Fail("Expected '=' after a key.");
            }

            Advance();
            SkipInlineWhitespace();
            object? value = ParseValue();
            Dictionary<string, object?> target = table;
            for (int index = 0; index < key.Length - 1; index++)
            {
                if (!target.TryGetValue(key[index], out object? existing))
                {
                    Dictionary<string, object?> next = new(StringComparer.Ordinal);
                    target[key[index]] = next;
                    target = next;
                }
                else if (existing is Dictionary<string, object?> nested)
                {
                    target = nested;
                }
                else
                {
                    throw Fail($"Dotted key '{string.Join('.', key)}' conflicts with an existing value.");
                }
            }

            if (target.ContainsKey(key[^1]))
            {
                throw Fail($"Duplicate key '{string.Join('.', key)}'.");
            }

            target[key[^1]] = value;
            RequireEndOfStatement();
        }

        private void RequireEndOfStatement()
        {
            SkipInlineWhitespace();
            if (Current == '#')
            {
                while (Current is not (null or '\n'))
                {
                    Advance();
                }
            }

            if (Current is not (null or '\n'))
            {
                if (Current == '\r' && Peek(1) == '\n')
                {
                    return;
                }

                throw Fail("Unexpected content after a statement.");
            }
        }

        private string[] ParseKey()
        {
            List<string> parts = [];
            while (true)
            {
                SkipInlineWhitespace();
                parts.Add(ParseKeyPart());
                SkipInlineWhitespace();
                if (Current != '.')
                {
                    break;
                }

                Advance();
            }

            return parts.ToArray();
        }

        private string ParseKeyPart()
        {
            return Current switch
            {
                '"' => ParseBasicString(),
                '\'' => ParseLiteralString(),
                { } character when char.IsLetterOrDigit(character) || character is '-' or '_' => ParseBareKey(),
                _ => throw Fail("Expected a key.")
            };
        }

        private string ParseBareKey()
        {
            int start = _position;
            while (Current is { } character && (char.IsLetterOrDigit(character) || character is '-' or '_'))
            {
                Advance();
            }

            return _text[start.._position];
        }

        private object? ParseValue()
        {
            return Current switch
            {
                '"' => ParseBasicString(),
                '\'' => ParseLiteralString(),
                '[' => ParseArray(),
                '{' => ParseInlineTable(),
                't' => ParseLiteral("true", true),
                'f' => ParseLiteral("false", false),
                'i' or 'n' => ParseSpecialFloat(),
                '+' or '-' => ParseNumber(),
                { } character when char.IsDigit(character) => ParseNumber(),
                _ => throw Fail(
                    "Unsupported or missing value (expected string, number, boolean, array or inline table).")
            };
        }

        private object ParseLiteral(string literal, object value)
        {
            foreach (char expected in literal)
            {
                if (Current != expected)
                {
                    throw Fail($"Invalid literal; expected '{literal}'.");
                }

                Advance();
            }

            return value;
        }

        private object ParseSpecialFloat()
        {
            if (Current == 'i')
            {
                return ParseLiteral("inf", double.PositiveInfinity);
            }

            return ParseLiteral("nan", double.NaN);
        }

        private string ParseBasicString()
        {
            if (Peek(1) == '"' && Peek(2) == '"')
            {
                throw Fail("Multi-line strings are not supported in plugin manifests.");
            }

            Advance(); // opening quote
            StringBuilder builder = new();
            while (true)
            {
                char? current = Current;
                switch (current)
                {
                    case null:
                        throw Fail("Unterminated string.");
                    case '\n':
                        throw Fail("Unterminated string on this line.");
                    case '"':
                        Advance();
                        return builder.ToString();
                    case '\\':
                        Advance();
                        builder.Append(ParseEscape());
                        break;
                    case < ' ' when current != '\t':
                        throw Fail("Control characters are not allowed in strings.");
                    default:
                        builder.Append(Advance());
                        break;
                }
            }
        }

        private char ParseEscape()
        {
            char? escaped = Current;
            Advance();
            return escaped switch
            {
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                'u' => ParseUnicodeEscape(4),
                'U' => ParseUnicodeEscape(8),
                _ => throw Fail($"Unsupported escape sequence '\\{escaped}'.")
            };
        }

        private char ParseUnicodeEscape(int digits)
        {
            if (_position + digits > _text.Length)
            {
                throw Fail("Truncated unicode escape.");
            }

            string hex = _text.Substring(_position, digits);
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint codepoint) ||
                codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF))
            {
                throw Fail("Invalid unicode escape.");
            }

            for (int index = 0; index < digits; index++)
            {
                Advance();
            }

            return (char)codepoint;
        }

        private string ParseLiteralString()
        {
            if (Peek(1) == '\'' && Peek(2) == '\'')
            {
                throw Fail("Multi-line strings are not supported in plugin manifests.");
            }

            Advance(); // opening quote
            int start = _position;
            while (true)
            {
                char? current = Current;
                switch (current)
                {
                    case null:
                    case '\n':
                        throw Fail("Unterminated literal string.");
                    case '\'':
                        string value = _text[start.._position];
                        Advance();
                        return value;
                    default:
                        Advance();
                        break;
                }
            }
        }

        private object ParseNumber()
        {
            int start = _position;
            if (Current is '+' or '-')
            {
                Advance();
            }

            if (Current == '0' && Peek(1) is 'x' or 'o' or 'b')
            {
                return ParseRadixInteger();
            }

            bool sawDigit = false;
            bool isFloat = false;
            while (Current is { } character)
            {
                if (char.IsDigit(character) || character == '_')
                {
                    sawDigit |= char.IsDigit(character);
                    Advance();
                }
                else if (character is '.' or 'e' or 'E' || (character is '+' or '-' &&
                                                            _text[_position - 1] is 'e' or 'E'))
                {
                    isFloat = true;
                    Advance();
                }
                else
                {
                    break;
                }
            }

            string token = _text[start.._position].Replace("_", "", StringComparison.Ordinal);
            if (!sawDigit)
            {
                throw Fail("Invalid number.");
            }

            if (isFloat)
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    throw Fail($"Invalid float '{token}'.");
                }

                return number;
            }

            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            {
                throw Fail($"Invalid integer '{token}'.");
            }

            return integer;
        }

        private object ParseRadixInteger()
        {
            int sign = 1;
            if (_position > 0 && _text[_position - 1] == '-')
            {
                sign = -1;
            }

            char marker = _text[_position + 1];
            Advance(); // '0'
            Advance(); // marker
            int digits = marker switch { 'x' => 16, 'o' => 8, _ => 2 };
            int start = _position;
            while (Current is { } character &&
                   (char.IsDigit(character) || (digits == 16 && char.IsAsciiHexDigit(character)) || character == '_'))
            {
                Advance();
            }

            string token = _text[start.._position].Replace("_", "", StringComparison.Ordinal);
            if (token.Length == 0)
            {
                throw Fail("Invalid integer literal.");
            }

            try
            {
                return sign * Convert.ToInt64(token, digits);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw Fail($"Invalid integer literal '0{marker}{token}'.");
            }
        }

        private object ParseArray()
        {
            Advance(); // '['
            List<object?> values = [];
            while (true)
            {
                SkipWhitespaceCommentsAndNewlines();
                if (Current == ']')
                {
                    Advance();
                    return values;
                }

                if (Current is null)
                {
                    throw Fail("Unterminated array.");
                }

                values.Add(ParseValue());
                SkipWhitespaceCommentsAndNewlines();
                if (Current == ',')
                {
                    Advance();
                }
                else if (Current != ']')
                {
                    throw Fail("Expected ',' or ']' in an array.");
                }
            }
        }

        private object ParseInlineTable()
        {
            Advance(); // '{'
            Dictionary<string, object?> table = new(StringComparer.Ordinal);
            SkipInlineWhitespace();
            if (Current == '}')
            {
                Advance();
                return table;
            }

            while (true)
            {
                SkipInlineWhitespace();
                string[] key = ParseKey();
                SkipInlineWhitespace();
                if (Current != '=')
                {
                    throw Fail("Expected '=' in an inline table.");
                }

                Advance();
                SkipInlineWhitespace();
                object? value = ParseValue();
                Dictionary<string, object?> target = table;
                for (int index = 0; index < key.Length - 1; index++)
                {
                    if (!target.TryGetValue(key[index], out object? existing))
                    {
                        Dictionary<string, object?> next = new(StringComparer.Ordinal);
                        target[key[index]] = next;
                        target = next;
                    }
                    else if (existing is Dictionary<string, object?> nested)
                    {
                        target = nested;
                    }
                    else
                    {
                        throw Fail($"Dotted key '{string.Join('.', key)}' conflicts with an existing value.");
                    }
                }

                if (target.ContainsKey(key[^1]))
                {
                    throw Fail($"Duplicate key '{string.Join('.', key)}'.");
                }

                target[key[^1]] = value;
                SkipInlineWhitespace();
                if (Current == ',')
                {
                    Advance();
                }
                else if (Current == '}')
                {
                    Advance();
                    return table;
                }
                else
                {
                    throw Fail("Expected ',' or '}' in an inline table.");
                }
            }
        }
    }
}
