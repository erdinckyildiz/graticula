using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Reads Filter Encoding 2.0 into the domain's own predicate types.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second front end, and the reason <see cref="PredicateSql"/> exists.</b>
/// This produces an <see cref="AttributePredicate"/> and a
/// <see cref="SpatialFilter"/> and stops. It never writes SQL — ADR-039 §5 —
/// which is what keeps a compatibility adapter on the outside of §51's line while
/// still supporting a filter language of its own.
/// </para>
/// <para>
/// <b>Every property name is matched against the layer's own fields before it
/// becomes a node</b>, so a <c>ValueReference</c> naming a column that does not
/// exist is refused here with the name in the message. The emitter matches again;
/// that duplication is deliberate and is described where it lives.
/// </para>
/// <para>
/// <b>A literal's type comes from the field, because Filter Encoding does not
/// carry one.</b> <c>&lt;Literal&gt;5&lt;/Literal&gt;</c> is text on the wire
/// whether the column is an integer or a name — where the SQL-92 grammar
/// distinguishes <c>5</c> from <c>'5'</c> by syntax. So the field's declared type
/// decides, and a value that does not fit it is refused rather than bound as text
/// and left for the database to complain about in its own words.
/// </para>
/// <para>
/// <b>What it refuses, and why refusing is the feature.</b> A query carries one
/// spatial restriction, one attribute predicate and a list of identities, joined
/// by <c>and</c>. Filter Encoding can say things that shape cannot hold —
/// <c>Or</c> across a spatial and an attribute test, <c>Not</c> around a spatial
/// one, two spatial tests at once. Each is refused by name. The alternative is to
/// apply the half that fits, which returns features the caller excluded and says
/// nothing, and that is precisely the silent degradation ADR-008 §2 forbids.
/// </para>
/// </remarks>
public static class FilterReader
{
    /// <summary>Reads a filter document.</summary>
    /// <param name="xml">The <c>fes:Filter</c> element, as text.</param>
    /// <param name="fields">The layer's attribute columns.</param>
    /// <param name="layerSrid">The layer's coordinate reference.</param>
    /// <param name="filter">What the filter says.</param>
    /// <param name="fault">Why it was refused.</param>
    /// <returns>Whether it read.</returns>
    public static bool TryRead(
        string? xml,
        IReadOnlyList<FieldDescription> fields,
        int layerSrid,
        out ParsedFilter filter,
        out WfsFault? fault)
    {
        ArgumentNullException.ThrowIfNull(fields);

        filter = ParsedFilter.None;
        fault = null;

        if (string.IsNullOrWhiteSpace(xml))
        {
            return true;
        }

        XElement root;

        try
        {
            using XmlReader reader = SafeXml.Read(xml);
            root = XElement.Load(reader, LoadOptions.None);
        }
        catch (XmlException e)
        {
            fault = WfsFault.Invalid("filter", $"The filter is not well-formed XML: {e.Message}");
            return false;
        }

        XNamespace fes = WfsNames.Fes;

        // A client may send the Filter element itself or its single child. Both
        // appear in the wild and the difference is not worth a refusal.
        XElement? body = root.Name == fes + "Filter"
            ? root.Elements().FirstOrDefault()
            : root;

        if (body is null)
        {
            fault = WfsFault.Invalid("filter", "The filter is empty.");
            return false;
        }

        if (!TryPart(body, fields, layerSrid, 0, out Part part, out fault))
        {
            return false;
        }

        filter = new ParsedFilter(part.Predicate, part.Spatial, part.Srid, part.Ids);
        return true;
    }

    /// <summary>One branch of a filter, in the three slots a query has.</summary>
    private readonly record struct Part(
        AttributePredicate? Predicate,
        SpatialFilter? Spatial,
        int? Srid,
        IReadOnlyList<string> Ids)
    {
        public static Part Empty => new(null, null, null, []);

        public bool HasSpatialOrIds => Spatial is not null || Ids.Count > 0;
    }

