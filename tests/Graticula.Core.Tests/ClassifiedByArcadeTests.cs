using System;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A classified renderer that names its field the way ArcGIS Pro names it now.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-206](../../docs/architecture-debt.md).</b> `CIMUniqueValueRenderer` and
/// `CIMClassBreaksRenderer` each carry a `valueExpressionInfo` beside their `fields` /
/// `field`, and the specification says of it that it holds *the Arcade expression that returns
/// value*. A renderer authored that way carries no `fields` at all — and this server threw,
/// so a document ArcGIS Pro writes today was rejected as unreadable rather than drawn.
/// </para>
/// <para>
/// <b>The same fault as [D-203](../../docs/architecture-debt.md), one level up.</b> That one was
/// in visual variables and was silent: the variable was dropped and the map drew flat. This one
/// is loud, which is better, and still wrong.
/// </para>
/// </remarks>
public sealed class ClassifiedByArcadeTests
{
    private const string ByLandUse =
        """
        {
          "type": "CIMUniqueValueRenderer",
          "valueExpressionInfo": {
            "type": "CIMExpressionInfo",
            "title": "Kullanim",
            "expression": "$feature.kullanim",
            "returnType": "Default"
          },
          "groups": [{ "classes": [
            { "label": "tarim", "visible": true,
              "values": [{ "type": "CIMUniqueValue", "fieldValues": ["tarim"] }],
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [120, 180, 90, 100] } }] } } },
            { "label": "yerlesim", "visible": true,
              "values": [{ "type": "CIMUniqueValue", "fieldValues": ["yerlesim"] }],
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [200, 80, 60, 100] } }] } } }
          ] }]
        }
        """;

    private const string ByPopulation =
        """
        {
          "type": "CIMClassBreaksRenderer",
          "valueExpressionInfo": {
            "type": "CIMExpressionInfo",
            "expression": "$feature[\"nufus\"]",
            "returnType": "Default"
          },
          "breaks": [
            { "upperBound": 5000, "label": "kasaba", "symbol": { "symbol": {
              "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [255, 255, 0, 100] } }] } } },
            { "upperBound": 20000, "label": "sehir", "symbol": { "symbol": {
              "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [255, 0, 0, 100] } }] } } }
          ]
        }
        """;

    [Fact]
    public void A_unique_value_renderer_reads_its_field_from_the_Arcade_slot()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(ByLandUse)!);

        Assert.Equal(Cim.UniqueValue, projection.Kind);
        Assert.Equal("kullanim", projection.Field);
        Assert.Equal(2, projection.Classes.Count);
    }

    [Fact]
    public void A_class_breaks_renderer_reads_its_field_from_the_Arcade_slot()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(ByPopulation)!);

        Assert.Equal(Cim.ClassBreaks, projection.Kind);
        Assert.Equal("nufus", projection.Field);
    }

    [Fact]
    public void Both_faces_then_carry_the_field_the_expression_named()
    {
        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)CimStyle.ToMapLibre(
                (JsonObject)JsonNode.Parse(ByLandUse)!, "araziler").Style["layers"]!)
                    .Single()!["paint"]!["fill-color"]);

        Assert.Equal("match", (string?)colour[0]);
        Assert.Equal("kullanim", (string?)((JsonArray)colour[1]!)[1]);

        JsonObject renderer = (JsonObject)CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(ByPopulation)!, "iller").DrawingInfo["renderer"]!;

        Assert.Equal("classBreaks", (string?)renderer["type"]);
        Assert.Equal("nufus", (string?)renderer["field"]);
    }

    [Fact]
    public void An_expression_that_computes_is_still_refused_rather_than_read_as_a_column()
    {
        // <b>The fallback must not widen what counts as a field.</b> `Plain` is the whole
        // defence: asking PostGIS for a column called `nufus / alan` fails as a database error a
        // long way from the document that caused it, and this reader does not evaluate Arcade.
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Cim.Project((JsonObject)JsonNode.Parse(
                ByLandUse.Replace(
                    "$feature.kullanim", "$feature.a + $feature.b", StringComparison.Ordinal))!));

        Assert.Contains("valueExpressionInfo", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A class-breaks renderer whose field is named plainly, with a space in it.</summary>
    private const string BySpacedColumn =
        """
        {
          "type": "CIMClassBreaksRenderer",
          "field": "arazi kullanimi",
          "breaks": [
            { "upperBound": 5000, "label": "az", "symbol": { "symbol": {
              "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [255, 255, 0, 100] } }] } } }
          ]
        }
        """;

    [Fact]
    public void A_plain_field_still_wins_and_may_be_a_name_Plain_would_reject()
    {
        // <b>Order matters, and this is why.</b> `field` is read with `Text`, which admits a
        // column name with a space in it; `Field` applies `Plain`, which does not. If the Arcade
        // slot were consulted first -- or if the plain one were routed through `Plain` -- this
        // document would have stopped working the day the fallback was added.
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(BySpacedColumn)!);

        Assert.Equal("arazi kullanimi", projection.Field);
    }
}
