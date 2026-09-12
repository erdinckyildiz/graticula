using System;
using System.Collections.Generic;
using System.Text.Json;
using Graticula.Catalog;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The written form of a domain and of a layer's subtypes — ADR-065.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS REST API's own shapes, stored as they are sent.</b> A domain is
/// <c>{"type":"codedValue","name":…,"codedValues":[{"name":…,"code":…}]}</c> or
/// <c>{"type":"range","name":…,"range":[min,max]}</c>, which is what the specification's domain
/// objects are; a subtype is <c>{"code":…,"name":…,"defaultValues":{…},"domains":{…}}</c>, which is
/// its <c>subtypes</c> entry. So what the catalogue stores, what the admin surface takes and gives
/// back, and what a client reads in the layer document are one shape and nobody translates.
/// </para>
/// <para>
/// <b>One reader for the store and the admin surface both</b>, for the reason
/// <see cref="FieldOverrideJson"/> gives: two independently written parsers is how a key comes to
/// be spelt two ways. The admin surface wants a sentence when a body is wrong and the store wants
/// to skip a malformed entry; both get it from the same method, the one reading the error and the
/// other discarding it.
/// </para>
/// </remarks>
public static class FieldDomainJson
{
    /// <summary>Reads a domain object.</summary>
    /// <param name="element">The object.</param>
    /// <param name="error">Why it is not one, when it is not.</param>
    /// <returns>The domain, or null with <paramref name="error"/> set.</returns>
    /// <remarks>
    /// <b>Its fit to a column is not judged here</b> — that needs the column, and
    /// <see cref="DomainRules.Refuse(FieldDomain, Graticula.Features.FieldType, int?)"/> has it.
    /// This only reads the shape.
    /// </remarks>
    public static FieldDomain? ReadDomain(JsonElement element, out string? error)
    {
        error = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "a domain is an object with a type, a name, and its codedValues or its range.";
            return null;
        }

        string type = element.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : string.Empty;

        string name = element.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!.Trim()
            : string.Empty;

        if (string.Equals(type, "codedValue", StringComparison.Ordinal))
        {
            if (!element.TryGetProperty("codedValues", out JsonElement values)
                || values.ValueKind != JsonValueKind.Array)
            {
                error = "a codedValue domain lists its values in codedValues, an array of {code, name}.";
                return null;
            }

            List<CodedValue> codes = new(values.GetArrayLength());

            foreach (JsonElement entry in values.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("code", out JsonElement code)
                    || !TryScalar(code, out DomainValue value))
                {
                    error = "each entry of codedValues is {code, name}, and a code is a number or text.";
                    return null;
                }

                string label = entry.TryGetProperty("name", out JsonElement l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString()!.Trim()
                    : string.Empty;

                codes.Add(new CodedValue(value, label));
            }

            return FieldDomain.Coded(name, codes);
        }

        if (string.Equals(type, "range", StringComparison.Ordinal))
        {
            if (!element.TryGetProperty("range", out JsonElement range)
                || range.ValueKind != JsonValueKind.Array
                || range.GetArrayLength() != 2
                || !TryScalar(range[0], out DomainValue min)
                || !TryScalar(range[1], out DomainValue max))
            {
                error = "a range domain gives its bounds as range: [least, greatest].";
                return null;
            }

            return FieldDomain.Range(name, min, max);
        }

