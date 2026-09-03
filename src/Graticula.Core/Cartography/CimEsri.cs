using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Converts between a stored CIM renderer and Esri's REST <c>drawingInfo</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both directions, and they are not symmetric.</b> `drawingInfo` is one symbol deep — a fill
/// with at most one outline, a stroke, or a marker with at most one outline — and CIM is a stack.
/// Coming in, a `drawingInfo` becomes the CIM that says the same thing and loses nothing. Going
/// out, a stack has to be flattened, and what did not fit is reported rather than dropped
/// quietly. That asymmetry is the whole reason
/// [ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) moved the
/// canonical document: under ADR-033 the flattening happened at the moment of storage and could
/// not be undone.
/// </para>
/// <para>
/// <b>Alpha is rescaled in both directions.</b> `drawingInfo` writes 0–255 and CIM writes 0–100.
/// ADR-052 §3.3 and its condition 1.
/// </para>
/// </remarks>
public static class CimEsri
{
    /// <summary>Points per pixel, which is what CIM and a style disagree by.</summary>
    private const double PointsPerPixel = 0.75;

    /// <summary>
    /// Reads an Esri <c>drawingInfo</c> into the CIM renderer that says the same thing.
    /// </summary>
    /// <param name="drawingInfo">The document, with its <c>renderer</c>.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <returns>The renderer, and what could not be carried.</returns>
    public static CimWrite FromDrawingInfo(JsonObject drawingInfo, GeometryKind geometry)
    {
        ArgumentNullException.ThrowIfNull(drawingInfo);

        if (drawingInfo["renderer"] is not JsonObject renderer)
        {
            throw new SymbologyException(
                "A `drawingInfo` has a `renderer`, and this document has none.");
        }

        List<string> losses = [];
        string kind = Text(renderer["type"]) ?? string.Empty;

        JsonObject built = kind switch
        {
            "simple" => new JsonObject
            {
                ["type"] = Cim.Simple,
                ["label"] = Text(renderer["label"]) ?? string.Empty,
                ["description"] = Text(renderer["description"]) ?? string.Empty,
                ["symbol"] = Reference(renderer["symbol"], geometry, "the renderer", losses),
            },

            "uniqueValue" => Unique(renderer, geometry, losses),
            "classBreaks" => Breaks(renderer, geometry, losses),

            _ => throw new SymbologyException(
                $"'{kind}' is not a renderer this server converts. ADR-033 §5e accepts `simple`, "
                + "`uniqueValue` and `classBreaks`, and nothing beyond them: a renderer stored "
                + "but not drawn is a layer that looks styled and is not."),
        };

        // <b>Layer transparency folded into the colours, once.</b> An Esri `drawingInfo`
        // expresses opacity twice — in each symbol's alpha and again in the layer's
        // `transparency` — and a reader that carried both would multiply them, which is the
        // 45%-becomes-20% fault. CIM has no layer-level opacity, so folding it in is the only
        // way to keep it at all, and the derived face writes `transparency: 0` so the two can
        // never be applied together.
        if (Continuous(renderer, losses) is { Count: > 0 } variables)
        {
            built["visualVariables"] = variables;
        }

        if (Number(drawingInfo["transparency"]) is { } transparency and > 0)
        {
            Fold(built, Math.Clamp(100 - transparency, 0, 100) / 100.0);
        }

        return new CimWrite(built, losses);
    }

