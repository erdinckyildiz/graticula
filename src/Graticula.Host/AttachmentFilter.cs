using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Graticula.Features;

namespace Graticula.Host;

/// <summary>
/// The <c>attachmentsDefinitionExpression</c> of <c>queryAttachments</c>: a SQL-92 filter over an attachment's own
/// properties, evaluated in memory against the attachments of the features already chosen (ADR-309 §1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why in memory.</b> The attachment stores answer one feature's list at a time and
/// <c>queryAttachments</c> already reads them that way, so a filter on the list is the same cost as the
/// <c>attachmentTypes</c> and <c>keywords</c> filters that sit beside it, and it cannot disagree with them.
/// </para>
/// <para>
/// <b>What is accepted.</b> Comparisons (<c>= &lt;&gt; != &lt; &gt; &lt;= &gt;=</c>), <c>LIKE</c>, <c>IN</c>,
/// <c>BETWEEN</c>, <c>IS [NOT] NULL</c>, <c>NOT</c>, <c>AND</c>, <c>OR</c> and parentheses, over the properties an
/// attachment has: the field names ArcGIS's reference lists (<c>ATTACHMENTID</c>, <c>ATT_NAME</c>, <c>CONTENT_TYPE</c>,
/// <c>DATA_SIZE</c>, <c>KEYWORDS</c>) and the property names <c>attachmentProperties</c> advertises (<c>id</c>,
/// <c>name</c>, <c>contentType</c>, <c>size</c>, <c>keywords</c>). Anything else is refused by name, which is the
/// ADR-008 §2 rule: an expression that is read as something other than what was written answers a different question.
/// </para>
/// </remarks>
internal sealed class AttachmentFilter
{
    private readonly Node _root;

    private AttachmentFilter(Node root) => _root = root;

