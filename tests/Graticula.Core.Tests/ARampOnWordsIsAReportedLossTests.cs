using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A renderer that interpolates a column of words is stored, and the author is told.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-218](../../docs/architecture-debt.md)'s open half.</b> The console's *Vary with a number*
/// form offered every column of the layer and accepted whichever was chosen. At draw time
/// <c>Interpolate.Evaluate</c> reads the value with <c>AsNumber(text) ?? 0</c>, so every feature
/// scores nought and the whole layer takes the ramp's low end. Measured on a real layer:
/// <b>1,752 pixels of a single colour</b> from a text column against <b>45 distinct colours</b>
/// from a numeric one — both stored with <c>losses: []</c>.
/// </para>
/// <para>
/// <b>The form was fixed and the other door was not.</b> A document authored in ArcGIS Pro is
/// PUT straight at <c>/admin/layers/{name}/symbology</c> and never passes through the console at
/// all, which is the half this closes — and it is the half that matters for the migration story,
/// because a Pro user is exactly who arrives with a renderer already built.
/// </para>
/// <para>
/// <b>A loss and not a refusal.</b> This server refuses a style it cannot draw; it does not
/// refuse one it can draw badly, because a stored document belongs to its author and the column
/// may be numeric text somebody means to cast later. What the author must not get is
/// <c>losses: []</c> about a map that will be one flat colour.
/// </para>
/// </remarks>
public sealed class ARampOnWordsIsAReportedLossTests
{
    /// <summary>The layer, as the schema reports it.</summary>
    private static readonly FieldDescription[] Columns =
    [
        new("nufus", FieldType.Integer, true, null),
        new("il", FieldType.Text, true, 64),
        new("kurulus", FieldType.Date, true, null),
    ];

