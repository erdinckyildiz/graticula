using System.Collections.Generic;

namespace Graticula.Features;

/// <summary>What a scalar expression yields, as far as the parser can tell.</summary>
/// <remarks>
/// <b>Coarse on purpose.</b> The kinds are the ones a function or an operator cares about — a
/// <c>SUBSTRING</c> needs text, an <c>EXTRACT</c> needs a date, a division of two integers truncates —
/// and not the column's storage type. <see cref="Unknown"/> is what a caller that gave no column types
/// gets for every column, and every check below lets it through.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "It names SQL types, as FieldType does, and the names are what SQL calls them.")]
public enum ExpressionKind
{
    /// <summary>Not known: no column types were given, or the value is null.</summary>
    Unknown,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number with a fraction.</summary>
    Real,

    /// <summary>Text.</summary>
    Text,

    /// <summary>A date or a moment.</summary>
    Date,

    /// <summary>True or false.</summary>
    Boolean,
}

/// <summary>The arithmetic the grammar evaluates.</summary>
public enum ArithmeticOperator
{
    /// <summary><c>+</c>.</summary>
    Add,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c>, which truncates when both sides are integers.</summary>
    Divide,
}

/// <summary>The functions of ArcGIS's standardized query list that the grammar evaluates.</summary>
public enum ScalarFunction
{
    /// <summary><c>ABS(n)</c>.</summary>
    Abs,

    /// <summary><c>CEILING(n)</c>.</summary>
    Ceiling,

    /// <summary><c>FLOOR(n)</c>.</summary>
    Floor,

    /// <summary><c>COS(n)</c>, radians.</summary>
    Cos,

    /// <summary><c>SIN(n)</c>, radians.</summary>
    Sin,

    /// <summary><c>TAN(n)</c>, radians.</summary>
    Tan,

    /// <summary><c>LOG(n)</c>, the natural logarithm, as ArcGIS defines it.</summary>
    Log,

    /// <summary><c>LOG10(n)</c>.</summary>
    Log10,

    /// <summary><c>POWER(n, y)</c>.</summary>
    Power,

    /// <summary><c>ROUND(n)</c> or <c>ROUND(n, places)</c>.</summary>
    Round,

    /// <summary><c>TRUNCATE(n)</c> or <c>TRUNCATE(n, places)</c>.</summary>
    Truncate,

    /// <summary><c>MOD(n, m)</c>.</summary>
    Mod,

    /// <summary><c>NULLIF(a, b)</c>.</summary>
    NullIf,

    /// <summary><c>COALESCE(a, b, …)</c>.</summary>
    Coalesce,

    /// <summary><c>CHAR_LENGTH(s)</c>.</summary>
    CharLength,

    /// <summary><c>CONCAT(a, b)</c>.</summary>
    Concat,

    /// <summary><c>POSITION(needle, haystack)</c> or <c>POSITION(needle IN haystack)</c>.</summary>
    Position,

    /// <summary><c>SUBSTRING(s, start[, length])</c> or <c>SUBSTRING(s FROM start [FOR length])</c>.</summary>
    Substring,

    /// <summary><c>UPPER(s)</c> over anything but a bare column, which keeps its own path.</summary>
    Upper,

    /// <summary><c>LOWER(s)</c> over anything but a bare column.</summary>
    Lower,
}

/// <summary>What a <c>CAST</c> converts to.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "It names SQL types, as FieldType does, and the names are what SQL calls them.")]
public enum CastTarget
{
    /// <summary><c>INT</c>, <c>INTEGER</c>.</summary>
    Integer,

    /// <summary><c>SMALLINT</c>.</summary>
    SmallInteger,

    /// <summary><c>BIGINT</c>.</summary>
    BigInteger,

    /// <summary><c>FLOAT</c>, <c>DOUBLE</c>, <c>DOUBLE PRECISION</c> — eight bytes, as ArcGIS means by FLOAT.</summary>
    Double,

    /// <summary><c>REAL</c> — four bytes.</summary>
    Real,

    /// <summary><c>VARCHAR</c>, <c>CHAR</c>, <c>TEXT</c>, with an optional length.</summary>
    Text,

    /// <summary><c>DATE</c>.</summary>
    Date,
}

/// <summary>A part of a date, for <c>EXTRACT</c>.</summary>
public enum DatePart
{
    /// <summary>The year.</summary>
    Year,

    /// <summary>The month, 1 to 12.</summary>
    Month,

    /// <summary>The day of the month.</summary>
    Day,

    /// <summary>The hour, 0 to 23, in UTC.</summary>
    Hour,

    /// <summary>The minute.</summary>
    Minute,

    /// <summary>The second, with its fraction.</summary>
    Second,
}

/// <summary>Which end <c>TRIM</c> takes spaces from.</summary>
public enum TrimSide
{
    /// <summary>Both ends — also what <c>TRIM(s)</c> means.</summary>
    Both,

    /// <summary>The start.</summary>
    Leading,

    /// <summary>The end.</summary>
    Trailing,
}

/// <summary>
/// A value computed from a row: a column, a constant, or a function, cast or arithmetic over them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second half of the tree, added 2026-09-23 for V-75</b> — ArcGIS's standardized query functions,
/// by the owner's decision ([ADR-083](../../../docs/adr/ADR-083-the-where-clause-evaluates-arcgis-standard-functions.md)).
/// Until then a predicate compared one column with one literal, and that is still how every predicate is
/// built when it can be: an expression node appears only where a function, a cast or arithmetic was written,
/// so every tree the other front ends build is unchanged.
/// </para>
/// <para>
/// <b>Nothing here is text a caller wrote.</b> A column is a name already matched against the layer; a
/// constant is bound as a parameter; a function is a member of <see cref="ScalarFunction"/> and its
/// spelling is <see cref="PredicateSql"/>'s, per dialect.
/// </para>
/// </remarks>
public abstract record ScalarExpression
{
    private protected ScalarExpression()
    {
    }

