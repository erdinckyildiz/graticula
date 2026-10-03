using System;
using System.Collections.Generic;
using System.Globalization;

namespace Graticula.Coverages;

/// <summary>
/// An arithmetic expression over an image's bands, as ArcGIS's <c>BandArithmetic</c> raster function takes it with
/// <c>Method</c> 0: <c>(B4 - B3) / (B4 + B3)</c> — ADR-151.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bands are numbered from one</b>, as ArcGIS numbers them in this expression, while the band IDs of <c>NDVI</c> and
/// <c>ExtractBand</c> are numbered from zero. Both are ArcGIS's choice, and each is read as ArcGIS reads it.
/// </para>
/// <para>
/// <b>Numbers, <c>B</c>n, the four operations, unary minus and parentheses — nothing else.</b> A name this reads as
/// neither is refused, naming it, rather than read as zero. A pixel where any band it uses is absent, or where it
/// divides by zero, has no value: NaN.
/// </para>
/// </remarks>
public sealed class BandExpression
{
    private readonly Node _root;

    private BandExpression(string text, Node root, int highest)
    {
        Text = text;
        _root = root;
        HighestBand = highest;
    }

    /// <summary>The expression as written.</summary>
    public string Text { get; }

    /// <summary>The highest band it names, from one.</summary>
    public int HighestBand { get; }

    /// <summary>Reads an expression.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="expression">The expression, when it read.</param>
    /// <param name="error">Why it did not.</param>
    /// <returns>Whether it read.</returns>
    public static bool TryParse(string? text, out BandExpression? expression, out string? error)
    {
        expression = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "BandArithmetic's `BandIndexes` is an expression over the bands, such as (B4 - B3) / (B4 + B3).";
            return false;
        }

        try
        {
            Parser parser = new(text);
            Node root = parser.Sum();
            parser.End();

            if (parser.Highest == 0)
            {
                throw new FormatException("it names no band; bands are B1, B2 and so on.");
            }

            expression = new BandExpression(text.Trim(), root, parser.Highest);
            return true;
        }
        catch (FormatException why)
        {
            error = $"Could not read '{text.Trim()}': {why.Message}";
            return false;
        }
    }

    /// <summary>The expression's value at one pixel.</summary>
    /// <param name="band">Each band's value at the pixel, by its index from zero; NaN where it is absent.</param>
    /// <returns>The value, NaN where there is none.</returns>
    public double Evaluate(Func<int, double> band)
    {
        ArgumentNullException.ThrowIfNull(band);
        double value = _root.Evaluate(band);
        return double.IsFinite(value) ? value : double.NaN;
    }

    private abstract class Node
    {
        public abstract double Evaluate(Func<int, double> band);
    }

    private sealed class Number(double value) : Node
    {
        public override double Evaluate(Func<int, double> band) => value;
    }

    private sealed class Band(int index) : Node
    {
        public override double Evaluate(Func<int, double> band) => band(index);
    }

    private sealed class Negate(Node inner) : Node
    {
        public override double Evaluate(Func<int, double> band) => -inner.Evaluate(band);
    }

    private sealed class Binary(char op, Node left, Node right) : Node
    {
        public override double Evaluate(Func<int, double> band)
        {
            double a = left.Evaluate(band), b = right.Evaluate(band);

            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                _ => b == 0 ? double.NaN : a / b,
            };
        }
    }

    private sealed class Parser(string text)
    {
        private int _at;

        public int Highest { get; private set; }

        public void End()
        {
            Skip();

            if (_at < text.Length)
            {
                throw new FormatException($"'{text[_at..].Trim()}' is left over.");
            }
        }

        public Node Sum()
        {
            Node left = Product();

            while (Next() is '+' or '-')
            {
                char op = text[_at++];
                left = new Binary(op, left, Product());
            }

            return left;
        }

        private Node Product()
        {
            Node left = Unary();

            while (Next() is '*' or '/')
            {
                char op = text[_at++];
                left = new Binary(op, left, Unary());
            }

            return left;
        }

        private Node Unary()
        {
            if (Next() == '-')
            {
                _at++;
                return new Negate(Unary());
            }

            if (Next() == '+')
            {
                _at++;
                return Unary();
            }

            return Atom();
        }

        private Node Atom()
        {
            char c = Next();

            if (c == '(')
            {
                _at++;
                Node inner = Sum();

                if (Next() != ')')
                {
                    throw new FormatException("a parenthesis is not closed.");
                }

                _at++;
                return inner;
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = _at;

                while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.'))
                {
                    _at++;
                }

                return double.TryParse(text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? new Number(value)
                    : throw new FormatException($"'{text[start.._at]}' is not a number.");
            }

            if (char.IsLetter(c))
            {
                int start = _at;

                while (_at < text.Length && char.IsLetterOrDigit(text[_at]))
                {
                    _at++;
                }

                string word = text[start.._at];

                if (word.Length > 1 && (word[0] is 'B' or 'b')
                    && int.TryParse(word.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1)
                {
                    Highest = Math.Max(Highest, number);
                    return new Band(number - 1);
                }

                throw new FormatException($"'{word}' is not a number or a band (B1, B2 and so on).");
            }

            throw new FormatException(_at >= text.Length ? "it ends where a value was expected." : $"'{c}' is not understood here.");
        }

        private char Next()
        {
            Skip();
            return _at < text.Length ? text[_at] : '\0';
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }
    }
}
