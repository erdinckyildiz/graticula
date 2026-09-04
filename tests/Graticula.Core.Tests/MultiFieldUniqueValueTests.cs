using System;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A classification by more than one field, which ArcGIS has always allowed.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 §3.17, found by the owner reading the documentation.</b>
/// `UniqueValueRenderer` carries `field1`, `field2`, `field3` and a `fieldDelimiter`, and a class
/// matches a *tuple*: land use within district, species within plot. This server read the first
/// field and reported the rest as lost — so a two-field renderer drew with the right shape and
/// the wrong classes, which is the failure this project keeps meeting: a map with nothing
/// visibly wrong with it that is not the map the document asked for.
/// </para>
/// <para>
/// <b>The joined key is the same string on both faces</b>, which is what makes one reading
/// enough: the tile face matches on a `concat` of the fields and Esri's `value` is the
/// delimiter-joined string, so the projection joins once and both faces use it.
/// </para>
/// </remarks>
public sealed class MultiFieldUniqueValueTests
{
    private const string ByUseAndDistrict =
        """
        {
          "type": "CIMUniqueValueRenderer",
          "fields": ["kullanim", "ilce"],
          "fieldDelimiter": " / ",
          "groups": [{ "classes": [
            { "label": "tarim / Merkez", "visible": true,
              "values": [{ "type": "CIMUniqueValue", "fieldValues": ["tarim", "Merkez"] }],
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [120, 180, 90, 100] } }] } } },
            { "label": "tarim / Kuzey", "visible": true,
              "values": [{ "type": "CIMUniqueValue", "fieldValues": ["tarim", "Kuzey"] }],
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [200, 80, 60, 100] } }] } } }
          ] }]
        }
        """;

