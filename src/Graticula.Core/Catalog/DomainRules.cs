using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Features;

namespace Graticula.Catalog;

/// <summary>
/// What a domain and a set of subtypes may say, and whether a value obeys them — ADR-065.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place for both questions, because they are the same rules read in two directions.</b>
/// The admin surface asks whether a domain fits a column before storing it; the importer asks the
/// same before carrying one out of a geodatabase; the writer asks whether a value obeys what was
/// stored. Three copies of *an integer column takes integral codes* is how a domain gets stored
/// that every write then refuses.
/// </para>
/// <para>
/// <b>Refusals are sentences</b>, as everywhere else in this server: each one names the column,
/// the domain and what it allows, because the reader of a refused edit is a person looking at a
/// form.
/// </para>
/// </remarks>
public static class DomainRules
{
    /// <summary>
    /// The most codes a domain, or subtypes a layer, may carry.
    /// </summary>
    /// <remarks>
    /// <b>Every ArcGIS client reads the whole list with the layer document</b>, on every open, and a
    /// drop-down of more than this is not a list anybody chooses from. A value set that large
    /// belongs in a table a relationship points at, which is what ADR-013 already serves.
    /// </remarks>
    public const int MaximumCodes = 10_000;

    /// <summary>The longest name a domain, a code or a subtype may have.</summary>
    /// <remarks>The length ArcGIS itself allows a field alias, for the reason ADR-063 gives.</remarks>
    public const int LongestName = 255;

    /// <summary>How many codes a refusal lists before it stops.</summary>
    private const int Listed = 10;

    /// <summary>
    /// The kinds of domain a column of this type can take — a list, a range, both, or neither.
    /// </summary>
    /// <param name="type">The column's type.</param>
    /// <returns>The kinds, in the order a person would be offered them.</returns>
    /// <remarks>
    /// <b>The one statement of which types take which domains</b>, which
    /// <see cref="Refuse(FieldDomain, FieldType, int?)"/> enforces and the admin surface reports, so
    /// the Fields page offers exactly what would be stored. A 64-bit integer takes neither, because
    /// ArcGIS clients are sent it as text.
    /// </remarks>
    public static IReadOnlyList<DomainKind> KindsFor(FieldType type) => type switch
    {
        FieldType.Text => [DomainKind.CodedValue],
        FieldType.SmallInteger or FieldType.Integer or FieldType.Single or FieldType.Double =>
            [DomainKind.CodedValue, DomainKind.Range],
        FieldType.Date => [DomainKind.Range],

        // enum-default-is-deliberate: BigInteger, Boolean, Guid, Binary and Unknown take no domain.
        _ => [],
    };

    /// <summary>Whether a column of this type can say which subtype a feature is.</summary>
    /// <param name="type">The column's type.</param>
    /// <returns>Whether it can.</returns>
    /// <remarks>A short or long integer, which is what an ArcGIS client reads a subtype code as.</remarks>
    public static bool HoldsSubtypes(FieldType type) =>
        type is FieldType.SmallInteger or FieldType.Integer;

    /// <summary>
    /// Why a domain cannot govern a column of this type, or null when it can.
    /// </summary>
    /// <param name="domain">The domain.</param>
    /// <param name="type">The column's type.</param>
    /// <param name="maxLength">The column's declared length, for text.</param>
    /// <returns>The refusal.</returns>
    public static string? Refuse(FieldDomain domain, FieldType type, int? maxLength)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (string.IsNullOrWhiteSpace(domain.Name))
        {
            return "it has no name, and ArcGIS clients show a domain by its name.";
        }

        if (domain.Name.Length > LongestName)
        {
            return $"its name is {domain.Name.Length} characters, and a name is at most {LongestName}.";
        }

        if (type is FieldType.BigInteger)
        {
            return "a 64-bit integer column is sent to ArcGIS clients as text, because JavaScript "
                + "cannot hold every such value exactly, so a numeric domain on it would describe "
                + "values no client is shown.";
        }

        if (type is FieldType.Boolean or FieldType.Guid or FieldType.Binary or FieldType.Unknown)
        {
            return $"a {Words(type)} column cannot have a domain. Domains govern text, numbers and dates.";
        }