    private static bool TryPart(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        int layerSrid,
        int depth,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;
        fault = null;

        if (depth > SafeXml.MaximumDepth)
        {
            fault = WfsFault.Invalid(
                "filter",
                $"The filter nests more than {SafeXml.MaximumDepth} levels deep. The limit exists "
                + "because reading it is recursive and a deep enough document would exhaust the "
                + "stack, which cannot be caught.");

            return false;
        }

        if (element.Name.Namespace != XNamespace.Get(WfsNames.Fes))
        {
            fault = WfsFault.Invalid(
                "filter",
                $"'{element.Name.LocalName}' is not a Filter Encoding 2.0 element. Predicates must "
                + $"be in the {WfsNames.Fes} namespace.");

            return false;
        }

        string name = element.Name.LocalName;

        return name switch
        {
            "And" => TryLogical(element, fields, layerSrid, depth, and: true, out part, out fault),
            "Or" => TryLogical(element, fields, layerSrid, depth, and: false, out part, out fault),
            "Not" => TryNot(element, fields, layerSrid, depth, out part, out fault),
            "ResourceId" => TryResourceId(element, out part, out fault),
            "PropertyIsNull" => TryNull(element, fields, out part, out fault),
            "PropertyIsLike" => TryLike(element, fields, out part, out fault),
            "PropertyIsBetween" => TryBetween(element, fields, out part, out fault),
            _ when Comparisons.ContainsKey(name) =>
                TryComparison(element, fields, Comparisons[name], out part, out fault),
            _ when SpatialRelations.ContainsKey(name) =>
                TrySpatial(element, fields, layerSrid, depth, name, out part, out fault),
            _ => Unsupported(name, out part, out fault),
        };
    }

    private static bool Unsupported(string name, out Part part, out WfsFault? fault)
    {
        part = Part.Empty;

        fault = new WfsFault(
            WfsFaultCode.OperationNotSupported,
            "filter",
            $"'fes:{name}' is not a predicate this server evaluates. It reads And, Or, Not, "
            + "ResourceId, PropertyIsEqualTo, PropertyIsNotEqualTo, PropertyIsLessThan, "
            + "PropertyIsGreaterThan, PropertyIsLessThanOrEqualTo, PropertyIsGreaterThanOrEqualTo, "
            + "PropertyIsLike, PropertyIsNull, PropertyIsBetween, BBOX, Intersects, Within, "
            + "Contains, Crosses, Overlaps, Touches and DWithin.");

        return false;
    }

    private static readonly Dictionary<string, ComparisonOperator> Comparisons =
        new(StringComparer.Ordinal)
        {
            ["PropertyIsEqualTo"] = ComparisonOperator.Equal,
            ["PropertyIsNotEqualTo"] = ComparisonOperator.NotEqual,
            ["PropertyIsLessThan"] = ComparisonOperator.LessThan,
            ["PropertyIsGreaterThan"] = ComparisonOperator.GreaterThan,
            ["PropertyIsLessThanOrEqualTo"] = ComparisonOperator.LessThanOrEqual,
            ["PropertyIsGreaterThanOrEqualTo"] = ComparisonOperator.GreaterThanOrEqual,
        };

    /// <summary>
    /// The spatial predicates this server evaluates, and what each means to a
    /// provider.
    /// </summary>
    /// <remarks>
    /// <b>Disjoint, Equals and Beyond are absent by decision.</b> Each is the
    /// negation or the equality of something in the table, and the query model
    /// carries one spatial relation with no way to negate it — so supporting them
    /// would mean either a wrong answer or a second mechanism. They are refused by
    /// name, which is a smaller surface honestly described.
    /// </remarks>
    private static readonly Dictionary<string, SpatialRelation> SpatialRelations =
        new(StringComparer.Ordinal)
        {
            ["BBOX"] = SpatialRelation.EnvelopeIntersects,
            ["Intersects"] = SpatialRelation.Intersects,
            ["Within"] = SpatialRelation.Within,
            ["Contains"] = SpatialRelation.Contains,
            ["Crosses"] = SpatialRelation.Crosses,
            ["Overlaps"] = SpatialRelation.Overlaps,
            ["Touches"] = SpatialRelation.Touches,
            ["DWithin"] = SpatialRelation.Intersects,
        };

