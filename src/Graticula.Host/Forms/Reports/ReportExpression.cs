using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Graticula.Host.Forms.Reports;

/// <summary>Why a report template's syntax was refused: the reason alone; the place is added by the caller.</summary>
internal sealed class ReportSyntaxException : Exception
{
    public ReportSyntaxException(string message)
        : base(message)
    {
    }

    public ReportSyntaxException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ReportSyntaxException()
    {
    }
}

/// <summary>What one <c>${…}</c> placeholder says — ADR-301 §5.2.</summary>
internal enum ReportTagKind
{
    /// <summary><c>${expression | method:argument …}</c>: a value written where the placeholder stands.</summary>
    Value,

    /// <summary><c>${#name | method …}</c>: a section repeated once per row of a repeat, per file, or per attachment.</summary>
    Each,

    /// <summary><c>${if expression}</c>: a section kept when the expression is true.</summary>
    If,

    /// <summary><c>${/}</c>: the end of the nearest open section.</summary>
    Close,
}

/// <summary>One method after a pipe: <c>format:"DD/MM"</c>, <c>size:300:200</c>, <c>countSelected</c>.</summary>
internal sealed record ReportMethod(string Name, IReadOnlyList<object?> Arguments)
{
    public string? Text(int i) => i < Arguments.Count ? Arguments[i] as string ?? Convert.ToString(Arguments[i], CultureInfo.InvariantCulture) : null;

    public double? Number(int i) => i < Arguments.Count
        ? Arguments[i] switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) => p,
            _ => null,
        }
        : null;
}

/// <summary>A placeholder, read: its kind, its expression and its methods.</summary>
internal sealed record ReportTag(ReportTagKind Kind, string Text, ReportNode? Expression, IReadOnlyList<ReportMethod> Methods);

/// <summary>A node of a placeholder's expression.</summary>
internal abstract record ReportNode;

/// <summary>A number, a string, true, false or null.</summary>
internal sealed record ReportLiteral(object? Value) : ReportNode;

/// <summary>A name: a question, <c>repeat.question</c>, or a keyword (<c>$feature</c>, <c>$date</c>, <c>$file</c>, …).</summary>
internal sealed record ReportName(IReadOnlyList<string> Path) : ReportNode
{
    public string Dotted => string.Join('.', Path);
}

/// <summary><c>!x</c> or <c>-x</c>.</summary>
internal sealed record ReportUnary(string Operator, ReportNode Operand) : ReportNode;

/// <summary><c>a op b</c>.</summary>
internal sealed record ReportBinary(string Operator, ReportNode Left, ReportNode Right) : ReportNode;

/// <summary>
/// Reads Survey123's placeholder syntax — <c>${question | method:"argument"}</c>, <c>${#repeat}</c>, <c>${if …}</c>,
/// <c>${/}</c> — into a <see cref="ReportTag"/> (ADR-301 §5.2). Written here: Tier 1, the server's own template language.
/// </summary>
/// <remarks>
/// The expression grammar is the part Survey123's report pages show: names, dotted names, numbers, quoted strings,
/// <c>+ - * / %</c>, comparisons, <c>&amp;&amp; || !</c> and parentheses. A function call, an index (<c>$layers[0]</c>)
/// and anything else the pages do not show are refused with what was found, never read as something else.
/// </remarks>
internal static class ReportExpression
{
    /// <summary>Reads the text between <c>${</c> and <c>}</c>.</summary>
    /// <exception cref="ReportSyntaxException">The placeholder cannot be read.</exception>
    public static ReportTag Parse(string inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        string text = Normalise(inner).Trim();

        if (text.Length == 0)
        {
            throw new ReportSyntaxException("The placeholder is empty: write a question's name between ${ and }.");
        }

        if (text[0] == '/')
        {
            return new ReportTag(ReportTagKind.Close, text, null, []);
        }

        if (text.Contains("$summary", StringComparison.OrdinalIgnoreCase) || text.StartsWith('<'))
        {
            throw new ReportSyntaxException("A summary section is not supported yet: a report here is made of records, one or many, and a summary across them is not computed.");
        }

        ReportTagKind kind = ReportTagKind.Value;

        if (text[0] == '#')
        {
            kind = ReportTagKind.Each;
            text = text[1..].TrimStart();
        }
        else if (text.StartsWith("if ", StringComparison.Ordinal) || text.StartsWith("if(", StringComparison.Ordinal))
        {
            kind = ReportTagKind.If;
            text = text[2..].TrimStart();
        }

        List<string> parts = SplitPipes(text);
        ReportNode expression = new Parser(parts[0]).ParseAll();
        List<ReportMethod> methods = [];

        for (int i = 1; i < parts.Count; i++)
        {
            methods.Add(ParseMethod(parts[i]));
        }

        if (kind == ReportTagKind.If && methods.Count > 0)
        {
            throw new ReportSyntaxException($"An if takes a condition alone; '| {methods[0].Name}' after it is not read.");
        }

        if (kind == ReportTagKind.Each && expression is not ReportName)
        {
            throw new ReportSyntaxException("${#…} names a repeat, an image or file question, or $attachment — not an expression.");
        }

        return new ReportTag(kind, inner, expression, methods);
    }