        // The types left take one kind or both; which is KindsFor's to say, and the two refusals
        // below are the only two ways a kind can be the wrong one for them.
        if (!KindsFor(type).Contains(domain.Kind))
        {
            return domain.Kind == DomainKind.Range
                ? "a range bounds a number or a date, and this is a text column. A text column "
                  + "takes a list of values."
                : "a date column takes a range rather than a list of values.";
        }

        if (domain.Kind == DomainKind.Range)
        {
            if (domain.Min is not { Number: { } min } || domain.Max is not { Number: { } max })
            {
                return "a range needs a least and a greatest value, and both must be numbers"
                    + (type == FieldType.Date ? " — a date is its milliseconds since 1970, UTC." : ".");
            }

            if (Unfit(DomainValue.Of(min), type, maxLength) is { } low)
            {
                return $"its least value {low}";
            }

            if (Unfit(DomainValue.Of(max), type, maxLength) is { } high)
            {
                return $"its greatest value {high}";
            }

            return min > max
                ? $"its least value {Shown(DomainValue.Of(min), type)} is greater than its greatest "
                  + $"{Shown(DomainValue.Of(max), type)}, so it would allow nothing."
                : null;
        }

        if (domain.Codes.Count == 0)
        {
            return "a list of values needs at least one value, or it allows nothing.";
        }

        if (domain.Codes.Count > MaximumCodes)
        {
            return $"it lists {domain.Codes.Count} values, and a list is at most {MaximumCodes}. Every "
                + "ArcGIS client reads the whole list each time it opens the layer; a set that large "
                + "belongs in a related table.";
        }

        HashSet<DomainValue> codes = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (CodedValue coded in domain.Codes)
        {
            if (Unfit(coded.Code, type, maxLength) is { } why)
            {
                return $"the code {coded.Code} {why}";
            }

            if (string.IsNullOrWhiteSpace(coded.Name))
            {
                return $"the code {coded.Code} has no name. A client shows the name, so each code needs one.";
            }

            if (coded.Name.Length > LongestName)
            {
                return $"the name of code {coded.Code} is {coded.Name.Length} characters; at most {LongestName}.";
            }

            if (!codes.Add(coded.Code))
            {
                return $"the code {coded.Code} is listed twice, so a stored value would have two names.";
            }

            if (!names.Add(coded.Name))
            {
                return $"the name '{coded.Name}' is given to two codes, so a person choosing it could "
                    + "mean either.";
            }
        }