    /// <summary>Scales every colour's alpha in a renderer, in place.</summary>
    /// <remarks>
    /// <b>A walk rather than a parameter threaded through every writer.</b> The colours are
    /// written in six places and a factor passed to five of them is a factor somebody forgets in
    /// the sixth, where it fails as one symbol that is opaque when the rest are not.
    /// </remarks>
    /// <param name="node">Anywhere in the renderer.</param>
    /// <param name="factor">What to multiply alpha by, 0 to 1.</param>
    private static void Fold(JsonNode? node, double factor)
    {
        switch (node)
        {
            case JsonObject body:
                if (Text(body["type"]) == "CIMRGBColor"
                    && body["values"] is JsonArray values
                    && values.Count > 3)
                {
                    values[3] = Num(Math.Clamp((Number(values[3]) ?? 100) * factor, 0, 100));

                    return;
                }

                foreach (KeyValuePair<string, JsonNode?> each in body)
                {
                    Fold(each.Value, factor);
                }

                break;

            case JsonArray array:
                foreach (JsonNode? each in array)
                {
                    Fold(each, factor);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>The renderer's visual variables, in Esri's REST vocabulary.</summary>
    /// <param name="projection">What the stored renderer says.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The `visualVariables` array.</returns>
    private static JsonArray Continuous(CimProjection projection, List<string> losses)
    {
        JsonArray variables = [];

        foreach (CimVary variable in projection.Vary)
        {
            JsonArray stops = [];

            for (int i = 0; i < variable.Stops.Count; i++)
            {
                JsonObject stop = new() { ["value"] = Num(variable.Stops[i]) };

                switch (variable.What)
                {
                    case CimVaries.Colour when i < variable.Colours.Count:
                        stop["color"] = Esri(variable.Colours[i]);
                        break;

                    case CimVaries.Size when i < variable.Numbers.Count:
                        stop["size"] = Num(variable.Numbers[i]);
                        break;

                    case CimVaries.Opacity when i < variable.Numbers.Count:
                        // Esri says transparency where this server says opacity.
                        stop["transparency"] = Num(Math.Clamp(
                            Math.Round(100 - (variable.Numbers[i] * 100), 2), 0, 100));
                        break;

                    default:
                        continue;
                }

                stops.Add(stop);
            }

            if (stops.Count < 2)
            {
                losses.Add(
                    $"A visual variable over `{variable.Field}` has fewer than two usable stops, "
                    + "so this face does not publish it.");

                continue;
            }

            variables.Add(new JsonObject
            {
                ["type"] = variable.What switch
                {
                    CimVaries.Colour => "colorInfo",
                    CimVaries.Size => "sizeInfo",
                    _ => "transparencyInfo",
                },
                ["field"] = variable.Field,
                ["stops"] = stops,
            });
        }

        return variables;
    }

    /// <summary>
    /// Reads Esri's `visualVariables` into the CIM ones.
    /// </summary>
    /// <remarks>
    /// <b>`rotationInfo` is named rather than read.</b> This renderer does not rotate a symbol,
    /// so accepting one would store a rotation that never happens and give somebody a map they
    /// cannot explain.
    /// </remarks>
    /// <param name="renderer">The Esri renderer.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The CIM `visualVariables`, possibly empty.</returns>
    private static JsonArray Continuous(JsonObject renderer, List<string> losses)
    {
        JsonArray variables = [];

        foreach (JsonObject variable in Objects(renderer["visualVariables"]))
        {
            string kind = Text(variable["type"]) ?? string.Empty;
            string field = Text(variable["field"]) ?? Text(variable["valueExpression"]) ?? string.Empty;

            if (field.Length == 0)
            {
                losses.Add($"A `{kind}` names no field, so nothing varies.");
                continue;
            }

            List<double> values = [];
            List<JsonNode?> outputs = [];

            foreach (JsonObject stop in Objects(variable["stops"]))
            {
                if (Number(stop["value"]) is not { } at)
                {
                    continue;
                }

                JsonNode? output = kind switch
                {
                    "colorInfo" => stop["color"]?.DeepClone(),
                    "sizeInfo" => stop["size"]?.DeepClone(),
                    "transparencyInfo" => stop["transparency"]?.DeepClone(),
                    _ => null,
                };

                if (output is not null)
                {
                    values.Add(at);
                    outputs.Add(output);
                }
            }

            if (values.Count < 2)
            {
                losses.Add(
                    kind is "colorInfo" or "sizeInfo" or "transparencyInfo"
                        ? $"A `{kind}` over `{field}` has fewer than two stops this server can "
                            + "read, so nothing varies."
                        : $"A `{kind}` is not a visual variable this server draws. It varies "
                            + "colour, size and transparency by a field.");

                continue;
            }

            JsonArray data = new([.. values.Select(v => (JsonNode?)Num(v))]);

            variables.Add(kind switch
            {
                "colorInfo" => new JsonObject
                {
                    ["type"] = "CIMColorVisualVariable",
                    ["expression"] = "$feature." + field,
                    ["minValue"] = Num(values[0]),
                    ["maxValue"] = Num(values[^1]),
                    ["colorRamp"] = outputs.Count == 2
                        ? new JsonObject
                        {
                            ["type"] = "CIMLinearContinuousColorRamp",
                            ["fromColor"] = Cim.Colour(Colour(outputs[0])),
                            ["toColor"] = Cim.Colour(Colour(outputs[^1])),
                        }
                        : new JsonObject
                        {
                            ["type"] = "CIMFixedColorRamp",
                            ["colors"] = new JsonArray(
                                [.. outputs.Select(o => (JsonNode?)Cim.Colour(Colour(o)))]),
                        },
                },

                "sizeInfo" => new JsonObject
                {
                    ["type"] = "CIMSizeVisualVariable",
                    ["expression"] = "$feature." + field,
                    ["dataValues"] = data,
                    ["sizeValues"] = new JsonArray(
                        [.. outputs.Select(o => (JsonNode?)Num(Number(o) ?? 0))]),
                    ["minValue"] = Num(values[0]),
                    ["maxValue"] = Num(values[^1]),
                    ["minSize"] = Num(Number(outputs[0]) ?? 0),
                    ["maxSize"] = Num(Number(outputs[^1]) ?? 0),
                },

                _ => new JsonObject
                {
                    ["type"] = "CIMTransparencyVisualVariable",
                    ["field"] = field,
                    ["dataValues"] = data,
                    ["transparencyValues"] = new JsonArray(
                        [.. outputs.Select(o => (JsonNode?)Num(Number(o) ?? 0))]),
                },
            });
        }

        return variables;
    }

    /// <summary>One symbol per value.</summary>
    /// <param name="renderer">The Esri renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The CIM renderer.</returns>
    private static JsonObject Unique(
        JsonObject renderer, GeometryKind geometry, List<string> losses)
    {
        // <b>`field1`, and the other two are named as lost.</b> Esri combines up to three with
        // a delimiter; this server classifies by one and ADR-052 §3.2 says so rather than
        // drawing by the first and letting a map be the notification.
        string? field = Text(renderer["field1"]) ?? Text(renderer["field"]);

        if (string.IsNullOrEmpty(field))
        {
            throw new SymbologyException(
                "A `uniqueValue` renderer names no `field1`, so there is nothing to classify by.");
        }

        foreach (string extra in new[] { "field2", "field3" })
        {
            if (Text(renderer[extra]) is { Length: > 0 } also)
            {
                losses.Add(
                    $"The renderer also classifies by `{also}` ({extra}). This server classifies "
                    + $"by one field and uses `{field}`.");
            }
        }

        JsonArray classes = [];

        foreach (JsonObject info in Objects(renderer["uniqueValueInfos"]))
        {
            string value = Text(info["value"]) ?? string.Empty;

            classes.Add(new JsonObject
            {
                ["label"] = Text(info["label"]) ?? value,
                ["visible"] = true,
                ["values"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "CIMUniqueValue",
                        ["fieldValues"] = new JsonArray(value),
                    }),
                ["symbol"] = Reference(
                    info["symbol"], geometry, $"the class '{value}'", losses),
            });
        }

        if (classes.Count == 0)
        {
            throw new SymbologyException(
                "A `uniqueValue` renderer has no `uniqueValueInfos`, so it draws nothing.");
        }

        JsonObject built = new()
        {
            ["type"] = Cim.UniqueValue,
            ["fields"] = new JsonArray(field),
            ["groups"] = new JsonArray(new JsonObject { ["classes"] = classes }),
        };

        if (renderer["defaultSymbol"] is not null and not JsonValue)
        {
            built["useDefaultSymbol"] = true;
            built["defaultLabel"] = Text(renderer["defaultLabel"]) ?? "Other";
            built["defaultSymbol"] = Reference(
                renderer["defaultSymbol"], geometry, "the default symbol", losses);
        }

        return built;
    }

    /// <summary>One symbol per range.</summary>
    /// <param name="renderer">The Esri renderer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The CIM renderer.</returns>
    private static JsonObject Breaks(
        JsonObject renderer, GeometryKind geometry, List<string> losses)
    {
        if (Text(renderer["field"]) is not { Length: > 0 } field)
        {
            throw new SymbologyException(
                "A `classBreaks` renderer names no `field`, so there is nothing to classify by.");
        }

        JsonArray breaks = [];

        foreach (JsonObject info in Objects(renderer["classBreakInfos"]))
        {
            if (Number(info["classMaxValue"]) is not { } upper)
            {
                throw new SymbologyException(
                    "A class break has no numeric `classMaxValue`, so the range it stands for is "
                    + "not defined.");
            }

            breaks.Add(new JsonObject
            {
                ["upperBound"] = Num(upper),
                ["label"] = Text(info["label"])
                    ?? upper.ToString(CultureInfo.InvariantCulture),
                ["symbol"] = Reference(
                    info["symbol"], geometry, $"the break up to {upper}", losses),
            });
        }

        if (breaks.Count == 0)
        {
            throw new SymbologyException(
                "A `classBreaks` renderer has no `classBreakInfos`, so it draws nothing.");
        }

        JsonObject built = new()
        {
            ["type"] = Cim.ClassBreaks,
            ["field"] = field,
            ["breaks"] = breaks,
        };

        if (Number(renderer["minValue"]) is { } minimum)
        {
            built["minimumBreak"] = Num(minimum);
        }

        return built;
    }

    /// <summary>Wraps a converted symbol the way CIM carries one.</summary>
    /// <param name="node">The Esri symbol.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="where">Where it sits, for a message that can be acted on.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The reference.</returns>
    private static JsonObject Reference(
        JsonNode? node, GeometryKind geometry, string where, List<string> losses) =>
        new()
        {
            ["type"] = "CIMSymbolReference",
            ["symbol"] = Symbol(node, geometry, where, losses),
        };

    /// <summary>
    /// One Esri symbol as a CIM symbol.
    /// </summary>
    /// <remarks>
    /// <b>The outline goes first in `symbolLayers` because CIM draws index zero on top.</b> An
    /// `esriSFS` is a fill with an outline over it, so the CIM that says the same thing lists the
    /// stroke before the fill. Listing them the other way round would bury every outline.
    /// </remarks>
    /// <param name="node">The symbol.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="where">Where it sits.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The CIM symbol.</returns>
    private static JsonObject Symbol(
        JsonNode? node, GeometryKind geometry, string where, List<string> losses)
    {
        if (node is not JsonObject symbol)
        {
            throw new SymbologyException(
                $"There is no symbol at {where}, so the features it stands for would be drawn "
                + "with nothing.");
        }

        string kind = Text(symbol["type"]) ?? string.Empty;
        JsonArray layers = [];

        switch (kind)
        {
            case "esriSFS":
                if (symbol["outline"] is JsonObject fillOutline)
                {
                    layers.Add(Stroke(fillOutline, where, losses));
                }

                layers.Add(new JsonObject
                {
                    ["type"] = "CIMSolidFill",
                    ["enable"] = true,
                    ["color"] = Cim.Colour(Colour(symbol["color"])),
                });

                Style(symbol, "esriSFSSolid", where, losses);

                return new JsonObject
                {
                    ["type"] = "CIMPolygonSymbol",
                    ["symbolLayers"] = layers,
                };

            case "esriSLS":
                layers.Add(Stroke(symbol, where, losses));

                return new JsonObject
                {
                    ["type"] = "CIMLineSymbol",
                    ["symbolLayers"] = layers,
                };

            case "esriSMS":
                if (symbol["outline"] is JsonObject markerOutline)
                {
                    layers.Add(Stroke(markerOutline, where, losses));
                }

                layers.Add(Marker(symbol, where, losses));

                return new JsonObject
                {
                    ["type"] = "CIMPointSymbol",
                    ["symbolLayers"] = layers,
                };

            case "esriPFS":
            case "esriPMS":
                throw new SymbologyException(
                    $"The symbol at {where} is a `{kind}`, which paints with an image. This "
                    + "server has no sprite or image library — ADR-027 condition 5 — so a "
                    + "picture symbol is refused rather than stored and drawn as a flat colour.");

            case "esriTS":
                throw new SymbologyException(
                    $"The symbol at {where} is a text symbol. Labelling is not in v1 (ADR-033 "
                    + "§5g), so a renderer that draws features as text is refused.");

            default:
                throw new SymbologyException(
                    $"'{kind}' at {where} is not a symbol this server reads. It reads `esriSFS`, "
                    + "`esriSLS` and `esriSMS`.");
        }
    }

    /// <summary>An <c>esriSLS</c> as a <c>CIMSolidStroke</c>.</summary>
    /// <param name="stroke">The Esri stroke.</param>
    /// <param name="where">Where it sits.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The CIM layer.</returns>
    private static JsonObject Stroke(JsonObject stroke, string where, List<string> losses)
    {
        JsonObject built = new()
        {
            ["type"] = "CIMSolidStroke",
            ["enable"] = true,
            ["capStyle"] = "Butt",
            ["joinStyle"] = "Miter",

            // <b>Esri's line width is in points and so is CIM's</b>, so this one does not
            // convert. The style derivation is where points become pixels.
            ["width"] = Num(Number(stroke["width"]) ?? 1),
            ["color"] = Cim.Colour(Colour(stroke["color"])),
        };

        // <b>A dash style becomes a dash effect, which is where CIM keeps it.</b> Esri names a
        // handful of patterns; the numbers are this server's reading of those names, and a name
        // it does not know is reported rather than drawn solid in silence.
        if (Text(stroke["style"]) is { Length: > 0 } style && style != "esriSLSSolid")
        {
            double[]? template = style switch
            {
                "esriSLSDash" => [6, 3],
                "esriSLSDot" => [1, 3],
                "esriSLSDashDot" => [6, 3, 1, 3],
                "esriSLSDashDotDot" => [6, 3, 1, 3, 1, 3],
                "esriSLSNull" => [],
                _ => null,
            };

            if (template is null)
            {
                losses.Add(
                    $"The line at {where} has style `{style}`, which this server does not know. "
                    + "It is drawn solid.");
            }
            else if (template.Length == 0)
            {
                losses.Add(
                    $"The line at {where} is `esriSLSNull`, which draws nothing. This server "
                    + "draws every layer it is given, so it is drawn solid; remove the outline "
                    + "instead.");
            }
            else
            {
                built["effects"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "CIMGeometricEffectDashes",
                        ["dashTemplate"] = new JsonArray(
                            [.. template.Select(d => (JsonNode?)JsonValue.Create(d))]),
                        ["lineDashEnding"] = "NoConstraint",
                    });
            }
        }

        return built;
    }

    /// <summary>An <c>esriSMS</c> as a <c>CIMVectorMarker</c>.</summary>
    /// <param name="marker">The Esri marker.</param>
    /// <param name="where">Where it sits.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The CIM layer.</returns>
    private static JsonObject Marker(JsonObject marker, string where, List<string> losses)
    {
        Style(marker, "esriSMSCircle", where, losses);

        Rgba colour = Colour(marker["color"]);

        // <b>A marker graphic, because that is where a `CIMVectorMarker` keeps its colour.</b>
        // The graphic's geometry is a unit circle: this server draws a disc, and writing a
        // circle here says so in the document rather than only in the renderer.
        return new JsonObject
        {
            ["type"] = "CIMVectorMarker",
            ["enable"] = true,
            ["size"] = Num(Number(marker["size"]) ?? 8),
            ["rotation"] = Num(Number(marker["angle"]) ?? 0),
            ["markerGraphics"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "CIMMarkerGraphic",
                    ["geometry"] = new JsonObject
                    {
                        ["x"] = Num(0),
                        ["y"] = Num(0),
                    },
                    ["symbol"] = new JsonObject
                    {
                        ["type"] = "CIMPolygonSymbol",
                        ["symbolLayers"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "CIMSolidFill",
                                ["enable"] = true,
                                ["color"] = Cim.Colour(colour),
                            }),
                    },
                }),
        };
    }

    /// <summary>
    /// Derives an Esri <c>drawingInfo</c> from a stored CIM renderer.
    /// </summary>
    /// <remarks>
    /// <b>This is where a stack becomes one symbol, so this is where the losses are.</b> An
    /// `esriSFS` is a fill with at most one outline; a CIM polygon symbol can be four fills under
    /// three strokes. The topmost of each kind is what this face publishes and everything else is
    /// named, because a road drawn here as a single line when the map draws it as a casing and a
    /// fill is a difference somebody has to be able to find out about without comparing pictures.
    /// </remarks>
    /// <param name="renderer">The stored CIM renderer.</param>
    /// <param name="layerName">What the layer is called, for messages.</param>
    /// <returns>The <c>drawingInfo</c> and what it could not carry.</returns>
    public static DerivedDrawingInfo ToDrawingInfo(JsonObject renderer, string layerName)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        CimProjection projection = Cim.Project(renderer);
        List<string> losses = [.. projection.NotDrawn];

        JsonObject built = projection.Kind switch
        {
            Cim.Simple => new JsonObject
            {
                ["type"] = "simple",
                ["symbol"] = Flatten(projection.Classes[0].Symbol, "this layer", losses),
                ["label"] = projection.Classes[0].Label,
                ["description"] = string.Empty,
            },

            Cim.UniqueValue => UniqueOut(projection, losses),
            Cim.ClassBreaks => BreaksOut(projection, losses),

            _ => throw new SymbologyException(
                $"'{projection.Kind}' has no Esri renderer to derive for '{layerName}'."),
        };

        // <b>`transparency: 0`, said rather than left out.</b> The layer's opacity is already
        // inside every colour's alpha; a face that omitted the property would let a client
        // supply its own default and a face that echoed it would apply it twice.
        // <b>The variables ride on the renderer, exactly as they do in CIM.</b> Esri's REST
        // face spells them `colorInfo`, `sizeInfo` and `transparencyInfo` and hangs them off the
        // renderer, so this is a rename rather than a restructuring.
        if (Continuous(projection, losses) is { Count: > 0 } variables)
        {
            built["visualVariables"] = variables;
        }

        return new DerivedDrawingInfo(
            new JsonObject
            {
                ["renderer"] = built,
                ["transparency"] = Num(0),
            },
            losses);
    }

    /// <summary>A `uniqueValue` renderer from the projection.</summary>
    /// <param name="projection">What the stored renderer says.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The Esri renderer.</returns>
    private static JsonObject UniqueOut(CimProjection projection, List<string> losses)
    {
        JsonArray infos = [];

        foreach (CimClass one in projection.Classes)
        {
            // <b>One `uniqueValueInfo` per value, not per class.</b> Esri's info carries a
            // single `value`; a CIM class that lists three would otherwise publish one of them
            // and drop two without saying so.
            foreach (string value in one.Values.Count == 0 ? [string.Empty] : one.Values)
            {
                infos.Add(new JsonObject
                {
                    ["value"] = value,
                    ["label"] = one.Label,
                    ["description"] = string.Empty,
                    ["symbol"] = Flatten(one.Symbol, $"the class '{one.Label}'", losses),
                });
            }
        }

        return new JsonObject
        {
            ["type"] = "uniqueValue",
            ["field1"] = projection.Field,
            ["field2"] = null,
            ["field3"] = null,
            ["fieldDelimiter"] = ",",
            ["defaultSymbol"] = projection.Default is null
                ? null
                : Flatten(projection.Default, "the default symbol", losses),
            ["defaultLabel"] = projection.Default is null ? null : "Other",
            ["uniqueValueInfos"] = infos,
        };
    }

    /// <summary>A `classBreaks` renderer from the projection.</summary>
    /// <param name="projection">What the stored renderer says.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The Esri renderer.</returns>
    private static JsonObject BreaksOut(CimProjection projection, List<string> losses)
    {
        JsonArray infos = [];
        double? previous = null;

        foreach (CimClass one in projection.Classes)
        {
            infos.Add(new JsonObject
            {
                ["classMinValue"] = previous is { } bottom ? Num(bottom) : null,
                ["classMaxValue"] = one.UpperBound is { } top ? Num(top) : null,
                ["label"] = one.Label,
                ["description"] = string.Empty,
                ["symbol"] = Flatten(one.Symbol, $"the break '{one.Label}'", losses),
            });

            previous = one.UpperBound;
        }

        return new JsonObject
        {
            ["type"] = "classBreaks",
            ["field"] = projection.Field,
            ["minValue"] = Num(0),
            ["classBreakInfos"] = infos,
        };
    }

    /// <summary>
    /// One CIM symbol as the single Esri symbol this face can carry.
    /// </summary>
    /// <param name="symbol">The stack.</param>
    /// <param name="where">Where it sits, for a message that can be acted on.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The Esri symbol.</returns>
    private static JsonObject Flatten(CimSymbol symbol, string where, List<string> losses)
    {
        // <b>Topmost, because that is what a reader sees.</b> The stack is bottom first, so the
        // last of each kind is the one on top.
        CimFill? fill = symbol.Paints.OfType<CimFill>().LastOrDefault();
        CimStroke? stroke = symbol.Paints.OfType<CimStroke>().LastOrDefault();
        CimMarker? marker = symbol.Paints.OfType<CimMarker>().LastOrDefault();

        int carried = (fill is null ? 0 : 1) + (stroke is null ? 0 : 1) + (marker is null ? 0 : 1);

        if (symbol.Paints.Count > carried)
        {
            losses.Add(
                $"The symbol for {where} is built from {symbol.Paints.Count} layers and an Esri "
                + $"symbol carries {carried}. The topmost of each kind is published; the rest are "
                + "drawn by this server and by the tile style, and are kept in the stored "
                + "document.");
        }

        if (marker is not null)
        {
            JsonObject point = new()
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["color"] = Esri(marker.Colour),
                ["size"] = Num(marker.Size),
                ["angle"] = Num(0),
                ["xoffset"] = Num(0),
                ["yoffset"] = Num(0),
            };

            if (stroke is not null)
            {
                point["outline"] = Line(stroke, where, losses);
            }

            return point;
        }

        if (fill is not null)
        {
            JsonObject area = new()
            {
                ["type"] = "esriSFS",
                ["style"] = "esriSFSSolid",
                ["color"] = Esri(fill.Colour),
            };

            if (stroke is not null)
            {
                area["outline"] = Line(stroke, where, losses);
            }

            return area;
        }

        if (stroke is not null)
        {
            return Line(stroke, where, losses);
        }

        throw new SymbologyException(
            $"The symbol at {where} has no layer that becomes an Esri symbol.");
    }

    /// <summary>A stroke as an <c>esriSLS</c>.</summary>
    /// <param name="stroke">The stroke.</param>
    /// <param name="where">Where it sits.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The Esri line.</returns>
    private static JsonObject Line(CimStroke stroke, string where, List<string> losses)
    {
        string style = "esriSLSSolid";

        if (stroke.Dashes is { Length: > 0 } template)
        {
            // <b>Named back where the name exists, and reported where it does not.</b> Esri's
            // line styles are a fixed handful and a dash template is an arbitrary sequence, so
            // most templates have no name and the honest answer is *dashed, but not this
            // dashed*.
            style = template switch
            {
                [6, 3] => "esriSLSDash",
                [1, 3] => "esriSLSDot",
                [6, 3, 1, 3] => "esriSLSDashDot",
                [6, 3, 1, 3, 1, 3] => "esriSLSDashDotDot",
                _ => "esriSLSDash",
            };

            if (style == "esriSLSDash" && template is not [6, 3])
            {
                losses.Add(
                    $"The line at {where} is dashed on a pattern of "
                    + $"[{string.Join(", ", template)}]. Esri's line styles are a fixed set of "
                    + "names, so this face publishes `esriSLSDash` and the exact pattern is "
                    + "drawn by this server and by the tile style only.");
            }
        }

        return new JsonObject
        {
            ["type"] = "esriSLS",
            ["style"] = style,
            ["color"] = Esri(stroke.Colour),
            ["width"] = Num(stroke.Width),
        };
    }

    /// <summary>A colour as Esri writes one: four channels, all 0-255.</summary>
    /// <param name="colour">The colour.</param>
    /// <returns>The array.</returns>
    private static JsonArray Esri(Rgba colour) =>
        new(Num(colour.R), Num(colour.G), Num(colour.B), Num(colour.A));

    /// <summary>Reports a symbol style this server draws as if it were the plain one.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="plain">The style that needs no comment.</param>
    /// <param name="where">Where it sits.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    private static void Style(
        JsonObject symbol, string plain, string where, List<string> losses)
    {
        if (Text(symbol["style"]) is { Length: > 0 } style && style != plain)
        {
            losses.Add(
                $"The symbol at {where} has style `{style}`, and was converted to `{plain}`. A "
                + "hatch or a picture fill needs a sprite image, and this server has no sprite "
                + "library (ADR-027 condition 5), so the shape is drawn as a flat colour.");
        }
    }

    /// <summary>An Esri colour array, whose alpha is 0–255.</summary>
    /// <param name="node">The colour.</param>
    /// <returns>The colour, opaque grey when it cannot be read.</returns>
    private static Rgba Colour(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count < 3)
        {
            return new Rgba(136, 136, 136, 255);
        }

        byte At(int i) =>
            (byte)Math.Clamp(
                Math.Round(Number(array[i]) ?? 0, MidpointRounding.AwayFromZero), 0, 255);

        return new Rgba(At(0), At(1), At(2), array.Count > 3 ? At(3) : (byte)255);
    }

    /// <summary>
    /// A number that reads back as whatever type asks for it.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>JsonValue.Create</c>, and the difference is a whole class of defect.</b> A
    /// <c>JsonValue</c> created from an <c>int</c> is a <c>JsonValue&lt;int&gt;</c> and refuses
    /// <c>GetValue&lt;double&gt;</c>; one created from a <c>double</c> refuses
    /// <c>GetValue&lt;int&gt;</c>. Both serialise identically, so the document on the wire looks
    /// right and every consumer that reads it in memory throws. Going through a
    /// <c>JsonElement</c> gives a value backed by the parser, which converts the way a document
    /// read from text does. Measured twice on 2026-09-03, in the round trip and in the Esri
    /// face.
    /// </remarks>
    /// <param name="value">The number.</param>
    /// <returns>The node.</returns>
    private static JsonValue Num(double value) =>
        JsonValue.Create(JsonSerializer.SerializeToElement(value))!;

    /// <summary>The objects of an array property.</summary>
    /// <param name="node">The property.</param>
    /// <returns>Its objects.</returns>
    private static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>A string property, or null.</summary>
    /// <param name="node">The property.</param>
    /// <returns>Its text.</returns>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>A number property, or null.</summary>
    /// <param name="node">The property.</param>
    /// <returns>Its value.</returns>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        // <b>Asked in several types, because a `JsonValue` remembers the one it was made
        // with.</b> A number that came from `JsonNode.Parse` is backed by a `JsonElement` and
        // answers to `double`; one this server built with `JsonValue.Create(12)` is a
        // `JsonValue<int>` and does not. Measured 2026-09-03: every colour survived a document
        // read from text and every colour came back opaque grey through a document built in
        // memory, which is exactly the round trip a `PUT` performs.
        if (value.TryGetValue(out double asDouble))
        {
            return asDouble;
        }

        if (value.TryGetValue(out int asInt))
        {
            return asInt;
        }

        if (value.TryGetValue(out long asLong))
        {
            return asLong;
        }

        if (value.TryGetValue(out byte asByte))
        {
            return asByte;
        }

        return value.TryGetValue(out decimal asDecimal) ? (double)asDecimal : null;
    }

    /// <summary>Unused for now; kept so the two directions read side by side.</summary>
    /// <param name="points">A CIM measurement.</param>
    /// <returns>The same in pixels.</returns>
    internal static double Pixels(double points) => points / PointsPerPixel;
}

/// <summary>A CIM renderer built from another vocabulary, and what was lost reaching it.</summary>
/// <param name="Renderer">The renderer to store.</param>
/// <param name="Losses">One sentence per thing that did not survive.</param>
public sealed record CimWrite(JsonObject Renderer, IReadOnlyList<string> Losses);