    [Fact]
    public void The_projection_keeps_every_field_and_joins_each_class_into_one_key()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(ByUseAndDistrict)!);

        Assert.Equal(["kullanim", "ilce"], projection.Fields);
        Assert.Equal(" / ", projection.Delimiter);
        Assert.Empty(projection.NotDrawn);

        // <b>The pair, joined.</b> Reading only the first would make both classes "tarim" and
        // the second would silently win — two districts drawn as one.
        Assert.Equal("tarim / Merkez", projection.Classes[0].Values.Single());
        Assert.Equal("tarim / Kuzey", projection.Classes[1].Values.Single());
    }

    [Fact]
    public void The_tile_face_matches_on_a_concat_of_the_fields()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(ByUseAndDistrict)!, "parseller");

        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)derived.Style["layers"]!).Single()!["paint"]!["fill-color"]);

        Assert.Equal("match", (string?)colour[0]);

        JsonArray input = (JsonArray)colour[1]!;

        // ["concat", ["to-string", ["get", a]], " / ", ["to-string", ["get", b]]]
        Assert.Equal("concat", (string?)input[0]);
        Assert.Equal("kullanim", (string?)((JsonArray)((JsonArray)input[1]!)[1]!)[1]);
        Assert.Equal(" / ", (string?)input[2]);
        Assert.Equal("ilce", (string?)((JsonArray)((JsonArray)input[3]!)[1]!)[1]);

        // And the class keys the match compares against are the joined ones.
        Assert.Equal("tarim / Merkez", (string?)colour[2]);
    }

    [Fact]
    public void The_renderer_evaluates_the_concat_and_picks_the_right_class()
    {
        // <b>The expression has to be executable, not merely well formed.</b> A style carrying a
        // `concat` that this renderer could not evaluate would draw every feature in the
        // fallback, which on a two-class map is one flat colour and looks like a styling
        // mistake rather than a missing operator.
        SymbologyPlan plan = SymbologyPlan.Compile(ByUseAndDistrict);

        Assert.Single(plan.Layers);
        Assert.Contains("kullanim", plan.Fields);
        Assert.Contains("ilce", plan.Fields);

        Assert.Equal(new Rgba(120, 180, 90, 255), Painted(plan, "tarim", "Merkez"));
        Assert.Equal(new Rgba(200, 80, 60, 255), Painted(plan, "tarim", "Kuzey"));
    }

    [Fact]
    public void The_ArcGIS_face_publishes_all_three_field_slots_and_the_delimiter()
    {
        DerivedDrawingInfo drawing = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(ByUseAndDistrict)!, "parseller");

        JsonObject renderer = (JsonObject)drawing.DrawingInfo["renderer"]!;

        Assert.Equal("kullanim", (string?)renderer["field1"]);
        Assert.Equal("ilce", (string?)renderer["field2"]);
        Assert.Null(renderer["field3"]);
        Assert.Equal(" / ", (string?)renderer["fieldDelimiter"]);

        Assert.Equal(
            "tarim / Merkez",
            (string?)((JsonArray)renderer["uniqueValueInfos"]!)[0]!["value"]);
    }

    [Fact]
    public void A_two_field_drawingInfo_comes_in_whole_rather_than_losing_its_second_field()
    {
        // <b>The direction the console uses.</b> `generateRenderer` answers in Esri's vocabulary
        // and the editor holds CIM, so this conversion is on the path of every classification
        // the console makes — and it reported the second field as lost until 2026-09-04.
        CimWrite stored = CimEsri.FromDrawingInfo(
            (JsonObject)JsonNode.Parse(
                """
                {
                  "renderer": {
                    "type": "uniqueValue",
                    "field1": "kullanim",
                    "field2": "ilce",
                    "fieldDelimiter": " / ",
                    "uniqueValueInfos": [
                      { "value": "tarim / Merkez", "label": "tarim / Merkez",
                        "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                                    "color": [120, 180, 90, 255] } }
                    ]
                  }
                }
                """)!,
            GeometryKind.Polygon);

        Assert.Empty(stored.Losses);

        JsonArray fields = (JsonArray)stored.Renderer["fields"]!;

        Assert.Equal(2, fields.Count);
        Assert.Equal("kullanim", (string?)fields[0]);
        Assert.Equal("ilce", (string?)fields[1]);

        JsonArray tuple = (JsonArray)stored.Renderer["groups"]![0]!["classes"]![0]!
            ["values"]![0]!["fieldValues"]!;

        Assert.Equal("tarim", (string?)tuple[0]);
        Assert.Equal("Merkez", (string?)tuple[1]);
    }

    [Fact]
    public void A_delimiter_inside_a_value_does_not_split_a_class_in_two()
    {
        // <b>The last field keeps whatever is left.</b> A value containing the delimiter is not
        // exotic — " / " appears in Turkish place names — and splitting greedily would turn one
        // class into three and match none of them.
        CimWrite stored = CimEsri.FromDrawingInfo(
            (JsonObject)JsonNode.Parse(
                """
                {
                  "renderer": {
                    "type": "uniqueValue",
                    "field1": "ilce",
                    "field2": "mahalle",
                    "fieldDelimiter": " / ",
                    "uniqueValueInfos": [
                      { "value": "Merkez / Ada / Bahce", "label": "x",
                        "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                                    "color": [1, 2, 3, 255] } }
                    ]
                  }
                }
                """)!,
            GeometryKind.Polygon);

        JsonArray tuple = (JsonArray)stored.Renderer["groups"]![0]!["classes"]![0]!
            ["values"]![0]!["fieldValues"]!;

        Assert.Equal(2, tuple.Count);
        Assert.Equal("Merkez", (string?)tuple[0]);
        Assert.Equal("Ada / Bahce", (string?)tuple[1]);
    }

    [Fact]
    public void More_than_three_fields_is_reported_rather_than_silently_taking_the_first()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(
            ByUseAndDistrict.Replace(
                "\"fields\": [\"kullanim\", \"ilce\"],",
                "\"fields\": [\"kullanim\", \"ilce\", \"tur\", \"yil\"],",
                StringComparison.Ordinal))!);

        Assert.Equal(3, projection.Fields.Count);
        Assert.Contains(
            projection.NotDrawn,
            l => l.Contains("ArcGIS allows three", StringComparison.Ordinal));
    }

    /// <summary>What one feature's two values are actually painted, through the renderer.</summary>
    /// <remarks>
    /// <b>Through `MapRenderer` rather than by resolving the layer directly.</b> What has to be
    /// true is that the whole path evaluates the `concat` — the plan, the expression and the
    /// feature's own attributes — and resolving a layer against a hand-built context would skip
    /// the part most likely to be wrong.
    /// </remarks>
    private static Rgba Painted(SymbologyPlan plan, string use, string district)
    {
        Feature feature = new(
            "1",
            new Graticula.Geometries.Polygon(new LinearRing(XySequence.Wrap(
                [0, 0, 10, 0, 10, 10, 0, 10, 0, 0]))),
            new FeatureSchema(["kullanim", "ilce"]),
            [use, district]);

        Fills canvas = new(50, 50);

        new MapRenderer(
            canvas, new PixelTransform(new Envelope(0, 0, 10, 10), 50, 50), geographic: false)
            .Draw(plan, [feature]);

        return Assert.Single(canvas.Colours);
    }

    /// <summary>A canvas that remembers the colours it was asked to fill with.</summary>
    private sealed class Fills(int width, int height) : IMapCanvas
    {
        public System.Collections.Generic.List<Rgba> Colours { get; } = [];

        public int Width => width;

        public int Height => height;

        public void Clear(Rgba colour)
        {
        }

        public void FillArea(PixelPath path, MapSymbol.Area symbol) => Colours.Add(symbol.Colour);

        public void StrokeLine(PixelPath path, MapSymbol.Stroke symbol)
        {
        }

        public void DrawMarker(double x, double y, MapSymbol.Marker symbol)
        {
        }

        public PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y) =>
            new(x, y, x, y);

        public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y)
        {
        }

        public void DrawImage(
            ReadOnlySpan<Rgba> pixels, int imageWidth, int imageHeight, PixelBox destination)
        {
        }

        public byte[] Encode(MapImageFormat format, int quality) => [];

        public void Dispose()
        {
        }
    }
}