        return null;
    }

    /// <summary>
    /// Why a set of subtypes cannot be stored on this table, or null when it can.
    /// </summary>
    /// <param name="subtypes">The subtypes.</param>
    /// <param name="table">The table's own shape, hidden columns included.</param>
    /// <param name="locked">
    /// Why a column may not carry a domain or a default — an identity, a column this server
    /// writes, a hidden column — or null when it may.
    /// </param>
    /// <param name="own">A column's own domain, as the same request stores it, or null.</param>
    /// <returns>The refusal.</returns>
    public static string? Refuse(
        LayerSubtypes subtypes,
        LayerDescription table,
        Func<string, string?> locked,
        Func<string, FieldDomain?> own)
    {
        ArgumentNullException.ThrowIfNull(subtypes);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(own);

        string field = subtypes.Field;

        if (table.Find(field) is not { } column)
        {
            return $"'{field}' is not a column of this layer's table, so it cannot hold subtype codes.";
        }

        if (!HoldsSubtypes(column.Type))
        {
            return $"'{field}' is a {Words(column.Type)} column. A subtype column holds short or long "
                + "integers, which is what an ArcGIS client reads a subtype code as.";
        }

        if (locked(field) is { } why)
        {
            return $"'{field}' cannot be the subtype column: {why}";
        }

        if (subtypes.Types.Count == 0)
        {
            return $"'{field}' is named as the subtype column and no subtypes are given. Remove the "
                + "subtypes instead, and the layer has none.";
        }

        if (subtypes.Types.Count > MaximumCodes)
        {
            return $"{subtypes.Types.Count} subtypes are given, and a layer has at most {MaximumCodes}.";
        }

        HashSet<long> codes = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (Subtype type in subtypes.Types)
        {
            if (Unfit(DomainValue.Of(type.Code), column.Type, null) is { } unfit)
            {
                return $"the subtype code {type.Code} {unfit}";
            }

            if (!codes.Add(type.Code))
            {
                return $"the subtype code {type.Code} is given twice, so a feature holding it would be "
                    + "two kinds at once.";
            }

            if (string.IsNullOrWhiteSpace(type.Name))
            {
                return $"subtype {type.Code} has no name. A client shows the name, so each needs one.";
            }

            if (type.Name.Length > LongestName)
            {
                return $"the name of subtype {type.Code} is {type.Name.Length} characters; at most {LongestName}.";
            }

            if (!names.Add(type.Name))
            {
                return $"the name '{type.Name}' is given to two subtypes.";
            }

            foreach ((string name, FieldDomain domain) in type.Domains)
            {
                if (Governed(name, field, table, locked) is { } refused)
                {
                    return $"subtype {type.Code} ({type.Name}) gives a domain to {refused}";
                }

                FieldDescription target = table.Find(name)!.Value;

                if (Refuse(domain, target.Type, target.MaxLength) is { } bad)
                {
                    return $"subtype {type.Code} ({type.Name}) gives '{name}' the domain '{domain.Name}', "
                        + $"and {bad}";
                }
            }

            foreach ((string name, DomainValue value) in type.Defaults)
            {
                if (Governed(name, field, table, locked) is { } refused)
                {
                    return $"subtype {type.Code} ({type.Name}) gives a default to {refused}";
                }

                FieldDescription target = table.Find(name)!.Value;

                if (Unfit(value, target.Type, target.MaxLength) is { } unfitDefault)
                {
                    return $"subtype {type.Code} ({type.Name}) gives '{name}' the default {value}, which {unfitDefault}";
                }

                FieldDomain? governing = subtypes.DomainFor(name, type.Code, own(name));

                if (governing is not null && !Allows(governing, value, target.Type))
                {
                    return $"subtype {type.Code} ({type.Name}) gives '{name}' the default "
                        + $"{Shown(value, target.Type)}, which its domain '{governing.Name}' does not allow. "
                        + $"It allows {Allowed(governing, target.Type)}.";
                }
            }
        }

        return codes.Contains(subtypes.DefaultCode)
            ? null
            : $"the default subtype {subtypes.DefaultCode} is not one of the subtypes given "
              + $"({string.Join(", ", codes.Take(Listed))}).";
    }

    /// <summary>
    /// Why a value written to a column breaks the domain governing it, or null when it does not.
    /// </summary>
    /// <param name="field">The column, as the layer describes it.</param>
    /// <param name="value">The value, already converted to the column's type.</param>
    /// <param name="domain">The domain that governs it for this feature, or null for none.</param>
    /// <param name="subtype">
    /// The subtype whose domain it is, when it is a subtype's rather than the column's, so the
    /// refusal can say why a value allowed elsewhere is refused here.
    /// </param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// <b>A null is never a domain's to refuse.</b> Whether a column may be empty is its
    /// nullability, which the database enforces; a domain says which values are allowed, and
    /// ArcGIS clients offer a blank beside every drop-down for a nullable column.
    /// </remarks>
    public static string? Refusal(FieldDescription field, object? value, FieldDomain? domain, Subtype? subtype = null)
    {
        if (domain is null || value is null || value is DBNull)
        {
            return null;
        }

        string whose = subtype is null ? string.Empty : $" for subtype {subtype.Code} ({subtype.Name})";

        if (!TryValue(value, field.Type, out DomainValue given))
        {
            return $"'{field.Name}' was sent a value the domain '{domain.Name}' cannot compare{whose}, "
                + $"so it is refused. It allows {Allowed(domain, field.Type)}.";
        }

        if (Allows(domain, given, field.Type))
        {
            return null;
        }

        return domain.Kind == DomainKind.Range
            ? $"'{field.Name}' is {Shown(given, field.Type)}, outside the domain '{domain.Name}'{whose}, "
              + $"which allows {Allowed(domain, field.Type)}."
            : $"'{field.Name}' is {Shown(given, field.Type)}, which the domain '{domain.Name}' does not "
              + $"allow{whose}. It allows {Allowed(domain, field.Type)}.";
    }

    /// <summary>
    /// Why a value written to the subtype column is not a subtype, or null when it is.
    /// </summary>
    /// <param name="subtypes">The layer's subtypes.</param>
    /// <param name="value">The value, already converted to the column's type.</param>
    /// <returns>The refusal.</returns>
    public static string? SubtypeRefusal(LayerSubtypes subtypes, object? value)
    {
        ArgumentNullException.ThrowIfNull(subtypes);

        if (value is null || value is DBNull)
        {
            return null;
        }

        if (TryCode(value, out long code) && subtypes.Find(code) is not null)
        {
            return null;
        }

        return $"'{subtypes.Field}' is {value}, which is not one of this layer's subtypes. It allows "
            + string.Join(", ", subtypes.Types.Take(Listed).Select(t => $"{t.Code} ({t.Name})"))
            + (subtypes.Types.Count > Listed ? $" and {subtypes.Types.Count - Listed} more." : ".");
    }

    /// <summary>A value from the subtype column as a code, when it is an integer.</summary>
    /// <param name="value">The value, as read or converted.</param>
    /// <param name="code">The code.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryCode(object? value, out long code)
    {
        switch (value)
        {
            case short s:
                code = s;
                return true;
            case int i:
                code = i;
                return true;
            case long l:
                code = l;
                return true;
            case decimal d when decimal.Truncate(d) == d && d is >= long.MinValue and <= long.MaxValue:
                code = (long)d;
                return true;
            default:
                code = 0;
                return false;
        }
    }

    /// <summary>Whether a domain allows a value, compared in the column's own precision.</summary>
    /// <param name="domain">The domain.</param>
    /// <param name="value">The value.</param>
    /// <param name="type">The column's type.</param>
    /// <returns>Whether it is allowed.</returns>
    public static bool Allows(FieldDomain domain, DomainValue value, FieldType type)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (domain.Kind == DomainKind.Range)
        {
            if (value.Number is not { } number
                || domain.Min is not { Number: { } min }
                || domain.Max is not { Number: { } max })
            {
                return false;
            }

            // <b>A single-precision column compares as one</b>: 1.1 stored in a `real` column reads
            // back as 1.10000002, and a bound of 1.1 must still admit it.
            return type == FieldType.Single
                ? (float)number >= (float)min && (float)number <= (float)max
                : number >= min && number <= max;
        }

        if (value.Text is { } text)
        {
            return domain.Lists(text);
        }

        if (value.Number is not { } code)
        {
            return false;
        }

        if (type == FieldType.Single)
        {
            foreach (CodedValue coded in domain.Codes)
            {
                if (coded.Code.Number is { } listed && (float)listed == (float)code)
                {
                    return true;
                }
            }

            return false;
        }

        return domain.Lists(code);
    }

    /// <summary>
    /// A written value in the shape a domain compares: a number, a date as its epoch
    /// milliseconds, or text.
    /// </summary>
    /// <param name="value">A value converted to its column's type.</param>
    /// <param name="type">The column's type.</param>
    /// <param name="compared">The value.</param>
    /// <returns>Whether it could be put in that shape.</returns>
    public static bool TryValue(object? value, FieldType type, out DomainValue compared)
    {
        compared = default;

        switch (value)
        {
            case string text when type == FieldType.Text:
                compared = DomainValue.Of(text);
                return true;

            case short or int or long or byte or sbyte or ushort or uint:
                compared = DomainValue.Of(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                return true;

            case decimal d:
                compared = DomainValue.Of(d);
                return true;

            case float f when float.IsFinite(f):
                compared = DomainValue.Of((decimal)f);
                return true;

            // Bounded by what a decimal holds; a double beyond it is outside any range a person
            // wrote, and is refused as incomparable rather than thrown.
            case double g when double.IsFinite(g) && Math.Abs(g) < 7.9e27:
                compared = DomainValue.Of((decimal)g);
                return true;

            // An unspecified kind is what Npgsql and the ArcGIS converter both hand over for UTC.
            case DateTime at when type == FieldType.Date:
                compared = DomainValue.Of(new DateTimeOffset(
                        at.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                            : at.ToUniversalTime())
                    .ToUnixTimeMilliseconds());
                return true;

            case DateTimeOffset offset when type == FieldType.Date:
                compared = DomainValue.Of(offset.ToUnixTimeMilliseconds());
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Why a value is not one a column of this type can hold, or null when it is.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="type">The column's type.</param>
    /// <param name="maxLength">The column's declared length, for text.</param>
    /// <returns>The end of a sentence that begins with the value.</returns>
    public static string? Unfit(DomainValue value, FieldType type, int? maxLength)
    {
        if (type == FieldType.Text)
        {
            if (value.Text is not { } text)
            {
                return "is a number, and this is a text column.";
            }

            return maxLength is { } most && text.Length > most
                ? $"is {text.Length} characters, and the column holds at most {most}."
                : null;
        }

        if (value.Number is not { } number)
        {
            return $"is text, and this is a {Words(type)} column.";
        }

        (decimal Least, decimal Most)? bounds = type switch
        {
            FieldType.SmallInteger => (short.MinValue, short.MaxValue),
            FieldType.Integer => (int.MinValue, int.MaxValue),
            FieldType.BigInteger => (long.MinValue, long.MaxValue),

            // The span a DateTimeOffset holds, in milliseconds.
            FieldType.Date => (-62_135_596_800_000m, 253_402_300_799_999m),

            // enum-default-is-deliberate: the floating types take any number a decimal holds.
            _ => null,
        };

        if (bounds is { } range)
        {
            if (decimal.Truncate(number) != number)
            {
                return type == FieldType.Date
                    ? "is not a whole number of milliseconds."
                    : $"is not a whole number, and this is a {Words(type)} column.";
            }

            if (number < range.Least || number > range.Most)
            {
                return $"does not fit a {Words(type)} column.";
            }
        }

        return null;
    }

    /// <summary>What a domain allows, in the words a refusal uses.</summary>
    /// <param name="domain">The domain.</param>
    /// <param name="type">The column's type.</param>
    /// <returns>The words.</returns>
    public static string Allowed(FieldDomain domain, FieldType type)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (domain.Kind == DomainKind.Range)
        {
            return $"{(domain.Min is { } min ? Shown(min, type) : "?")} to "
                + $"{(domain.Max is { } max ? Shown(max, type) : "?")}";
        }

        string listed = string.Join(
            ", ", domain.Codes.Take(Listed).Select(c => $"{Shown(c.Code, type)} ({c.Name})"));

        return domain.Codes.Count > Listed
            ? $"{listed} and {domain.Codes.Count - Listed} more"
            : listed;
    }

    /// <summary>A value as a person reads it: a date as a date, text in quotes.</summary>
    private static string Shown(DomainValue value, FieldType type) =>
        type == FieldType.Date
        && value.Number is { } ms
        && ms is >= -62_135_596_800_000m and <= 253_402_300_799_999m
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : value.ToString();

    /// <summary>Why a column cannot carry a subtype's domain or default, or null — the column named first.</summary>
    private static string? Governed(
        string name, string subtypeField, LayerDescription table, Func<string, string?> locked)
    {
        if (string.Equals(name, subtypeField, StringComparison.Ordinal))
        {
            return $"'{name}', which is the subtype column itself: its values are the subtype codes.";
        }

        if (table.Find(name) is null)
        {
            return $"'{name}', which is not a column of this layer's table.";
        }

        return locked(name) is { } why ? $"'{name}', which cannot take one: {why}" : null;
    }

    /// <summary>A type in the words a refusal uses.</summary>
    private static string Words(FieldType type) => type switch
    {
        FieldType.SmallInteger => "short integer",
        FieldType.Integer => "long integer",
        FieldType.BigInteger => "64-bit integer",
        FieldType.Single => "single-precision",
        FieldType.Double => "double-precision",
        FieldType.Text => "text",
        FieldType.Boolean => "true-or-false",
        FieldType.Date => "date",
        FieldType.Guid => "GUID",
        FieldType.Binary => "binary",

        // enum-default-is-deliberate: Unknown is the only value left, and it is named as one.
        _ => "unrecognised",
    };
}