    /// <summary>Word's typographic quotes, which its autocorrect puts in place of the ones typed, read as plain quotes.</summary>
    public static string Normalise(string text) => text
        .Replace('“', '"').Replace('”', '"').Replace('„', '"')
        .Replace('‘', '\'').Replace('’', '\'')
        .Replace(' ', ' ');

    /// <summary>Splits at each single <c>|</c> outside quotes; <c>||</c> is the expression's or.</summary>
    private static List<string> SplitPipes(string text)
    {
        List<string> parts = [];
        StringBuilder current = new();
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                current.Append(c);

                if (c == '\\' && i + 1 < text.Length)
                {
                    current.Append(text[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                current.Append(c);
            }
            else if (c == '|' && i + 1 < text.Length && text[i + 1] == '|')
            {
                current.Append("||");
                i++;
            }
            else if (c == '|')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (quote != '\0')
        {
            throw new ReportSyntaxException("A quoted text in the placeholder is never closed.");
        }

        parts.Add(current.ToString().Trim());

        if (parts[0].Length == 0)
        {
            throw new ReportSyntaxException("The placeholder names nothing before its first '|'.");
        }

        if (parts.Exists(p => p.Length == 0))
        {
            throw new ReportSyntaxException("A '|' in the placeholder is followed by no method.");
        }

        return parts;
    }

    /// <summary>Reads <c>name</c> or <c>name:argument:argument</c>.</summary>
    private static ReportMethod ParseMethod(string text)
    {
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        string name = (colon < 0 ? text : text[..colon]).Trim();

        if (name.Length == 0 || !IsName(name))
        {
            throw new ReportSyntaxException($"'{text}' is not a method: a method is a name, as in '| format:\"DD/MM\"'.");
        }

        List<object?> arguments = [];

        if (colon >= 0)
        {
            string rest = text[(colon + 1)..];
            int i = 0;

            while (true)
            {
                while (i < rest.Length && rest[i] == ' ')
                {
                    i++;
                }

                if (i >= rest.Length)
                {
                    arguments.Add(string.Empty);
                    break;
                }

                if (rest[i] is '"' or '\'')
                {
                    char quote = rest[i];
                    StringBuilder value = new();
                    i++;

                    while (i < rest.Length && rest[i] != quote)
                    {
                        if (rest[i] == '\\' && i + 1 < rest.Length)
                        {
                            i++;
                        }

                        value.Append(rest[i++]);
                    }

                    if (i >= rest.Length)
                    {
                        throw new ReportSyntaxException($"The argument of '{name}' opens a quote it never closes.");
                    }

                    i++;
                    arguments.Add(value.ToString());
                }
                else
                {
                    int end = rest.IndexOf(':', i);
                    string raw = (end < 0 ? rest[i..] : rest[i..end]).Trim();
                    i = end < 0 ? rest.Length : end;

                    arguments.Add(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n
                        : raw is "true" ? true : raw is "false" ? false : (object)raw);

                    if (end < 0)
                    {
                        break;
                    }
                }

                while (i < rest.Length && rest[i] == ' ')
                {
                    i++;
                }

                if (i >= rest.Length)
                {
                    break;
                }

                if (rest[i] != ':')
                {
                    throw new ReportSyntaxException($"The arguments of '{name}' are separated by ':'; '{rest[i..]}' is not read.");
                }

                i++;
            }
        }

        return new ReportMethod(name, arguments);
    }

    private static bool IsName(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A recursive-descent reader of one expression.</summary>
    private sealed class Parser(string text)
    {
        private int _at;

        public ReportNode ParseAll()
        {
            ReportNode node = Or();
            Skip();

            if (_at < text.Length)
            {
                throw new ReportSyntaxException(text[_at] is '(' or '['
                    ? $"'{text}' calls a function or reads an index, which a report here does not: it reads question names, numbers, texts, + - * / %, comparisons, && || and !."
                    : $"'{text[_at..]}' in '{text}' is not read: the expression ends before it.");
            }

            return node;
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }

        private bool Take(string symbol)
        {
            Skip();

            if (string.CompareOrdinal(text, _at, symbol, 0, symbol.Length) == 0)
            {
                // '=' alone is not '==', and '<' is not '<='.
                _at += symbol.Length;
                return true;
            }

            return false;
        }

        private ReportNode Or()
        {
            ReportNode left = And();

            while (Take("||"))
            {
                left = new ReportBinary("||", left, And());
            }

            return left;
        }

        private ReportNode And()
        {
            ReportNode left = Equality();

            while (Take("&&"))
            {
                left = new ReportBinary("&&", left, Equality());
            }

            return left;
        }

        private ReportNode Equality()
        {
            ReportNode left = Relation();

            while (true)
            {
                string? op = Take("===") ? "==" : Take("!==") ? "!=" : Take("==") ? "==" : Take("!=") ? "!=" : null;

                if (op is null)
                {
                    Skip();

                    if (_at < text.Length && text[_at] == '=')
                    {
                        throw new ReportSyntaxException($"'{text}' compares with a single '='; write '==' to compare.");
                    }

                    return left;
                }

                left = new ReportBinary(op, left, Relation());
            }
        }

        private ReportNode Relation()
        {
            ReportNode left = Additive();

            while (true)
            {
                string? op = Take("<=") ? "<=" : Take(">=") ? ">=" : Take("<") ? "<" : Take(">") ? ">" : null;

                if (op is null)
                {
                    return left;
                }

                left = new ReportBinary(op, left, Additive());
            }
        }

        private ReportNode Additive()
        {
            ReportNode left = Multiplicative();

            while (true)
            {
                string? op = Take("+") ? "+" : Take("-") ? "-" : null;

                if (op is null)
                {
                    return left;
                }

                left = new ReportBinary(op, left, Multiplicative());
            }
        }

        private ReportNode Multiplicative()
        {
            ReportNode left = Unary();

            while (true)
            {
                string? op = Take("*") ? "*" : Take("/") ? "/" : Take("%") ? "%" : null;

                if (op is null)
                {
                    return left;
                }

                left = new ReportBinary(op, left, Unary());
            }
        }

        private ReportNode Unary()
        {
            Skip();

            if (_at < text.Length && text[_at] == '!' && !(_at + 1 < text.Length && text[_at + 1] == '='))
            {
                _at++;
                return new ReportUnary("!", Unary());
            }

            if (Take("-"))
            {
                return new ReportUnary("-", Unary());
            }

            return Primary();
        }

        private ReportNode Primary()
        {
            Skip();

            if (_at >= text.Length)
            {
                throw new ReportSyntaxException($"'{text}' ends where a value was expected.");
            }

            char c = text[_at];

            if (c == '(')
            {
                _at++;
                ReportNode inner = Or();

                if (!Take(")"))
                {
                    throw new ReportSyntaxException($"'{text}' opens a bracket it never closes.");
                }

                return inner;
            }

            if (c is '"' or '\'')
            {
                StringBuilder value = new();
                _at++;

                while (_at < text.Length && text[_at] != c)
                {
                    if (text[_at] == '\\' && _at + 1 < text.Length)
                    {
                        _at++;
                    }

                    value.Append(text[_at++]);
                }

                if (_at >= text.Length)
                {
                    throw new ReportSyntaxException($"'{text}' opens a quote it never closes.");
                }

                _at++;
                return new ReportLiteral(value.ToString());
            }

            if (char.IsDigit(c) || (c == '.' && _at + 1 < text.Length && char.IsDigit(text[_at + 1])))
            {
                int start = _at;

                while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.'))
                {
                    _at++;
                }

                if (_at < text.Length && text[_at] is 'e' or 'E')
                {
                    _at++;

                    if (_at < text.Length && text[_at] is '+' or '-')
                    {
                        _at++;
                    }

                    while (_at < text.Length && char.IsDigit(text[_at]))
                    {
                        _at++;
                    }
                }

                string raw = text[start.._at];
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    ? new ReportLiteral(n)
                    : throw new ReportSyntaxException($"'{raw}' is not a number.");
            }

            if (char.IsLetter(c) || c is '_' or '$')
            {
                List<string> path = [];

                while (true)
                {
                    int start = _at;

                    if (_at < text.Length && text[_at] == '$')
                    {
                        _at++;
                    }

                    // A '-' is subtraction: a question's name becomes a column, and a column has none.
                    while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_'))
                    {
                        _at++;
                    }

                    string segment = text[start.._at];

                    if (segment.Length == 0 || segment == "$")
                    {
                        throw new ReportSyntaxException($"'{text}' has a '.' or '$' with no name after it.");
                    }

                    path.Add(segment);

                    if (_at < text.Length && text[_at] == '.')
                    {
                        _at++;
                        continue;
                    }

                    break;
                }

                if (path.Count == 1)
                {
                    switch (path[0])
                    {
                        case "true":
                            return new ReportLiteral(true);
                        case "false":
                            return new ReportLiteral(false);
                        case "null":
                            return new ReportLiteral(null);
                        default:
                            break;
                    }
                }

                return new ReportName(path);
            }

            throw new ReportSyntaxException($"'{text[_at..]}' in '{text}' is not a value: a value is a question's name, a number or a quoted text.");
        }
    }
}