        error = type.Length == 0
            ? "a domain says its type: codedValue or range."
            : $"'{type}' is not a domain type this server stores. A domain is codedValue or range; "
              + "inherited is how a subtype says it keeps the column's own, and is not stored.";
        return null;
    }

    /// <summary>Reads a set of subtypes.</summary>
    /// <param name="field">The subtype column, which the stored form does not repeat.</param>
    /// <param name="element">The object: <c>{defaultCode, types: [...]}</c>.</param>
    /// <param name="error">Why it is not one, when it is not.</param>
    /// <returns>The subtypes, or null with <paramref name="error"/> set.</returns>
    public static LayerSubtypes? ReadSubtypes(string field, JsonElement element, out string? error)
    {
        ArgumentNullException.ThrowIfNull(field);

        error = null;

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("types", out JsonElement types)
            || types.ValueKind != JsonValueKind.Array)
        {
            error = "subtypes are an object with the subtype column's name as field, a defaultCode, "
                + "and the subtypes themselves as types: [{code, name, defaultValues, domains}].";
            return null;
        }

        List<Subtype> read = new(types.GetArrayLength());

        foreach (JsonElement type in types.EnumerateArray())
        {
            if (type.ValueKind != JsonValueKind.Object
                || !type.TryGetProperty("code", out JsonElement c)
                || c.ValueKind != JsonValueKind.Number
                || !c.TryGetInt64(out long code))
            {
                error = "each subtype has a code, and a subtype code is a whole number.";
                return null;
            }

            string name = type.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!.Trim()
                : string.Empty;

            Dictionary<string, DomainValue> defaults = new(StringComparer.Ordinal);

            if (type.TryGetProperty("defaultValues", out JsonElement given) && given.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty value in given.EnumerateObject())
                {
                    // <b>A null default is no default</b>, which is what a template with nothing in
                    // that attribute already says — so it is not stored as a claim.
                    if (value.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (!TryScalar(value.Value, out DomainValue scalar))
                    {
                        error = $"subtype {code} gives '{value.Name}' a default that is neither a number nor text.";
                        return null;
                    }

                    defaults[value.Name] = scalar;
                }
            }

            Dictionary<string, FieldDomain> domains = new(StringComparer.Ordinal);

            if (type.TryGetProperty("domains", out JsonElement governed) && governed.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty domain in governed.EnumerateObject())
                {
                    // <b>Inherited is the absence of an entry</b>, and null says the same.
                    if (domain.Value.ValueKind == JsonValueKind.Null
                        || (domain.Value.ValueKind == JsonValueKind.Object
                            && domain.Value.TryGetProperty("type", out JsonElement kind)
                            && kind.ValueKind == JsonValueKind.String
                            && string.Equals(kind.GetString(), "inherited", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (ReadDomain(domain.Value, out string? why) is not { } parsed)
                    {
                        error = $"subtype {code} gives '{domain.Name}' a domain that cannot be read: {why}";
                        return null;
                    }

                    domains[domain.Name] = parsed;
                }
            }

            read.Add(new Subtype(code, name, defaults, domains));
        }

        long defaultCode;

        if (element.TryGetProperty("defaultCode", out JsonElement d) && d.ValueKind != JsonValueKind.Null)
        {
            if (d.ValueKind != JsonValueKind.Number || !d.TryGetInt64(out defaultCode))
            {
                error = "defaultCode is the code of one of the subtypes, a whole number.";
                return null;
            }
        }
        else
        {
            // <b>The first subtype when none is named</b>, which is what a person listing them in
            // order would expect a client to offer first.
            defaultCode = read.Count > 0 ? read[0].Code : 0;
        }

        return new LayerSubtypes(field, defaultCode, read);
    }

    /// <summary>The written form of a domain.</summary>
    /// <param name="domain">The domain.</param>
    /// <returns>An object the serializer writes as the ArcGIS domain object.</returns>
    public static Dictionary<string, object?> Write(FieldDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        Dictionary<string, object?> written = new(StringComparer.Ordinal)
        {
            ["type"] = domain.Kind == DomainKind.Range ? "range" : "codedValue",
            ["name"] = domain.Name,
        };

        if (domain.Kind == DomainKind.Range)
        {
            written["range"] = new[] { Scalar(domain.Min), Scalar(domain.Max) };
        }
        else
        {
            List<Dictionary<string, object?>> codes = new(domain.Codes.Count);

            foreach (CodedValue coded in domain.Codes)
            {
                codes.Add(new(StringComparer.Ordinal) { ["name"] = coded.Name, ["code"] = Scalar(coded.Code) });
            }

            written["codedValues"] = codes;
        }

        return written;
    }

    /// <summary>The written form of a set of subtypes.</summary>
    /// <param name="subtypes">The subtypes.</param>
    /// <param name="withField">
    /// Whether to name the subtype column — the admin surface does; the store keeps it on the
    /// override of that column and does not repeat it.
    /// </param>
    /// <returns>An object the serializer writes.</returns>
    public static Dictionary<string, object?> Write(LayerSubtypes subtypes, bool withField)
    {
        ArgumentNullException.ThrowIfNull(subtypes);

        List<Dictionary<string, object?>> types = new(subtypes.Types.Count);

        foreach (Subtype type in subtypes.Types)
        {
            Dictionary<string, object?> defaults = new(StringComparer.Ordinal);

            foreach ((string column, DomainValue value) in type.Defaults)
            {
                defaults[column] = Scalar(value);
            }

            Dictionary<string, object?> domains = new(StringComparer.Ordinal);

            foreach ((string column, FieldDomain domain) in type.Domains)
            {
                domains[column] = Write(domain);
            }

            types.Add(new(StringComparer.Ordinal)
            {
                ["code"] = type.Code,
                ["name"] = type.Name,
                ["defaultValues"] = defaults,
                ["domains"] = domains,
            });
        }

        Dictionary<string, object?> written = new(StringComparer.Ordinal);

        if (withField)
        {
            written["field"] = subtypes.Field;
        }

        written["defaultCode"] = subtypes.DefaultCode;
        written["types"] = types;

        return written;
    }

    /// <summary>A value as the serializer should write it: a number or a string.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A <see cref="decimal"/>, a <see cref="string"/>, or null.</returns>
    public static object? Scalar(DomainValue? value) =>
        value is not { } known ? null
        : known.Number is { } number ? number
        : known.Text;

    /// <summary>A JSON number or string as a domain value.</summary>
    private static bool TryScalar(JsonElement element, out DomainValue value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDecimal(out decimal number):
                value = DomainValue.Of(number);
                return true;

            case JsonValueKind.String:
                value = DomainValue.Of(element.GetString()!);
                return true;

            default:
                value = default;
                return false;
        }
    }
}