    /// <summary>What the expression yields.</summary>
    public abstract ExpressionKind Kind { get; }

    /// <summary>A column's value.</summary>
    /// <param name="Column">The column, as the layer names it.</param>
    /// <param name="ColumnKind">What it holds.</param>
    public sealed record ColumnValue(string Column, ExpressionKind ColumnKind) : ScalarExpression
    {
        /// <inheritdoc />
        public override ExpressionKind Kind => ColumnKind;
    }

    /// <summary>A constant, bound as a parameter.</summary>
    /// <param name="Value">The value: a string, a long, a double, a bool, a moment, an int, or null.</param>
    public sealed record Constant(object? Value) : ScalarExpression
    {
        /// <inheritdoc />
        public override ExpressionKind Kind => Value switch
        {
            string => ExpressionKind.Text,
            long or int => ExpressionKind.Integer,
            double => ExpressionKind.Real,
            bool => ExpressionKind.Boolean,
            System.DateTimeOffset => ExpressionKind.Date,
            _ => ExpressionKind.Unknown,
        };
    }

    /// <summary><c>left op right</c>.</summary>
    /// <param name="Operator">The operator.</param>
    /// <param name="Left">The left side.</param>
    /// <param name="Right">The right side.</param>
    public sealed record Arithmetic(ArithmeticOperator Operator, ScalarExpression Left, ScalarExpression Right)
        : ScalarExpression
    {
        /// <summary>Whether both sides are known to be whole numbers, so a division truncates.</summary>
        public bool Integral => Left.Kind == ExpressionKind.Integer && Right.Kind == ExpressionKind.Integer;

        /// <inheritdoc />
        public override ExpressionKind Kind =>
            Integral ? ExpressionKind.Integer
            : Left.Kind == ExpressionKind.Unknown || Right.Kind == ExpressionKind.Unknown ? ExpressionKind.Unknown
            : ExpressionKind.Real;
    }

    /// <summary><c>-operand</c>.</summary>
    /// <param name="Operand">The value negated.</param>
    public sealed record Negated(ScalarExpression Operand) : ScalarExpression
    {
        /// <inheritdoc />
        public override ExpressionKind Kind => Operand.Kind;
    }

    /// <summary>A function of the standardized list.</summary>
    /// <param name="Function">Which.</param>
    /// <param name="Arguments">Its arguments, in the order the function takes them.</param>
    public sealed record FunctionCall(ScalarFunction Function, IReadOnlyList<ScalarExpression> Arguments) : ScalarExpression
    {
        /// <inheritdoc />
        public override ExpressionKind Kind => Function switch
        {
            ScalarFunction.CharLength or ScalarFunction.Position => ExpressionKind.Integer,
            ScalarFunction.Concat or ScalarFunction.Substring or ScalarFunction.Upper or ScalarFunction.Lower
                => ExpressionKind.Text,
            ScalarFunction.Abs or ScalarFunction.NullIf or ScalarFunction.Coalesce => Arguments[0].Kind,
            ScalarFunction.Ceiling or ScalarFunction.Floor
                => Arguments[0].Kind == ExpressionKind.Integer ? ExpressionKind.Integer : Arguments[0].Kind,
            ScalarFunction.Mod => Arguments[0].Kind == ExpressionKind.Integer && Arguments[1].Kind == ExpressionKind.Integer
                ? ExpressionKind.Integer
                : ExpressionKind.Real,
            _ => ExpressionKind.Real,
        };
    }

    /// <summary><c>CAST(operand AS target)</c>.</summary>
    /// <param name="Operand">The value converted.</param>
    /// <param name="Target">What to.</param>
    /// <param name="Length">For text, the length it is cut to; null for none.</param>
    public sealed record Cast(ScalarExpression Operand, CastTarget Target, int? Length) : ScalarExpression
    {
        /// <inheritdoc />
        public override ExpressionKind Kind => Target switch
        {
            CastTarget.Integer or CastTarget.SmallInteger or CastTarget.BigInteger => ExpressionKind.Integer,
            CastTarget.Double or CastTarget.Real => ExpressionKind.Real,
            CastTarget.Text => ExpressionKind.Text,
            _ => ExpressionKind.Date,
        };
    }

    /// <summary><c>EXTRACT(part FROM operand)</c>.</summary>
    /// <param name="Part">Which part.</param>
    /// <param name="Operand">The date.</param>
    public sealed record Extract(DatePart Part, ScalarExpression Operand) : ScalarExpression
    {
        /// <inheritdoc />
        public override ExpressionKind Kind => Part == DatePart.Second ? ExpressionKind.Real : ExpressionKind.Integer;
    }

    /// <summary><c>TRIM([BOTH|LEADING|TRAILING] ' ' FROM operand)</c> — spaces only, as ArcGIS defines it.</summary>
    /// <param name="Side">Which end.</param>
    /// <param name="Operand">The text.</param>
    public sealed record Trim(TrimSide Side, ScalarExpression Operand) : ScalarExpression
    {
        /// <inheritdoc />
        public override ExpressionKind Kind => ExpressionKind.Text;
    }
}