    /// <summary>The attachment properties an expression may name, by every spelling.</summary>
    private static readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ATTACHMENTID"] = "id", ["id"] = "id",
        ["ATT_NAME"] = "name", ["name"] = "name",
        ["CONTENT_TYPE"] = "contentType", ["contentType"] = "contentType",
        ["DATA_SIZE"] = "size", ["size"] = "size",
        ["KEYWORDS"] = "keywords",
    };

    /// <summary>Parses an expression, or explains why not.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="error">Why it could not be read.</param>
    /// <returns>Whether it could be.</returns>
    public static bool TryParse(string text, out AttachmentFilter? filter, out string? error)
    {
        filter = null;
        error = null;

        try
        {
            Parser parser = new(Tokenise(text));
            Node root = parser.Expression();
            parser.End();
            filter = new AttachmentFilter(root);
            return true;
        }
        catch (FormatException fault)
        {
            error = $"'attachmentsDefinitionExpression' could not be read: {fault.Message} "
                + "The fields are ATTACHMENTID, ATT_NAME, CONTENT_TYPE, DATA_SIZE and KEYWORDS.";
            return false;
        }
    }

    /// <summary>Whether an attachment satisfies the expression.</summary>
    /// <param name="attachment">The attachment.</param>
    /// <returns>Whether it does.</returns>
    public bool Matches(AttachmentInfo attachment) => _root.Test(attachment);

    // ---------- evaluation ----------

    private static object? ValueOf(AttachmentInfo a, string property) => property switch
    {
        "id" => (double)a.Id,
        "name" => a.Name,
        "contentType" => a.ContentType,
        "size" => (double)a.Size,
        "keywords" => a.Keywords.Length == 0 ? null : a.Keywords,
        _ => null,
    };

    private abstract class Node
    {
        public abstract bool Test(AttachmentInfo a);
    }

    private sealed class Logical(bool and, Node left, Node right) : Node
    {
        public override bool Test(AttachmentInfo a) => and ? left.Test(a) && right.Test(a) : left.Test(a) || right.Test(a);
    }

    private sealed class Negation(Node inner) : Node
    {
        public override bool Test(AttachmentInfo a) => !inner.Test(a);
    }

    private sealed class Predicate(string property, Func<object?, bool> test) : Node
    {
        public override bool Test(AttachmentInfo a) => test(ValueOf(a, property));
    }

    // ---------- parsing ----------

    private enum Kind { Word, Number, Text, Symbol, End }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenise(string text)
    {
        List<Token> tokens = [];
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '\'')
            {
                StringBuilder literal = new();
                i++;

                while (true)
                {
                    if (i >= text.Length) { throw new FormatException("A string literal is not closed."); }

                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { literal.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }

                    literal.Append(text[i++]);
                }

                tokens.Add(new Token(Kind.Text, literal.ToString()));
            }
            else if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int start = i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) { i++; }
                tokens.Add(new Token(Kind.Number, text[start..i]));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) { i++; }
                tokens.Add(new Token(Kind.Word, text[start..i]));
            }
            else if ((c == '<' || c == '>' || c == '!') && i + 1 < text.Length && (text[i + 1] == '=' || (c == '<' && text[i + 1] == '>')))
            {
                tokens.Add(new Token(Kind.Symbol, text.Substring(i, 2)));
                i += 2;
            }
            else if ("=<>(),".Contains(c, StringComparison.Ordinal))
            {
                tokens.Add(new Token(Kind.Symbol, c.ToString()));
                i++;
            }
            else
            {
                throw new FormatException($"The character '{c}' is not part of the supported grammar.");
            }
        }

        tokens.Add(new Token(Kind.End, string.Empty));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _at;

        private Token Peek => tokens[_at];

        private bool Word(string word) =>
            Peek.Kind == Kind.Word && string.Equals(Peek.Text, word, StringComparison.OrdinalIgnoreCase);

        private bool Symbol(string symbol) => Peek.Kind == Kind.Symbol && Peek.Text == symbol;

        public void End()
        {
            if (Peek.Kind != Kind.End) { throw new FormatException($"Unexpected '{Peek.Text}'."); }
        }

        public Node Expression()
        {
            Node left = Conjunction();

            while (Word("OR")) { _at++; left = new Logical(false, left, Conjunction()); }

            return left;
        }

        private Node Conjunction()
        {
            Node left = Unary();

            while (Word("AND")) { _at++; left = new Logical(true, left, Unary()); }

            return left;
        }

        private Node Unary()
        {
            if (Word("NOT")) { _at++; return new Negation(Unary()); }

            if (Symbol("("))
            {
                _at++;
                Node inner = Expression();
                if (!Symbol(")")) { throw new FormatException("A parenthesis is not closed."); }
                _at++;
                return inner;
            }

            return Comparison();
        }

        private Predicate Comparison()
        {
            if (Peek.Kind != Kind.Word) { throw new FormatException($"A field name was expected, found '{Peek.Text}'."); }

            if (!Fields.TryGetValue(Peek.Text, out string? property))
            {
                throw new FormatException($"'{Peek.Text}' is not an attachment field.");
            }

            _at++;
            bool negated = false;

            if (Word("IS"))
            {
                _at++;
                if (Word("NOT")) { _at++; negated = true; }
                if (!Word("NULL")) { throw new FormatException("NULL was expected after IS."); }
                _at++;
                return new Predicate(property, v => negated ? v is not null : v is null);
            }

            if (Word("NOT")) { _at++; negated = true; }

            if (Word("LIKE"))
            {
                _at++;
                string pattern = TextLiteral();
                Regex regex = new(
                    "^" + Regex.Escape(pattern).Replace("%", ".*", StringComparison.Ordinal).Replace("_", ".", StringComparison.Ordinal) + "$",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
                return new Predicate(property, v => v is not null && (regex.IsMatch(Show(v)) != negated));
            }

            if (Word("IN"))
            {
                _at++;
                if (!Symbol("(")) { throw new FormatException("A '(' was expected after IN."); }
                _at++;
                List<object> members = [];

                do
                {
                    if (Symbol(",")) { _at++; }
                    members.Add(Literal());
                }
                while (Symbol(","));

                if (!Symbol(")")) { throw new FormatException("A ')' was expected to close IN."); }
                _at++;
                return new Predicate(property, v => v is not null && (members.Exists(m => Compare(v, m) == 0) != negated));
            }

            if (Word("BETWEEN"))
            {
                _at++;
                object low = Literal();
                if (!Word("AND")) { throw new FormatException("AND was expected in BETWEEN."); }
                _at++;
                object high = Literal();
                return new Predicate(property, v => v is not null && ((Compare(v, low) >= 0 && Compare(v, high) <= 0) != negated));
            }

            if (negated) { throw new FormatException("NOT must be followed by LIKE, IN or BETWEEN here."); }

            if (Peek.Kind != Kind.Symbol || Peek.Text is "(" or ")" or ",")
            {
                throw new FormatException($"A comparison operator was expected after '{property}'.");
            }

            string op = Peek.Text;
            _at++;
            object operand = Literal();

            return new Predicate(property, v =>
            {
                if (v is null) { return false; }

                int order = Compare(v, operand);
                return op switch
                {
                    "=" => order == 0,
                    "<>" or "!=" => order != 0,
                    "<" => order < 0,
                    ">" => order > 0,
                    "<=" => order <= 0,
                    ">=" => order >= 0,
                    _ => false,
                };
            });
        }

        private string TextLiteral()
        {
            if (Peek.Kind != Kind.Text) { throw new FormatException("A quoted string was expected."); }
            return tokens[_at++].Text;
        }

        private object Literal()
        {
            Token t = tokens[_at];

            if (t.Kind == Kind.Text) { _at++; return t.Text; }

            if (t.Kind == Kind.Number
                && double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                _at++;
                return number;
            }

            throw new FormatException($"A literal was expected, found '{t.Text}'.");
        }

        private static string Show(object v) => v is double d ? d.ToString(CultureInfo.InvariantCulture) : (string)v;

        /// <summary>Orders a property against a literal: numbers numerically, anything else as text without regard to case.</summary>
        private static int Compare(object property, object literal)
        {
            if (property is double p && literal is double l) { return p.CompareTo(l); }

            return string.Compare(Show(property), Show(literal), StringComparison.OrdinalIgnoreCase);
        }
    }
}