    private static bool TryLogical(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        int layerSrid,
        int depth,
        bool and,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;
        fault = null;

        List<XElement> children = [.. element.Elements()];

        if (children.Count < 2)
        {
            fault = WfsFault.Invalid(
                "filter", $"'fes:{element.Name.LocalName}' needs at least two operands.");

            return false;
        }

        AttributePredicate? predicate = null;
        SpatialFilter? spatial = null;
        int? srid = null;
        List<string> ids = [];

        foreach (XElement child in children)
        {
            if (!TryPart(child, fields, layerSrid, depth + 1, out Part one, out fault))
            {
                return false;
            }

            if (!and && one.HasSpatialOrIds)
            {
                fault = new WfsFault(
                    WfsFaultCode.OperationNotSupported,
                    "filter",
                    "A spatial predicate or a ResourceId inside an 'Or' cannot be evaluated: a "
                    + "query carries one spatial restriction and one identity list, both joined "
                    + "to the rest with 'and'. Rewrite the filter so the alternatives are "
                    + "attribute tests, or send one request per alternative.");

                return false;
            }

            if (one.Spatial is not null)
            {
                if (spatial is not null)
                {
                    fault = new WfsFault(
                        WfsFaultCode.OperationNotSupported,
                        "filter",
                        "Two spatial predicates in one filter cannot be evaluated: a query carries "
                        + "one. Combine them into a single geometry, or send one request each.");

                    return false;
                }

                spatial = one.Spatial;
                srid = one.Srid;
            }

            ids.AddRange(one.Ids);

            if (one.Predicate is not null)
            {
                predicate = predicate is null
                    ? one.Predicate
                    : and
                        ? new AttributePredicate.Conjunction(predicate, one.Predicate)
                        : new AttributePredicate.Disjunction(predicate, one.Predicate);
            }
        }

        part = new Part(predicate, spatial, srid, ids);
        return true;
    }

    private static bool TryNot(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        int layerSrid,
        int depth,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;

        if (element.Elements().FirstOrDefault() is not { } child)
        {
            fault = WfsFault.Invalid("filter", "'fes:Not' needs one operand.");
            return false;
        }

        if (!TryPart(child, fields, layerSrid, depth + 1, out Part inner, out fault))
        {
            return false;
        }

        if (inner.HasSpatialOrIds || inner.Predicate is null)
        {
            fault = new WfsFault(
                WfsFaultCode.OperationNotSupported,
                "filter",
                "'fes:Not' around a spatial predicate or a ResourceId cannot be evaluated: the "
                + "query model has no way to negate either. Negate the attribute test instead, or "
                + "invert the geometry in the request.");

            return false;
        }

        part = new Part(new AttributePredicate.Negation(inner.Predicate), null, null, []);
        return true;
    }

    private static bool TryResourceId(XElement element, out Part part, out WfsFault? fault)
    {
        part = Part.Empty;

        string? rid = (string?)element.Attribute("rid");

        if (string.IsNullOrWhiteSpace(rid))
        {
            fault = WfsFault.Invalid("filter", "A fes:ResourceId needs a 'rid' attribute.");
            return false;
        }

        fault = null;
        part = new Part(null, null, null, [rid]);
        return true;
    }

    private static bool TryComparison(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        ComparisonOperator op,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;

        if (!MatchCaseIsHonoured(element, out fault)
            || !TryProperty(element, fields, out FieldDescription field, out fault)
            || !TryLiteral(element, field, Literal(element, 0), out object? value, out fault))
        {
            return false;
        }

        part = new Part(
            new AttributePredicate.Comparison(field.Name, op, value), null, null, []);

        return true;
    }

    private static bool TryNull(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;

        if (!TryProperty(element, fields, out FieldDescription field, out fault))
        {
            return false;
        }

        part = new Part(new AttributePredicate.IsNull(field.Name, Negated: false), null, null, []);
        return true;
    }

    private static bool TryBetween(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;

        if (!TryProperty(element, fields, out FieldDescription field, out fault))
        {
            return false;
        }

        XNamespace fes = WfsNames.Fes;

        XElement? lower = element.Element(fes + "LowerBoundary")?.Element(fes + "Literal");
        XElement? upper = element.Element(fes + "UpperBoundary")?.Element(fes + "Literal");

        if (lower is null || upper is null)
        {
            fault = WfsFault.Invalid(
                "filter",
                "A fes:PropertyIsBetween needs a LowerBoundary and an UpperBoundary, each holding "
                + "a Literal.");

            return false;
        }

        if (!TryLiteral(element, field, lower, out object? low, out fault)
            || !TryLiteral(element, field, upper, out object? high, out fault))
        {
            return false;
        }

        part = new Part(
            new AttributePredicate.Between(field.Name, low, high, Negated: false), null, null, []);

        return true;
    }