    /// <summary>
    /// A class-breaks renderer over a text column is reported.
    /// </summary>
    [Fact]
    public void Class_breaks_over_a_text_column_is_a_loss()
    {
        SymbologyWrite written = SymbologyConversion.Read(
            Breaks("il"), GeometryKind.Polygon, Columns);

        Assert.Contains(
            written.Losses,
            l => l.Contains("`il`", StringComparison.Ordinal)
                && l.Contains("text", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same renderer over the numeric column is reported as nothing.
    /// </summary>
    /// <remarks>
    /// <b>The half that makes the other one mean something.</b> A check that reports every column
    /// would pass the test above and refuse every real map, and this is the assertion that tells
    /// the two apart.
    /// </remarks>
    [Fact]
    public void Class_breaks_over_a_numeric_column_is_not()
    {
        SymbologyWrite written = SymbologyConversion.Read(
            Breaks("nufus"), GeometryKind.Polygon, Columns);

        Assert.DoesNotContain(
            written.Losses,
            l => l.Contains("scores zero", StringComparison.Ordinal));
    }

    /// <summary>
    /// A unique-value renderer over the same text column is not a loss.
    /// </summary>
    /// <remarks>
    /// <b>The distinction the whole check rests on, asserted rather than assumed.</b> A
    /// unique-value renderer <i>matches</i> its column and a column of words is exactly what
    /// people classify by — *land use*, *province*, *species*. Reporting a loss here would refuse
    /// the ordinary case in order to catch the broken one, and it is why
    /// <c>CimProjection.NumericFields</c> exists beside <c>AllFields</c> rather than instead of
    /// it.
    /// </remarks>
    [Fact]
    public void Classifying_by_the_values_of_a_text_column_is_ordinary()
    {
        SymbologyWrite written = SymbologyConversion.Read(
            UniqueValues("il"), GeometryKind.Polygon, Columns);

        Assert.DoesNotContain(
            written.Losses,
            l => l.Contains("scores zero", StringComparison.Ordinal));
    }

    /// <summary>
    /// Without the layer's columns, nothing is claimed.
    /// </summary>
    /// <remarks>
    /// <b>Silence rather than a guess.</b> Only the write path knows the schema; a conversion
    /// with no layer behind it reports what it always did. Saying *this is not a number* about a
    /// column nobody has looked at would be an invented fact, which is the failure this whole
    /// entry is about.
    /// </remarks>
    [Fact]
    public void With_no_columns_to_check_against_nothing_is_reported()
    {
        SymbologyWrite written = SymbologyConversion.Read(Breaks("il"), GeometryKind.Polygon);

        Assert.DoesNotContain(
            written.Losses,
            l => l.Contains("scores zero", StringComparison.Ordinal));
    }

    /// <summary>
    /// A column the layer does not have is not reported as the wrong type.
    /// </summary>
    /// <remarks>
    /// <b>A different fault with a different repair.</b> A renderer naming a column that is not
    /// there is worth reporting and it is not *this* report: telling somebody their column is
    /// text when the truth is that it is absent sends them to change the data rather than the
    /// renderer. [D-231](../../docs/architecture-debt.md) is the shape of what happens when the
    /// field list itself is wrong, and it is why this test exists at all.
    /// </remarks>
    [Fact]
    public void A_column_the_layer_does_not_have_is_not_called_the_wrong_type()
    {
        SymbologyWrite written = SymbologyConversion.Read(
            Breaks("yok_boyle_bir_alan"), GeometryKind.Polygon, Columns);

        Assert.DoesNotContain(
            written.Losses,
            l => l.Contains("scores zero", StringComparison.Ordinal));
    }

    /// <summary>
    /// A visual variable over a text column is reported too, not only a class-breaks field.
    /// </summary>
    /// <remarks>
    /// <b>The console form's own case, and the one the measurement was taken on.</b> D-218's
    /// numbers — 1,752 pixels of one colour against 45 — came from a *colour visual variable*,
    /// which is a simple renderer carrying an interpolation rather than a classification. A check
    /// that read only the classifying field would have missed the case that opened the row.
    /// </remarks>
    [Fact]
    public void A_visual_variable_over_a_text_column_is_a_loss()
    {
        SymbologyWrite written = SymbologyConversion.Read(
            VariedBy("il"), GeometryKind.Polygon, Columns);

        Assert.Contains(
            written.Losses,
            l => l.Contains("`il`", StringComparison.Ordinal));
    }

    /// <summary>A fill, because none of these documents is about the symbol.</summary>
    private const string Fill =
        """
        { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
          { "type": "CIMSolidFill",
            "color": { "type": "CIMRGBColor", "values": [200, 200, 200, 100] } }] } }
        """;

    /// <summary>A class-breaks renderer over one column.</summary>
    /// <param name="column">What it classifies by.</param>
    /// <returns>The document.</returns>
    private static string Breaks(string column) => $$"""
    {
      "type": "CIMClassBreaksRenderer",
      "field": "{{column}}",
      "minimumBreak": 0,
      "useDefaultSymbol": true,
      "defaultSymbol": {{Fill}},
      "breaks": [
        { "upperBound": 5000, "label": "low", "symbol": { "symbol": {
          "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
          "color": { "type": "CIMRGBColor", "values": [255, 255, 0, 100] } }] } } },
        { "upperBound": 20000, "label": "high", "symbol": { "symbol": {
          "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
          "color": { "type": "CIMRGBColor", "values": [255, 0, 0, 100] } }] } } }
      ]
    }
    """;

    /// <summary>A unique-value renderer over one column.</summary>
    /// <param name="column">What it matches on.</param>
    /// <returns>The document.</returns>
    private static string UniqueValues(string column) => $$"""
    {
      "type": "CIMUniqueValueRenderer",
      "fields": ["{{column}}"],
      "useDefaultSymbol": true,
      "defaultLabel": "Other",
      "defaultSymbol": {{Fill}},
      "groups": [{ "classes": [
        { "label": "Ankara", "visible": true,
          "values": [{ "type": "CIMUniqueValue", "fieldValues": ["Ankara"] }],
          "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [230, 120, 60, 100] } }] } } },
        { "label": "Izmir", "visible": true,
          "values": [{ "type": "CIMUniqueValue", "fieldValues": ["Izmir"] }],
          "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [60, 120, 230, 100] } }] } } }
      ] }]
    }
    """;

    /// <summary>A simple renderer whose colour slides with one column.</summary>
    /// <param name="column">What it varies by.</param>
    /// <returns>The document.</returns>
    private static string VariedBy(string column) => $$"""
    {
      "type": "CIMSimpleRenderer",
      "label": "all",
      "symbol": {{Fill}},
      "visualVariables": [
        { "type": "CIMColorVisualVariable",
          "field": "{{column}}",
          "minValue": 0, "maxValue": 100,
          "colorRamp": {
            "type": "CIMLinearContinuousColorRamp",
            "fromColor": { "type": "CIMRGBColor", "values": [255, 255, 200, 100] },
            "toColor":   { "type": "CIMRGBColor", "values": [180, 20, 20, 100] }
          } }
      ]
    }
    """;
}
