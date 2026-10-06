using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Graticula.Api.Wms;

/// <summary>One layer an SLD styles: its name, and the <c>drawingInfo</c> its rules say, or null for its own style.</summary>
/// <param name="Name">The WMS layer it names.</param>
/// <param name="DrawingInfo">An Esri <c>drawingInfo</c> with its renderer, or null where the SLD names the default.</param>
/// <param name="Losses">What the SLD said that this server does not draw.</param>
public sealed record SldLayer(string Name, JsonObject? DrawingInfo, IReadOnlyList<string> Losses);

/// <summary>
/// SLD at the boundary — ADR-171: read from <c>SLD_BODY</c> into an Esri <c>drawingInfo</c>, written from one for
/// <c>GetStyles</c>, and never stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>A serialisation at the edge and never the model</b>, as ADR-033 §2E said it would have to be if it ever came. The
/// layer's canonical document stays CIM (ADR-052); <c>drawingInfo</c> is the vocabulary this server already reads in
/// and writes out with its losses named, so SLD is translated to and from it rather than to a fourth dialect.
/// </para>
/// <para>
/// <b>SLD 1.0 and 1.1 are read alike</b> — by local name, so <c>sld:CssParameter</c> and <c>se:SvgParameter</c>, an
/// <c>ogc:Filter</c> and a <c>fes:Filter</c>, are the same thing here. Written as 1.1.0 with Symbology Encoding.
/// </para>
/// <para>
/// <b>What a rule may filter by is what a <c>drawingInfo</c> can classify by</b>: nothing (one symbol), equality on
/// one field (unique values, with <c>ElseFilter</c> as the default symbol), or ranges on one numeric field (class
/// breaks). Anything else is refused by name rather than drawn as though the filter were not there.
/// </para>
/// </remarks>
public static class StyledLayerDescriptor
{
    private const string SldNs = "http://www.opengis.net/sld";
    private const string SeNs = "http://www.opengis.net/se";
    private const string OgcNs = "http://www.opengis.net/ogc";
    private const string XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The most an <c>SLD_BODY</c> may hold.</summary>
    public const int MaximumLength = 64 * 1024;

    /// <summary>A CSS pixel in points — SLD measures in pixels, a <c>drawingInfo</c> in points.</summary>
    private const double PointsPerPixel = 0.75;