    private static bool TryLike(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;

        if (!MatchCaseIsHonoured(element, out fault)
            || !TryProperty(element, fields, out FieldDescription field, out fault))
        {
            return false;
        }

        if (Literal(element, 0) is not { } literal)
        {
            fault = WfsFault.Invalid("filter", "A fes:PropertyIsLike needs a Literal.");
            return false;
        }

        char wildCard = Single(element, "wildCard", '*');
        char singleChar = Single(element, "singleChar", '_');
        char escapeChar = Single(element, "escapeChar", '\\');

        part = new Part(
            new AttributePredicate.Matches(
                field.Name,
                SqlPattern(literal.Value, wildCard, singleChar, escapeChar),
                Negated: false),
            null,
            null,
            []);

        return true;
    }

    /// <summary>
    /// Rewrites a Filter Encoding pattern as a SQL <c>like</c> pattern.
    /// </summary>
    /// <remarks>
    /// <b>Both directions matter, and the second is the one that gets forgotten.</b>
    /// The client's wildcards become SQL's — that is the obvious half. The other
    /// half is that a literal <c>%</c> or <c>_</c> in the client's text is an
    /// ordinary character to them and a wildcard to SQL, so it must be escaped or
    /// a search for "50%" quietly matches everything beginning "50".
    /// </remarks>
    private static string SqlPattern(string text, char wildCard, char singleChar, char escapeChar)
    {
        System.Text.StringBuilder pattern = new(text.Length + 8);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == escapeChar && i + 1 < text.Length)
            {
                char next = text[++i];

                if (next is '%' or '_' or '\\')
                {
                    pattern.Append('\\');
                }

                pattern.Append(next);
                continue;
            }

            if (c == wildCard)
            {
                pattern.Append('%');
                continue;
            }

            if (c == singleChar)
            {
                pattern.Append('_');
                continue;
            }

            if (c is '%' or '_' or '\\')
            {
                pattern.Append('\\');
            }

