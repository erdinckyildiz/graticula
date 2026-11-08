using System;
using System.Collections.Generic;

namespace Graticula.Host.Forms;

/// <summary>
/// Finds what an XLSForm expression calls and refers to, without evaluating it — ADR-287 §5.9 (i), §5.10.
/// </summary>
/// <remarks>
/// <para>
/// <b>A scanner, not an evaluator.</b> The server does not run a form's logic: that is the client's, and a second
/// evaluator here would drift from it (§5.9 (i)). What the server must still do is refuse, at upload, a form whose
/// expressions call a function the client does not have or refer to a question that does not exist (condition 5). That
/// needs only the tokens: names followed by <c>(</c>, <c>${…}</c> references, and string literals skipped so that a
/// quoted <c>'if('</c> is not a call.
/// </para>
/// </remarks>
public static class FormExpressions
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "and", "or", "div", "mod" };

    /// <summary>Scans an expression.</summary>
    public static ExpressionScan Scan(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        List<FunctionCall> calls = [];
        List<string> references = [];
        string? problem = null;
        int i = 0;

        while (i < expression.Length && problem is null)
        {
            char c = expression[i];

            if (c is '\'' or '"')
            {
                int end = expression.IndexOf(c, i + 1);

                if (end < 0)
                {
                    problem = $"a string starting at character {i + 1} is never closed";
                    break;
                }

                i = end + 1;
            }
            else if (c == '$' && i + 1 < expression.Length && expression[i + 1] == '{')
            {
                int end = expression.IndexOf('}', i + 2);

                if (end < 0)
                {
                    problem = $"the reference starting at character {i + 1} has no closing '}}'";
                    break;
                }

                string name = expression[(i + 2)..end].Trim();

                if (name.Length == 0)
                {
                    problem = $"the reference at character {i + 1} names no question";
                    break;
                }

                references.Add(name);
                i = end + 1;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i;

                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] is '_' or '-' or '.' or ':'))
                {
                    i++;
                }

                string word = expression[start..i];
                int after = i;

                while (after < expression.Length && char.IsWhiteSpace(expression[after]))
                {
                    after++;
                }

                if (after < expression.Length && expression[after] == '(' && !Operators.Contains(word))
                {
                    calls.Add(new FunctionCall(word, FirstLiteral(expression, after + 1)));
                }
            }
            else if (char.IsDigit(c))
            {
                while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.'))
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        if (problem is null)
        {
            problem = Balance(expression);
        }

        return new ExpressionScan(calls, references, problem);
    }

    /// <summary>The string literal a call's first argument is, if it is one: <c>pulldata('@layer', …)</c> gives <c>@layer</c>.</summary>
    private static string? FirstLiteral(string expression, int from)
    {
        int i = from;

        while (i < expression.Length && char.IsWhiteSpace(expression[i]))
        {
            i++;
        }

        if (i >= expression.Length || expression[i] is not ('\'' or '"'))
        {
            return null;
        }

        int end = expression.IndexOf(expression[i], i + 1);

        return end < 0 ? null : expression[(i + 1)..end];
    }

    private static string? Balance(string expression)
    {
        int depth = 0;
        char quote = '\0';

        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth < 0)
            {
                return $"the ')' at character {i + 1} closes nothing";
            }
        }

        return depth > 0 ? $"{depth} '(' {(depth == 1 ? "is" : "are")} never closed" : null;
    }
}

/// <summary>What an expression calls and refers to, and the first thing wrong with how it is written, if any.</summary>
public sealed record ExpressionScan(IReadOnlyList<FunctionCall> Calls, IReadOnlyList<string> References, string? Problem);

/// <summary>A call: the function's name, and its first argument when that is a string literal.</summary>
public sealed record FunctionCall(string Name, string? FirstLiteral);