    /// <summary>Reads an SLD document.</summary>
    /// <param name="text">The document.</param>
    /// <param name="layers">Each named layer and what its style says.</param>
    /// <param name="error">Why it was refused, when it was.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryRead(string text, out IReadOnlyList<SldLayer> layers, out string? error)
    {
        layers = [];
        error = null;

        if (text.Length > MaximumLength)
        {
            error = $"SLD_BODY is {text.Length:N0} characters and this server reads at most {MaximumLength:N0}.";
            return false;
        }

        XDocument document;

        try
        {
            using XmlReader reader = XmlReader.Create(
                new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            error = $"SLD_BODY is not XML: {e.Message}";
            return false;
        }

        if (document.Root is not { } root || root.Name.LocalName != "StyledLayerDescriptor")
        {
            error = "SLD_BODY is not a StyledLayerDescriptor.";
            return false;
        }

        if (Children(root, "UserLayer").Any())
        {
            error = "SLD_BODY has a UserLayer. This server styles the layers it publishes (NamedLayer) and does not "
                + "draw features sent with the request.";
            return false;
        }

        List<SldLayer> read = [];

        foreach (XElement named in Children(root, "NamedLayer"))
        {
            string? name = Child(named, "Name")?.Value.Trim();

            if (string.IsNullOrEmpty(name))
            {
                error = "A NamedLayer in SLD_BODY has no Name.";
                return false;
            }

            if (Children(named, "UserStyle").FirstOrDefault() is not { } style)
            {
                // A NamedStyle: the layer's own, which is the one style a layer here has (ADR-041 §5.2).
                string? styleName = Child(Child(named, "NamedStyle"), "Name")?.Value.Trim();

                if (styleName is { Length: > 0 } && !string.Equals(styleName, "default", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"`{styleName}` is not a style this server defines for `{name}`. A layer here has one style, its "
                        + "own (`default`); send the style itself in a UserStyle instead.";
                    return false;
                }

                read.Add(new SldLayer(name, null, []));
                continue;
            }

            List<string> losses = [];

            if (Children(named, "UserStyle").Skip(1).Any())
            {
                losses.Add($"`{name}` has more than one UserStyle; the first is drawn.");
            }

            if (!TryRenderer(name, style, losses, out JsonObject? renderer, out error))
            {
                return false;
            }

            read.Add(new SldLayer(name, new JsonObject { ["renderer"] = renderer }, losses));
        }

        if (read.Count == 0)
        {
            error = "SLD_BODY names no layer: it has no NamedLayer.";
            return false;
        }

        layers = read;
        return true;
    }

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent?.Elements().Where(e => e.Name.LocalName == name) ?? [];

    private static XElement? Child(XElement? parent, string name) => Children(parent, name).FirstOrDefault();

    private static string Where(string layer, XElement rule) =>
        Child(rule, "Name")?.Value.Trim() is { Length: > 0 } name ? $"`{layer}` rule `{name}`" : $"a rule of `{layer}`";

    /// <summary>What one rule's filter says, reduced to what a <c>drawingInfo</c> can classify by.</summary>
    private sealed record Condition(string? Field, string? Value, double? Lower, double? Upper, bool Else);

    private static bool TryRenderer(string layer, XElement style, List<string> losses, out JsonObject? renderer, out string? error)
    {
        renderer = null;
        error = null;
        List<(Condition Condition, JsonObject Symbol, string Label)> rules = [];

        foreach (XElement rule in Children(style, "FeatureTypeStyle").SelectMany(f => Children(f, "Rule")))
        {
            string where = Where(layer, rule);

            if (Child(rule, "MinScaleDenominator") is not null || Child(rule, "MaxScaleDenominator") is not null)
            {
                losses.Add($"{where} has a scale range; it is drawn at every scale.");
            }

            if (!TryCondition(where, rule, out Condition? condition, out error)
                || !TrySymbol(where, rule, losses, out JsonObject? symbol, out error))
            {
                return false;
            }

            string label = Child(Child(rule, "Description"), "Title")?.Value.Trim() is { Length: > 0 } title
                ? title
                : Child(rule, "Title")?.Value.Trim() is { Length: > 0 } old ? old : Child(rule, "Name")?.Value.Trim() ?? string.Empty;
            rules.Add((condition!, symbol!, label));
        }

        if (rules.Count == 0)
        {
            error = $"The UserStyle for `{layer}` has no Rule, so it draws nothing.";
            return false;
        }

        List<(Condition Condition, JsonObject Symbol, string Label)> classes = [.. rules.Where(r => r.Condition.Field is not null)];
        List<(Condition Condition, JsonObject Symbol, string Label)> plain = [.. rules.Where(r => r.Condition.Field is null && !r.Condition.Else)];
        JsonObject? otherwise = rules.FirstOrDefault(r => r.Condition.Else).Symbol;

        if (classes.Count == 0)
        {
            if (plain.Count > 1)
            {
                losses.Add($"`{layer}` has {plain.Count} unfiltered rules, which draw over each other; the first is drawn.");
            }

            renderer = new JsonObject
            {
                ["type"] = "simple",
                ["symbol"] = (plain.Count > 0 ? plain[0].Symbol : otherwise)!.DeepClone(),
                ["label"] = plain.Count > 0 ? plain[0].Label : string.Empty,
            };

            return true;
        }

        if (plain.Count > 0)
        {
            error = $"`{layer}` mixes rules with a filter and rules without one, so some features would be drawn twice. "
                + "This server draws one symbol a feature; give the unfiltered rule an ElseFilter to make it the default.";
            return false;
        }

        if (classes.Select(r => r.Condition.Field).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            error = $"`{layer}`'s rules filter by more than one property; this server classifies by one.";
            return false;
        }

        string field = classes[0].Condition.Field!;

        if (classes.All(r => r.Condition.Value is not null))
        {
            renderer = new JsonObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = field,
                ["uniqueValueInfos"] = new JsonArray([.. classes.Select(r => (JsonNode)new JsonObject
                {
                    ["value"] = r.Condition.Value,
                    ["label"] = r.Label.Length > 0 ? r.Label : r.Condition.Value,
                    ["symbol"] = r.Symbol.DeepClone(),
                })]),
            };
        }
        else if (classes.All(r => r.Condition.Value is null))
        {
            if (classes.Any(r => r.Condition.Upper is null))
            {
                error = $"A range rule of `{layer}` has no upper bound. Class breaks are read by their upper bounds; give "
                    + "the last class one at the field's largest value.";
                return false;
            }

            List<(Condition Condition, JsonObject Symbol, string Label)> ordered = [.. classes.OrderBy(r => r.Condition.Upper)];
            double floor = ordered.Select(r => r.Condition.Lower).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Min();

            renderer = new JsonObject
            {
                ["type"] = "classBreaks",
                ["field"] = field,
                ["minValue"] = floor,
                ["classBreakInfos"] = new JsonArray([.. ordered.Select(r => (JsonNode)new JsonObject
                {
                    ["classMaxValue"] = r.Condition.Upper,
                    ["label"] = r.Label,
                    ["symbol"] = r.Symbol.DeepClone(),
                })]),
            };
        }
        else
        {
            error = $"`{layer}`'s rules mix equality and range filters; this server classifies by one or the other.";
            return false;
        }

        if (otherwise is not null)
        {
            renderer["defaultSymbol"] = otherwise.DeepClone();
        }

        return true;
    }

    private static bool TryCondition(string where, XElement rule, out Condition? condition, out string? error)
    {
        error = null;
        condition = new Condition(null, null, null, null, Child(rule, "ElseFilter") is not null);

        if (Child(rule, "Filter") is not { } filter)
        {
            return true;
        }

        XElement? test = filter.Elements().FirstOrDefault();
        IReadOnlyList<XElement> parts = test?.Name.LocalName == "And" ? [.. test.Elements()] : test is null ? [] : [test];
        string? field = null, equals = null;
        double? lower = null, upper = null;

        foreach (XElement part in parts)
        {
            string? property = Child(part, "PropertyName")?.Value.Trim() ?? Child(part, "ValueReference")?.Value.Trim();
            string? literal = Child(part, "Literal")?.Value;

            if (part.Name.LocalName == "PropertyIsBetween")
            {
                literal = null;
                lower = Number(Child(Child(part, "LowerBoundary"), "Literal")?.Value);
                upper = Number(Child(Child(part, "UpperBoundary"), "Literal")?.Value);

                if (lower is null || upper is null)
                {
                    error = $"{where}'s PropertyIsBetween needs a number at each boundary.";
                    return false;
                }
            }

            if (property is null || (literal is null && part.Name.LocalName != "PropertyIsBetween"))
            {
                error = $"{where} filters with `{part.Name.LocalName}`. This server reads PropertyIsEqualTo, the four "
                    + "comparisons and PropertyIsBetween, each on one property against a literal, alone or joined by And.";
                return false;
            }

            if (field is not null && !string.Equals(field, property, StringComparison.Ordinal))
            {
                error = $"{where} filters by `{field}` and `{property}`; this server classifies by one property.";
                return false;
            }

            field = property;

            switch (part.Name.LocalName)
            {
                case "PropertyIsEqualTo":
                    equals = literal!.Trim();
                    break;
                case "PropertyIsLessThan" or "PropertyIsLessThanOrEqualTo":
                    upper = Number(literal) ?? double.NaN;
                    break;
                case "PropertyIsGreaterThan" or "PropertyIsGreaterThanOrEqualTo":
                    lower = Number(literal) ?? double.NaN;
                    break;
                case "PropertyIsBetween":
                    break;
                default:
                    error = $"{where} filters with `{part.Name.LocalName}`. This server reads PropertyIsEqualTo, the four "
                        + "comparisons and PropertyIsBetween.";
                    return false;
            }
        }

        if (double.IsNaN(lower ?? 0) || double.IsNaN(upper ?? 0))
        {
            error = $"{where} compares `{field}` with something that is not a number.";
            return false;
        }

        if (equals is not null && (lower is not null || upper is not null))
        {
            error = $"{where} joins an equality and a range; this server reads one or the other.";
            return false;
        }

        condition = condition with { Field = field, Value = equals, Lower = lower, Upper = upper };
        return true;
    }

    private static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    /// <summary>An SVG or CSS parameter of a Fill or Stroke, by name.</summary>
    private static string? Parameter(XElement? owner, string name) =>
        owner?.Elements().FirstOrDefault(e => e.Name.LocalName is "SvgParameter" or "CssParameter"
            && (string?)e.Attribute("name") == name)?.Value.Trim();

    private static JsonArray Colour(string? hex, string? opacity)
    {
        byte r = 128, g = 128, b = 128;

        if (hex is { Length: 7 } && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            (r, g, b) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        int alpha = (int)Math.Round(Math.Clamp(Number(opacity) ?? 1, 0, 1) * 255);
        return [r, g, b, alpha];
    }

    private static JsonObject Line(XElement stroke)
    {
        double width = (Number(Parameter(stroke, "stroke-width")) ?? 1) * PointsPerPixel;

        return new JsonObject
        {
            ["type"] = "esriSLS",
            ["style"] = Parameter(stroke, "stroke-dasharray") is { Length: > 0 } ? "esriSLSDash" : "esriSLSSolid",
            ["color"] = Colour(Parameter(stroke, "stroke") ?? "#000000", Parameter(stroke, "stroke-opacity")),
            ["width"] = Math.Round(width, 3),
        };
    }

    private static bool TrySymbol(string where, XElement rule, List<string> losses, out JsonObject? symbol, out string? error)
    {
        symbol = null;
        error = null;
        XElement? polygon = Child(rule, "PolygonSymbolizer"), line = Child(rule, "LineSymbolizer"), point = Child(rule, "PointSymbolizer");

        foreach (string other in rule.Elements().Select(e => e.Name.LocalName).Where(n => n.EndsWith("Symbolizer", StringComparison.Ordinal)
                     && n is not ("PolygonSymbolizer" or "LineSymbolizer" or "PointSymbolizer")).Distinct())
        {
            losses.Add($"{where} has a {other}, which this server does not draw from an SLD.");
        }

        if (polygon is not null)
        {
            XElement? fill = Child(polygon, "Fill");
            XElement? outline = Child(polygon, "Stroke") ?? Child(line, "Stroke");

            symbol = new JsonObject
            {
                ["type"] = "esriSFS",
                ["style"] = "esriSFSSolid",

                // No Fill is no fill: transparent, not the grey a missing colour would be.
                ["color"] = fill is null ? new JsonArray(0, 0, 0, 0) : Colour(Parameter(fill, "fill") ?? "#808080", Parameter(fill, "fill-opacity")),
            };

            if (outline is not null)
            {
                symbol["outline"] = Line(outline);
            }

            return true;
        }

        if (line is not null)
        {
            if (Child(line, "Stroke") is not { } stroke)
            {
                error = $"{where}'s LineSymbolizer has no Stroke, so it draws nothing.";
                return false;
            }

            symbol = Line(stroke);
            return true;
        }

        if (point is not null)
        {
            XElement? graphic = Child(point, "Graphic");
            XElement? mark = Child(graphic, "Mark");

            if (mark is null)
            {
                losses.Add($"{where}'s point is an ExternalGraphic; it is drawn as a grey circle.");
            }
            else if (Child(mark, "WellKnownName")?.Value.Trim() is { Length: > 0 } shape && shape != "circle")
            {
                losses.Add($"{where}'s marker is a {shape}; this server draws markers as circles.");
            }

            XElement? fill = Child(mark, "Fill");
            symbol = new JsonObject
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["color"] = mark is not null && fill is null ? new JsonArray(0, 0, 0, 0) : Colour(Parameter(fill, "fill") ?? "#808080", Parameter(fill, "fill-opacity")),
                ["size"] = Math.Round((Number(Child(graphic, "Size")?.Value) ?? 6) * PointsPerPixel, 3),
            };

            if (Child(mark, "Stroke") is { } outline)
            {
                symbol["outline"] = Line(outline);
            }

            return true;
        }

        error = $"{where} has no PolygonSymbolizer, LineSymbolizer or PointSymbolizer, so it draws nothing.";
        return false;
    }

    // ---------- Writing ----------

    /// <summary>Writes an SLD 1.1.0 document, a NamedLayer a layer.</summary>
    /// <param name="layers">Each layer's name, its <c>drawingInfo</c>, and what the <c>drawingInfo</c> lost.</param>
    /// <param name="styleName">
    /// What each <c>UserStyle</c> is called. <c>default</c> for WMS, where it is the one style a layer has (ADR-041
    /// §5.2); OGC API Styles passes the style's id, which its <c>/rec/sld-se/style-names</c> asks for (ADR-176).
    /// </param>
    /// <param name="namesFeatureTypes">
    /// Whether each <c>FeatureTypeStyle</c> names its layer in <c>se:FeatureTypeName</c> — OGC API Styles'
    /// <c>/rec/sld-se/style-names</c> C. Off for WMS, whose <c>GetStyles</c> answer is left as it was.
    /// </param>
    /// <returns>The document.</returns>
    public static string Write(
        IReadOnlyList<(string Name, JsonNode? DrawingInfo, IReadOnlyList<string> Losses)> layers,
        string styleName = "default",
        bool namesFeatureTypes = false) =>
        Serialise(Build(layers, styleName, namesFeatureTypes));

    /// <summary>Writes the same document as SLD 1.0.0 — ADR-176.</summary>
    /// <param name="layers">As <see cref="Write"/>.</param>
    /// <param name="styleName">As <see cref="Write"/>.</param>
    /// <param name="namesFeatureTypes">As <see cref="Write"/>.</param>
    /// <returns>The document.</returns>
    /// <remarks>
    /// <para>
    /// <b>Translated from the 1.1 tree, not written a second time.</b> SLD 1.0 is SLD 1.1 before Symbology Encoding
    /// was split out of it: the same elements in the <c>sld</c> namespace, <c>CssParameter</c> where SE says
    /// <c>SvgParameter</c>, and a <c>Title</c> and <c>Abstract</c> directly where SE wraps them in a
    /// <c>Description</c>. A second writer would be a second set of rules for which renderer becomes which rule, and
    /// the two would drift; a rename cannot.
    /// </para>
    /// <para>
    /// <b>Checked against both schemas on 2026-10-06</b>, with lxml and the files at <c>schemas.opengis.net</c>, for a
    /// simple, a unique-value and a class-breaks layer and a layer with no SLD form.
    /// </para>
    /// </remarks>
    public static string Write10(
        IReadOnlyList<(string Name, JsonNode? DrawingInfo, IReadOnlyList<string> Losses)> layers,
        string styleName = "default",
        bool namesFeatureTypes = false)
    {
        XElement eleven = Build(layers, styleName, namesFeatureTypes);
        XNamespace sld = SldNs, xsi = XsiNs;

        XElement root = new(sld + "StyledLayerDescriptor",
            new XAttribute("version", "1.0.0"),
            new XAttribute(XNamespace.Xmlns + "sld", SldNs),
            new XAttribute(XNamespace.Xmlns + "ogc", OgcNs),
            new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
            new XAttribute(xsi + "schemaLocation", $"{SldNs} http://schemas.opengis.net/sld/1.0.0/StyledLayerDescriptor.xsd"),
            eleven.Nodes().Select(To10));

        return Serialise(root);
    }

    /// <summary>One node of a 1.1 document as SLD 1.0 has it.</summary>
    private static object To10(XNode node)
    {
        if (node is not XElement element)
        {
            return node is XComment comment ? new XComment(comment.Value) : node;
        }

        XNamespace sld = SldNs;

        // The ogc filter namespace is the same in both versions; only SE's and SLD's own elements move.
        if (element.Name.NamespaceName == OgcNs)
        {
            return new XElement(element);
        }

        string local = element.Name.LocalName switch
        {
            "SvgParameter" => "CssParameter",
            _ => element.Name.LocalName,
        };

        XElement copy = new(sld + local, element.Attributes());

        foreach (XNode child in element.Nodes())
        {
            // SE's Description holds Title and Abstract; SLD 1.0 puts them straight in their parent.
            if (child is XElement { Name.LocalName: "Description" } description && description.Name.NamespaceName == SeNs)
            {
                copy.Add(description.Elements().Select(To10));
                continue;
            }

            copy.Add(To10(child));
        }

        return copy;
    }

    private static string Serialise(XElement root)
    {
        StringBuilder text = new();

        using (XmlWriter w = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
        {
            new XDocument(root).Save(w);
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + text;
    }

    private static XElement Build(
        IReadOnlyList<(string Name, JsonNode? DrawingInfo, IReadOnlyList<string> Losses)> layers,
        string styleName,
        bool namesFeatureTypes)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentException.ThrowIfNullOrWhiteSpace(styleName);
        XNamespace sld = SldNs, se = SeNs, ogc = OgcNs, xsi = XsiNs;

        XElement root = new(sld + "StyledLayerDescriptor",
            new XAttribute("version", "1.1.0"),
            new XAttribute(XNamespace.Xmlns + "sld", SldNs),
            new XAttribute(XNamespace.Xmlns + "se", SeNs),
            new XAttribute(XNamespace.Xmlns + "ogc", OgcNs),
            new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
            new XAttribute(xsi + "schemaLocation", $"{SldNs} http://schemas.opengis.net/sld/1.1.0/StyledLayerDescriptor.xsd"));

        foreach ((string name, JsonNode? drawingInfo, IReadOnlyList<string> losses) in layers)
        {
            XElement named = new(sld + "NamedLayer", new XElement(se + "Name", name));
            List<string> lost = [.. losses];
            List<XElement> rules = Rules(drawingInfo?["renderer"] as JsonObject, se, ogc, lost);

            if (rules.Count == 0)
            {
                named.Add(new XComment($" {Comment(string.Join(" ", lost))} "));
                named.Add(new XElement(sld + "NamedStyle", new XElement(se + "Name", "default")));
            }
            else
            {
                XElement style = new(sld + "UserStyle", new XElement(se + "Name", styleName));

                if (lost.Count > 0)
                {
                    style.Add(new XElement(se + "Description",
                        new XElement(se + "Title", styleName),
                        new XElement(se + "Abstract", "Not carried into SLD: " + string.Join(" ", lost))));
                }

                XElement featureTypeStyle = new(se + "FeatureTypeStyle");

                if (namesFeatureTypes)
                {
                    featureTypeStyle.Add(new XElement(se + "FeatureTypeName", name));
                }

                featureTypeStyle.Add(rules);
                style.Add(new XElement(sld + "IsDefault", "1"), featureTypeStyle);
                named.Add(style);
            }

            root.Add(named);
        }

        return root;
    }

    private static string Comment(string text) => text.Replace("--", "- -", StringComparison.Ordinal);

    private static List<XElement> Rules(JsonObject? renderer, XNamespace se, XNamespace ogc, List<string> lost)
    {
        List<XElement> rules = [];
        string? kind = (string?)renderer?["type"];

        XElement Rule(string? title, XElement? filter, JsonNode? symbol)
        {
            XElement rule = new(se + "Rule");

            if (title is { Length: > 0 })
            {
                rule.Add(new XElement(se + "Name", title), new XElement(se + "Description", new XElement(se + "Title", title)));
            }

            if (filter is not null)
            {
                rule.Add(filter);
            }

            rule.Add(Symbolizer(symbol as JsonObject, se, lost));
            return rule;
        }

        XElement Compare(string op, string field, JsonNode? value) =>
            new(ogc + op, new XElement(ogc + "PropertyName", field), new XElement(ogc + "Literal", Literal(value)));

        switch (kind)
        {
            case "simple":
                rules.Add(Rule((string?)renderer!["label"], null, renderer["symbol"]));
                break;

            case "uniqueValue" when (string?)renderer!["field1"] is { } field:
                if (renderer["field2"] is not null)
                {
                    lost.Add("A renderer by several fields is written by its first.");
                }

                foreach (JsonObject info in (renderer["uniqueValueInfos"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    rules.Add(Rule((string?)info["label"], new XElement(ogc + "Filter", Compare("PropertyIsEqualTo", field, info["value"])), info["symbol"]));
                }

                break;

            case "classBreaks" when (string?)renderer!["field"] is { } field:
                double? below = D(renderer["minValue"]);

                foreach (JsonObject info in (renderer["classBreakInfos"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    List<XElement> tests = [];

                    if (below is { } low)
                    {
                        tests.Add(Compare(rules.Count == 0 ? "PropertyIsGreaterThanOrEqualTo" : "PropertyIsGreaterThan", field, low));
                    }

                    if (info["classMaxValue"] is { } high)
                    {
                        tests.Add(Compare("PropertyIsLessThanOrEqualTo", field, high));
                        below = D(high);
                    }

                    XElement filter = tests.Count == 1 ? new XElement(ogc + "Filter", tests[0]) : new XElement(ogc + "Filter", new XElement(ogc + "And", tests));
                    rules.Add(Rule((string?)info["label"], filter, info["symbol"]));
                }

                break;

            default:
                lost.Add(renderer is null
                    ? "The layer has no renderer that SLD can express; it is drawn with its own style."
                    : $"A `{kind}` renderer has no SLD form; the layer is drawn with its own style.");
                return [];
        }

        if (renderer["defaultSymbol"] is JsonObject fallback)
        {
            XElement rule = Rule((string?)renderer["defaultLabel"] ?? "Other", null, fallback);
            rule.Elements().FirstOrDefault(e => e.Name.LocalName == "Description")?.AddAfterSelf(new XElement(se + "ElseFilter"));

            if (rule.Element(se + "ElseFilter") is null)
            {
                rule.AddFirst(new XElement(se + "ElseFilter"));
            }

            rules.Add(rule);
        }

        return rules;
    }

    /// <summary>A JSON number, whatever CLR type the node was built from.</summary>
    private static double? D(JsonNode? node) =>
        node is JsonValue && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    private static string Literal(JsonNode? value) => value switch
    {
        JsonValue when D(value) is { } d => d.ToString("R", CultureInfo.InvariantCulture),
        null => string.Empty,
        _ => value.ToString(),
    };

    private static string Hex(JsonNode? colour) =>
        colour is JsonArray { Count: >= 3 } c
            ? $"#{(int)(D(c[0]) ?? 0):x2}{(int)(D(c[1]) ?? 0):x2}{(int)(D(c[2]) ?? 0):x2}"
            : "#808080";

    private static string Opacity(JsonNode? colour) =>
        colour is JsonArray { Count: >= 4 } c ? Math.Round((D(c[3]) ?? 255) / 255, 3).ToString(CultureInfo.InvariantCulture) : "1";

    private static XElement Parameter(XNamespace se, string name, string value) => new(se + "SvgParameter", new XAttribute("name", name), value);

    private static XElement Stroke(JsonObject line, XNamespace se)
    {
        XElement stroke = new(se + "Stroke",
            Parameter(se, "stroke", Hex(line["color"])),
            Parameter(se, "stroke-opacity", Opacity(line["color"])),
            Parameter(se, "stroke-width", Math.Round((D(line["width"]) ?? 1) / PointsPerPixel, 3).ToString(CultureInfo.InvariantCulture)));

        if ((string?)line["style"] is { } style && style != "esriSLSSolid")
        {
            stroke.Add(Parameter(se, "stroke-dasharray", style == "esriSLSDot" ? "1 3" : "6 3"));
        }

        return stroke;
    }

    private static XElement Symbolizer(JsonObject? symbol, XNamespace se, List<string> lost)
    {
        switch ((string?)symbol?["type"])
        {
            case "esriSFS":
                XElement polygon = new(se + "PolygonSymbolizer",
                    new XElement(se + "Fill", Parameter(se, "fill", Hex(symbol!["color"])), Parameter(se, "fill-opacity", Opacity(symbol["color"]))));

                if (symbol["outline"] is JsonObject outline)
                {
                    polygon.Add(Stroke(outline, se));
                }

                return polygon;

            case "esriSLS":
                return new XElement(se + "LineSymbolizer", Stroke(symbol!, se));

            default:
                if ((string?)symbol?["type"] is { } kind && kind != "esriSMS")
                {
                    lost.Add($"An `{kind}` symbol is written as a grey circle.");
                }

                XElement mark = new(se + "Mark",
                    new XElement(se + "WellKnownName", "circle"),
                    new XElement(se + "Fill", Parameter(se, "fill", Hex(symbol?["color"])), Parameter(se, "fill-opacity", Opacity(symbol?["color"]))));

                if (symbol?["outline"] is JsonObject ring)
                {
                    mark.Add(Stroke(ring, se));
                }

                return new XElement(se + "PointSymbolizer",
                    new XElement(se + "Graphic", mark,
                        new XElement(se + "Size", Math.Round((D(symbol?["size"]) ?? 8) / PointsPerPixel, 3).ToString(CultureInfo.InvariantCulture))));
        }
    }
}