            pattern.Append(c);
        }

        return pattern.ToString();
    }

    private static char Single(XElement element, string attribute, char fallback)
    {
        string? text = (string?)element.Attribute(attribute);

        return string.IsNullOrEmpty(text) ? fallback : text[0];
    }

    private static bool TrySpatial(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        int layerSrid,
        int depth,
        string name,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;
        fault = null;

        // <b>The geometry property is not checked against the field list, and that
        // is not an oversight.</b> LayerDescription carries attribute columns with
        // the geometry excluded, so the name a client sends here — whatever it
        // calls it — has nothing to be matched against. A layer has exactly one
        // geometry, so there is no ambiguity to resolve and nothing a wrong name
        // could select instead.
        XElement? shape = element
            .Elements()
            .FirstOrDefault(e => e.Name.Namespace == XNamespace.Get(WfsNames.Gml));

        if (shape is null)
        {
            fault = WfsFault.Invalid(
                "filter", $"'fes:{name}' needs a GML geometry to compare against.");

            return false;
        }

        if (!GmlGeometryReader.TryRead(
                shape, layerSrid, depth + 1, out Geometry? geometry, out int srid, out fault))
        {
            return false;
        }

        double distance = 0;

        if (string.Equals(name, "DWithin", StringComparison.Ordinal)
            && !TryDistance(element, out distance, out fault))
        {
            return false;
        }

        part = new Part(
            null,
            new SpatialFilter(geometry!, SpatialRelations[name], RelatePattern: null, distance),
            srid == layerSrid ? null : srid,
            []);

        return true;
    }

    /// <summary>Reads a DWithin distance.</summary>
    /// <remarks>
    /// <b>The units attribute is required to match the layer's, and saying so is
    /// the point.</b> <c>SpatialFilter.Distance</c> is in the layer's own units,
    /// and turning metres into degrees needs to know where on the earth the
    /// question is being asked. Converting badly is worse than refusing: a buffer
    /// that is out by a factor of a hundred thousand returns a plausible answer.
    /// </remarks>
    private static bool TryDistance(XElement element, out double distance, out WfsFault? fault)
    {
        distance = 0;
        fault = null;

        XNamespace fes = WfsNames.Fes;

        if (element.Element(fes + "Distance") is not { } node)
        {
            fault = WfsFault.Invalid("filter", "A fes:DWithin needs a fes:Distance.");
            return false;
        }

        if (!double.TryParse(
                node.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out distance)
            || distance < 0)
        {
            fault = WfsFault.Invalid(
                "filter", $"'{node.Value}' is not a distance. It must be a number and not negative.");

            return false;
        }

        string? units = (string?)node.Attribute("uom") ?? (string?)node.Attribute("units");

        if (!string.IsNullOrWhiteSpace(units)
            && !units.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            fault = new WfsFault(
                WfsFaultCode.OperationNotSupported,
                "filter",
                $"A distance in '{units}' cannot be converted: this server measures a DWithin in "
                + "the layer's own units. Send the distance in those units and omit uom.");

            return false;
        }

        return true;
    }

    private static bool MatchCaseIsHonoured(XElement element, out WfsFault? fault)
    {
        fault = null;

        string? matchCase = (string?)element.Attribute("matchCase");

        if (matchCase is null
            || bool.TryParse(matchCase, out bool value) && value)
        {
            return true;
        }

        fault = new WfsFault(
            WfsFaultCode.OperationNotSupported,
            "filter",
            "matchCase=\"false\" is not supported: the query model has no case-insensitive "
            + "comparison, and answering a case-sensitive comparison to a caller who asked for a "
            + "case-insensitive one is a wrong answer rather than a smaller one.");

        return false;
    }

    private static XElement? Literal(XElement element, int skip) => element
        .Elements(XNamespace.Get(WfsNames.Fes) + "Literal")
        .Skip(skip)
        .FirstOrDefault();

    private static bool TryProperty(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        out FieldDescription field,
        out WfsFault? fault)
    {
        field = default;
        fault = null;

        XNamespace fes = WfsNames.Fes;

        XElement? reference = element.Element(fes + "ValueReference")
            ?? element.Element(fes + "PropertyName");

        if (reference is null || string.IsNullOrWhiteSpace(reference.Value))
        {
            fault = WfsFault.Invalid(
                "filter",
                $"'fes:{element.Name.LocalName}' needs a fes:ValueReference naming a property.");

            return false;
        }

        // Minimum XPath, shared with GetPropertyValue so the two cannot diverge.
        if (!ValueReference.TryLocalName(reference.Value, out string local, out fault))
        {
            return false;
        }

        foreach (FieldDescription candidate in fields)
        {
            if (string.Equals(candidate.Name, local, StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }

        fault = WfsFault.Invalid(
            "filter",
            $"'{local}' is not a property of this feature type. A filter may only mention "
            + "properties the schema lists.");

        return false;
    }

    private static bool TryLiteral(
        XElement owner,
        FieldDescription field,
        XElement? literal,
        out object? value,
        out WfsFault? fault)
    {
        value = null;
        fault = null;

        if (literal is null)
        {
            fault = WfsFault.Invalid(
                "filter", $"'fes:{owner.Name.LocalName}' needs a fes:Literal.");

            return false;
        }

        string text = literal.Value;

        switch (field.Type)
        {
            case FieldType.SmallInteger:
            case FieldType.Integer:
            case FieldType.BigInteger:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out long whole))
                {
                    fault = Mismatch(field, text, "a whole number");
                    return false;
                }

                value = whole;
                return true;

            case FieldType.Single:
            case FieldType.Double:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double real))
                {
                    fault = Mismatch(field, text, "a number");
                    return false;
                }

                value = real;
                return true;

            case FieldType.Boolean:
                if (!bool.TryParse(text, out bool flag))
                {
                    fault = Mismatch(field, text, "true or false");
                    return false;
                }

                value = flag;
                return true;

            default:
                // <b>Text, uuid, binary and date all bind as text, and the last of
                // those is a known limit rather than an accident.</b> The SQL-92
                // front end has the same one — its grammar has no date literal —
                // so a comparison against a timestamp column is refused by the
                // database in both surfaces rather than in one. Making WFS convert
                // dates while ArcGIS does not would give the two front ends
                // different answers to the same question, which is the divergence
                // the shared emitter exists to prevent. Q-124.
                value = text;
                return true;
        }
    }

    private static WfsFault Mismatch(FieldDescription field, string text, string wanted) =>
        WfsFault.Invalid(
            "filter",
            $"'{text}' is not {wanted}, and '{field.Name}' is {field.Type}. Filter Encoding sends "
            + "every literal as text, so the property's own type decides how it is read.");
}
