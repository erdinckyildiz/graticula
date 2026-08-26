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
    /// <param name="geometryColumn">
    /// The layer's geometry column, so that a null check may name it. Omitted, the
    /// geometry is not a property any predicate can mention.
    /// </param>
    /// <returns>Whether it read.</returns>
    public static bool TryRead(
        string? xml,
        IReadOnlyList<FieldDescription> fields,
        int layerSrid,
        out ParsedFilter filter,
        out WfsFault? fault,
        string? geometryColumn = null)
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

        /*
          <b>Several `ResourceId` children of one `Filter` are one filter, not the first
          of several.</b> Filter Encoding 2.0 §7.11 makes a list of identities an implicit
          union — `<Filter><ResourceId rid="a"/><ResourceId rid="b"/></Filter>` asks for
          both — and this read `FirstOrDefault()` and answered with one feature. A 200
          carrying half of what was asked for, with nothing to say so.

          <b>Only identities, and only at the top.</b> Filter Encoding gives no implicit
          combination to anything else: two sibling comparisons under one `Filter` are not
          a valid document, and guessing `And` for them would be inventing a meaning the
          specification declines to give. Those still take the first child, which is what
          the tolerance above is for.

          Found on 2026-08-21 by re-running the OGC suite, which charges 13 failures for
          it. The KVP spelling `resourceId=a,b` was correct throughout — the two spellings
          disagreed, which is the class the consistency gate exists for.
        */
        if (root.Name == fes + "Filter")
        {
            List<XElement> identities = [.. root.Elements().Where(e => e.Name == fes + "ResourceId")];

            if (identities.Count > 1 && identities.Count == root.Elements().Count())
            {
                List<string> rids = [];

                foreach (XElement each in identities)
                {
                    if (!TryResourceId(each, out Part one, out fault))
                    {
                        return false;
                    }

                    rids.AddRange(one.Ids);
                }

                filter = new ParsedFilter(null, null, null, rids);
                return true;
            }
        }

        if (body is null)
        {
            fault = WfsFault.Invalid("filter", "The filter is empty.");
            return false;
        }

        if (!TryPart(body, fields, layerSrid, 0, geometryColumn, out Part part, out fault))
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
        string? geometryColumn,
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
            "And" => TryLogical(element, fields, layerSrid, depth, geometryColumn, and: true, out part, out fault),
            "Or" => TryLogical(element, fields, layerSrid, depth, geometryColumn, and: false, out part, out fault),
            "Not" => TryNot(element, fields, layerSrid, depth, geometryColumn, out part, out fault),
            "ResourceId" => TryResourceId(element, out part, out fault),
            "PropertyIsNull" => TryNull(element, fields, geometryColumn, negated: false, out part, out fault),

            // <b>Its own operator in Filter Encoding 2.0, and it was simply missing.</b>
            // `AttributePredicate.IsNull` already carried a `Negated` flag for the ArcGIS
            // front end, so the whole of this was one case and one argument. The OGC suite
            // charges 25 failures for it — one per feature type — which is what makes a
            // missing operator look like a catastrophe in a conformance report and a
            // one-line change in the code.
            "PropertyIsNotNull" => TryNull(element, fields, geometryColumn, negated: true, out part, out fault),

            /*
              <b>`PropertyIsNil` reads as `PropertyIsNull` here, and the reason is the
              store rather than convenience — added 2026-08-26.</b> Filter Encoding 2.0
              separates the two: `PropertyIsNull` asks whether the property is *absent*,
              `PropertyIsNil` whether it is present and carries `xsi:nil="true"`. A
              relational column has one representation for both — `NULL` — so this server
              cannot tell them apart, and answering the same for both is the only honest
              mapping available to it. A store that could distinguish them would have to
              answer differently, which is why this is stated here rather than left to be
              inferred from the code.

              <b>Found by running the WFS 2.0 CITE suite rather than by reading the
              specification.</b> `PropertyIsNilOperatorTests` expects 200 and this server
              answered 400 `OperationNotSupported`; the recorded 2026-08-23 evidence did
              not show it, because that run left the test untested. That is
              [D-158](../../docs/architecture-debt.md)'s whole argument arriving as a
              defect rather than as a warning.
            */
            "PropertyIsNil" => TryNil(element, fields, geometryColumn, out part, out fault),
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
            + "PropertyIsLike, PropertyIsNull, PropertyIsNotNull, PropertyIsNil, "
            + "PropertyIsBetween, BBOX, Intersects, Within, "
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
        string? geometryColumn,
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
            if (!TryPart(child, fields, layerSrid, depth + 1, geometryColumn, out Part one, out fault))
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
        string? geometryColumn,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;

        if (element.Elements().FirstOrDefault() is not { } child)
        {
            fault = WfsFault.Invalid("filter", "'fes:Not' needs one operand.");
            return false;
        }

        if (!TryPart(child, fields, layerSrid, depth + 1, geometryColumn, out Part inner, out fault))
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

        if (!TryMatchCase(element, out bool ignoreCase, out fault)
            || !TryProperty(element, fields, out FieldDescription field, out fault)
            || !TryLiteral(element, field, Literal(element, 0), out object? value, out fault))
        {
            return false;
        }

        part = new Part(
            new AttributePredicate.Comparison(field.Name, op, value, ignoreCase),
            null,
            null,
            []);

        return true;
    }

    /// <summary>Reads <c>fes:PropertyIsNil</c>, which this store answers as null.</summary>
    /// <remarks>
    /// <b><c>nilReason</c> is refused rather than ignored.</b> The attribute asks *why* the
    /// value is nil — `inapplicable`, `missing`, `withheld` and the rest — and a `NULL`
    /// column records no reason at all. Ignoring it would answer a narrower question than
    /// the one asked and call it the same answer, which is the shape of a silent wrong
    /// result rather than a missing feature.
    /// </remarks>
    private static bool TryNil(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        string? geometryColumn,
        out Part part,
        out WfsFault? fault)
    {
        if (element.Attribute("nilReason") is { } reason)
        {
            part = Part.Empty;

            fault = new WfsFault(
                WfsFaultCode.OperationNotSupported,
                "filter",
                $"'fes:PropertyIsNil' with nilReason='{reason.Value}' asks why a value is "
                + "absent, and this server stores values in a relational column whose only "
                + "answer is that it is null — it records no reason, so it cannot match one. "
                + "Drop the attribute to ask whether the value is absent at all.");

            return false;
        }

        return TryNull(element, fields, geometryColumn, negated: false, out part, out fault);
    }

    private static bool TryNull(
        XElement element,
        IReadOnlyList<FieldDescription> fields,
        string? geometryColumn,
        bool negated,
        out Part part,
        out WfsFault? fault)
    {
        part = Part.Empty;
        fault = null;

        /*
          <b>The geometry counts as a property here, and only here.</b>
          `DescribeFeatureType` publishes the geometry as an element of the feature type —
          it has to, a client needs to know its name and type — while `described.Fields`
          holds the attribute columns alone. So this server's own schema said `geom`
          existed and this server's own filter said it did not, about the same layer, in
          the same request. That is two parts of one server disagreeing, which is the
          class the consistency gate exists for, and the OGC suite charges 23 failures for
          it.

          <b>Asking whether a geometry is null is a real question.</b> A row whose geometry
          column is empty is a row nothing can draw and nothing spatial can match, and
          finding those is ordinary data work. Comparing a geometry to a literal is not a
          real question, so `PropertyIsEqualTo` and its family still refuse it with the
          sentence below — a spatial predicate is how you ask that.
        */
        /*
          <b>GML's own properties exist on every feature type and this server holds none
          of them.</b> A feature type derived from `gml:AbstractFeatureType` inherits
          `gml:name`, `gml:description` and `gml:identifier`, all optional. That is not a
          courtesy of the schema — it is what the derivation means, and our
          `DescribeFeatureType` declares that derivation. So a filter naming `gml:name` is
          valid against every layer here, and this refused it: *'name' is not a property of
          this feature type*, 24 times in the OGC suite, once per layer.

          <b>Nothing populates them, so the answer is known without asking the database.</b>
          Every feature's `gml:name` is absent, so `PropertyIsNull` matches everything and
          `PropertyIsNotNull` matches nothing. Both are answered here rather than in SQL,
          because there is no column to ask about.

          <b>The namespace decides, not the local name.</b> `look_buildings` has an
          attribute column called `name`, and a client filtering on that must keep getting
          its own column. Only a reference whose prefix resolves to the GML 3.2 namespace
          is one of these.
        */
        if (TryGmlProperty(element, out fault))
        {
            part = new Part(
                negated
                    ? new AttributePredicate.MatchesNothing()
                    : new AttributePredicate.Negation(new AttributePredicate.MatchesNothing()),
                null,
                null,
                []);

            return true;
        }

        if (fault is not null)
        {
            return false;
        }

        if (geometryColumn is { Length: > 0 }
            && TryGeometryReference(element, geometryColumn, out fault))
        {
            part = new Part(new AttributePredicate.IsNull(geometryColumn, negated), null, null, []);
            return true;
        }

        if (fault is not null)
        {
            return false;
        }

        if (!TryProperty(element, fields, out FieldDescription field, out fault))
        {
            return false;
        }

        part = new Part(new AttributePredicate.IsNull(field.Name, negated), null, null, []);
        return true;
    }

    /// <summary>
    /// Whether this predicate's ValueReference names one of GML's own properties.
    /// </summary>
    /// <remarks>
    /// Returns false with no fault when it names something else, so the caller falls
    /// through to the geometry and then to the ordinary field lookup.
    /// </remarks>
    private static bool TryGmlProperty(XElement element, out WfsFault? fault)
    {
        fault = null;

        XNamespace fes = WfsNames.Fes;

        XElement? reference = element.Element(fes + "ValueReference")
            ?? element.Element(fes + "PropertyName");

        if (reference is null)
        {
            return false;
        }

        string text = reference.Value.Trim();
        int colon = text.LastIndexOf(':');

        // Unprefixed means the feature type's own namespace, which is never GML's.
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        XNamespace bound = reference.GetNamespaceOfPrefix(text[..colon]) ?? XNamespace.None;

        return bound == XNamespace.Get(GmlNamespace)
            && GmlProperties.Contains(text[(colon + 1)..]);
    }

    /// <summary>GML 3.2's own namespace, which a prefix must resolve to.</summary>
    private const string GmlNamespace = "http://www.opengis.net/gml/3.2";

    /// <summary>
    /// The properties every feature type inherits and this server never populates.
    /// </summary>
    /// <remarks>
    /// <c>gml:boundedBy</c> is deliberately absent: it is derived from the geometry rather
    /// than stored, so a filter on it has an answer this could not give without asking the
    /// database, and answering it as *always absent* would be a wrong answer rather than a
    /// missing one.
    /// </remarks>
    private static readonly HashSet<string> GmlProperties =
        new(StringComparer.Ordinal) { "name", "description", "identifier" };

    /// <summary>Whether this predicate's ValueReference names the geometry column.</summary>
    /// <remarks>
    /// Returns false with no fault when it names something else, so the caller falls
    /// through to the ordinary field lookup and that lookup writes the message.
    /// </remarks>
    private static bool TryGeometryReference(
        XElement element, string geometryColumn, out WfsFault? fault)
    {
        fault = null;

        XNamespace fes = WfsNames.Fes;

        XElement? reference = element.Element(fes + "ValueReference")
            ?? element.Element(fes + "PropertyName");

        if (reference is null || string.IsNullOrWhiteSpace(reference.Value))
        {
            return false;
        }

        if (!ValueReference.TryLocalName(reference.Value, out string local, out fault))
        {
            return false;
        }

        return string.Equals(local, geometryColumn, StringComparison.OrdinalIgnoreCase);
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

        if (!TryMatchCase(element, out bool ignoreCase, out fault)
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
                Negated: false,
                IgnoreCase: ignoreCase),
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

    /// <summary>Reads <c>matchCase</c>, which is true unless it says otherwise.</summary>
    /// <remarks>
    /// <para>
    /// <b>This used to refuse <c>matchCase="false"</c>, and refusing was right at the
    /// time.</b> The query model had no case-insensitive comparison, and answering a
    /// case-sensitive comparison to a caller who asked for a case-insensitive one is a
    /// wrong answer rather than a smaller one — so an `OperationNotSupported` was the
    /// truthful reply. CITE's `propertyIsEqualTo_caseSensitive` and
    /// `propertyIsNotEqualTo_caseSensitive` reported it as 400 where 200 was due, which
    /// is not the refusal being wrong but the capability being absent.
    /// </para>
    /// <para>
    /// <b>The capability is there now</b> —
    /// <see cref="AttributePredicate.Comparison.IgnoreCase"/> — so the attribute is read
    /// rather than resisted. Filter Encoding 2.0 §7.7.3.2 defaults it to true; anything
    /// that is not parseable as a boolean is a bad value rather than a false one, because
    /// `matchCase="yes"` silently meaning *case-insensitive* is the kind of leniency
    /// nobody can debug.
    /// </para>
    /// </remarks>
    private static bool TryMatchCase(XElement element, out bool ignoreCase, out WfsFault? fault)
    {
        fault = null;
        ignoreCase = false;

        if ((string?)element.Attribute("matchCase") is not { } matchCase)
        {
            return true;
        }

        if (!bool.TryParse(matchCase, out bool value))
        {
            fault = WfsFault.Invalid(
                "filter",
                $"matchCase=\"{matchCase}\" is neither 'true' nor 'false'.");

            return false;
        }

        ignoreCase = !value;
        return true;
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

        // <b>A GML feature has properties no column corresponds to, and refusing them
        // as unknown is the wrong refusal.</b> Every `gml:AbstractFeature` carries
        // `boundedBy`, so a filter naming it is not naming something that does not
        // exist — it is asking for a comparison against an envelope, which this server
        // understands and cannot carry out. OWS Common has a code for exactly that
        // distinction and CITE's `invalidOperand_boundedBy` asserts it:
        // `OperationProcessingFailed` means *understood, and no*, while
        // `InvalidParameterValue` means *that is not a thing*, and sending the second
        // tells a client to look for a typo it will not find.
        if (Gml.Contains(local))
        {
            fault = new WfsFault(
                WfsFaultCode.OperationProcessingFailed,
                "filter",
                $"'gml:{local}' is a property every GML feature has and not one this server "
                + "can compare. Use a spatial operator against the geometry property, which "
                + "DescribeFeatureType names.");

            return false;
        }

        fault = WfsFault.Invalid(
            "filter",
            $"'{local}' is not a property of this feature type. A filter may only mention "
            + "properties the schema lists.");

        return false;
    }

    /// <summary>
    /// The properties GML gives every feature, which no column of ours corresponds to.
    /// </summary>
    /// <remarks>
    /// <b>Consulted only after the layer's own columns, so a real column of the same
    /// name always wins.</b> `name` and `description` are ordinary column names, and a
    /// layer that has one must be filterable on it; this list exists to make the
    /// *refusal* accurate for the case where it does not.
    /// </remarks>
    private static readonly HashSet<string> Gml = new(StringComparer.OrdinalIgnoreCase)
    {
        "boundedBy", "name", "description", "identifier", "location",
    };

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

            case FieldType.Date:
                /*
                  <b>Bound as a moment rather than as the text it arrived as, which is
                  [Q-124](../../../docs/open-questions.md).</b> This used to fall through
                  to the default and bind a string, so `observed_at = '2026-08-02'`
                  reached PostgreSQL as `timestamp with time zone = text` and came back
                  as `42883: operator does not exist` — a 400 saying nothing useful, on
                  the filter an incident layer or an alert feed most wants. CITE's
                  `propertyIsEqualTo_caseSensitive` is what finally named it, having
                  compared a feature against a value this server had just published.

                  <b>The old comment said the two front ends were kept equally unable on
                  purpose, and that reasoning was sound and is now spent.</b> The
                  divergence it feared was WFS converting while the ArcGIS `where` did
                  not; both convert now, and the OGC API Features face has converted this
                  way since it was written — Q-124's recommendation was to teach the
                  provider, and the answer turned out to be sitting in the third surface
                  all along.

                  <b>UTC when the literal does not say, which is a choice and not a
                  default.</b> `AssumeUniversal | AdjustToUniversal` is what the OGC face
                  uses, so a bare `2026-08-02` means midnight UTC on all three surfaces
                  rather than midnight wherever the server happens to be — a server whose
                  answers move when its time zone is reconfigured is worse than one that
                  is arguably an hour off.
                */
                if (!DateTimeOffset.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset moment))
                {
                    fault = Mismatch(field, text, "a date or a date and time");
                    return false;
                }

                value = moment;
                return true;

            default:
                // Text, uuid and binary bind as text, which is what they are.
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
