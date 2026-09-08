using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// Every renderer this server reads reports the columns it draws with.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-058](../../docs/adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md)
/// condition 2, and this is the half that condition said it could not cover.</b> The datastore's
/// fields are dropped from a screen, and the safety of that is
/// <c>HostedDataEndpoints.HoldingOn</c> refusing to drop a column something reads.
/// <c>EveryColumnNameIsGuardedTests</c> enumerates the columns a layer stores as a *property* —
/// geometry, identity, time field — and that test's own remarks name what it cannot reach:
/// <i>a column name that never becomes a property, inside the symbology document, which the guard
/// handles by compiling it rather than by reading a property. That half stays a judgement.</i>
/// </para>
/// <para>
/// <b>It does not have to stay a judgement, because the compiling has an authoritative list.</b>
/// The guard asks <see cref="SymbologyPlan.Compile"/> for <see cref="SymbologyPlan.Fields"/> —
/// <i>every attribute column the style reads</i> — and what <c>Cim.Project</c> can produce that
/// list for is exactly the set of renderer kinds <c>Cim</c> declares as constants. So the
/// enumeration is over those constants, read by reflection rather than typed here, which is the
/// same shape the property test uses and for the same reason: a list maintained by remembering is
/// wrong in both directions, and this repository has the receipt — ADR-058 §5c listed a fourth
/// dependency that does not exist in this server.
/// </para>
/// <para>
/// <b>An eighth renderer fails this test by name.</b> Adding a constant to <c>Cim</c> without
/// adding a document here reports <i>Cim declares CIMWhateverRenderer and this test has no
/// document for it</i> — which is what *enforced by something that fails when it is incomplete*
/// means for the half a property list cannot see.
/// </para>
/// <para>
/// <b>What it still does not prove</b> is that a renderer's *own* reading of its document is
/// complete — a kind that reads two fields and reports one would pass, because the marker column
/// is found. That is a narrower gap than *this half is judgement*, and it is the gap
/// <c>CimTests</c> and the per-renderer suites beside it exist to close.
/// </para>
/// </remarks>
public sealed class SymbologyNamesItsColumnsTests
{
    /// <summary>The column every document below draws with.</summary>
    /// <remarks>
    /// <b>Distinctive on purpose.</b> A marker that could occur anywhere else in the document —
    /// <c>value</c>, <c>field</c> — would let a test pass on a substring match against the
    /// renderer's own vocabulary rather than against the column it read.
    /// </remarks>
    private const string Marker = "zzzguarded";

    /// <summary>
    /// Every renderer kind <c>Cim</c> names has a document here, and each reports its column.
    /// </summary>
    [Fact]
    public void Every_renderer_kind_reports_the_column_it_draws_with()
    {
        IReadOnlyList<string> declared = DeclaredKinds();

        Assert.True(
            declared.Count >= 7,
            $"Only {declared.Count} renderer kinds were found on Cim by reflection. This test "
            + "enumerates constants whose value looks like `CIM…Renderer`; if the constants were "
            + "renamed or moved, this reads an empty list and passes forever.");

        List<string> missing = [];
        List<string> silent = [];

        foreach (string kind in declared)
        {
            if (Documents.TryGetValue(kind, out string? document) is false)
            {
                missing.Add(kind);
                continue;
            }

            IReadOnlyList<string> fields =
                Cim.Project((JsonObject)JsonNode.Parse(document)!).AllFields();

            bool named = fields.Any(
                f => string.Equals(f, Marker, StringComparison.OrdinalIgnoreCase));

            // <b>A simple renderer draws by no column, and that is the answer rather than a
            // gap.</b> It is in the list so the list is the whole list; the case below is what
            // keeps it honest, because a simple renderer *with a size variable* does read one.
            if (kind == Cim.Simple)
            {
                Assert.True(
                    fields.Count == 0,
                    $"A plain CIMSimpleRenderer reported {fields.Count} field(s): "
                    + $"{string.Join(", ", fields)}. It draws every feature the same way, so a "
                    + "field here means the compiler is reading something the renderer does not.");

                continue;
            }

            if (!named)
            {
                silent.Add($"{kind} → [{string.Join(", ", fields)}]");
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Cim declares {string.Join(", ", missing)} and this test has no document for it. "
            + "A renderer whose fields nothing enumerates is a renderer whose column "
            + "`HoldingOn` will let somebody drop — ADR-058 condition 2. Add a document that "
            + $"draws by '{Marker}'.");

        Assert.True(
            silent.Count == 0,
            $"These renderers draw by '{Marker}' and did not report it: "
            + $"{string.Join("; ", silent)}. `HoldingOn` refuses a drop by asking "
            + "`SymbologyPlan.Fields`, so a column a renderer reads and does not report is a "
            + "column this server will drop out from under a map that is drawing with it.");
    }

    /// <summary>
    /// A simple renderer that varies by a column reports that column.
    /// </summary>
    /// <remarks>
    /// <b>The case that keeps <c>CIMSimpleRenderer</c>'s exemption above from being a hole.</b>
    /// <c>Cim.Proportional</c>'s own remarks say a proportional renderer <i>is</i> a simple
    /// renderer carrying a size variable, and the JavaScript SDK has no other way to express one
    /// — so *a simple renderer reads no field* is true only until somebody attaches a visual
    /// variable, which is an ordinary thing to do and which the exemption would otherwise wave
    /// through.
    /// </remarks>
    [Fact]
    public void A_simple_renderer_with_a_visual_variable_reports_its_column()
    {
        IReadOnlyList<string> fields =
            Cim.Project((JsonObject)JsonNode.Parse(SimpleWithSize)!).AllFields();

        Assert.True(
            fields.Any(f => string.Equals(f, Marker, StringComparison.OrdinalIgnoreCase)),
            $"A CIMSimpleRenderer sized by '{Marker}' reported [{string.Join(", ", fields)}]. "
            + "The exemption for a plain simple renderer in the test above is only safe while "
            + "this case is not.");
    }

    /// <summary>
    /// A renderer whose classes all draw the same still names the column it classifies by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case that found the gap, and it found it by accident.</b> The documents
    /// above were first written with one shared fill for every class, because the colours are not
    /// what this file is about — and two renderers then reported **no fields at all**. The cause
    /// is not a reading defect: a <c>match</c> whose outcomes are all the same value collapses to
    /// that value, so the derived MapLibre paint carries no <c>get</c>, so
    /// <c>SymbologyPlan.Fields</c> — which is collected from the compiled paint — is empty and
    /// correct.
    /// </para>
    /// <para>
    /// <b>Correct for drawing and wrong for the guard, which is why the guard changed.</b>
    /// <c>HoldingOn</c> was asking the plan, so it would have allowed the column to be dropped.
    /// Nothing on the map would change — and the stored document would go on naming a column
    /// that no longer exists, so the symbology editor and both derived faces fail on the next
    /// read. It now asks <c>CimProjection.AllFields</c>, which reads the document.
    /// </para>
    /// <para>
    /// <b>Both halves are asserted, because either alone is a coincidence.</b> That the plan
    /// reports nothing is the measurement this rests on; that the projection reports the column
    /// is the repair. If the collapse ever stops happening, the first assertion fails and says
    /// so, rather than leaving a test that passes for a reason nobody can see any more.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_renderer_whose_classes_all_draw_the_same_still_names_its_column()
    {
        JsonObject body = (JsonObject)JsonNode.Parse(OneColourForEveryClass)!;

        Assert.True(
            SymbologyPlan.Compile(OneColourForEveryClass).Fields.Count == 0,
            "A unique-value renderer whose classes all carry one colour used to compile to a "
            + "constant, so the plan read no fields from it. If that has changed, the reasoning "
            + "behind `CimProjection.AllFields` has changed with it and should be re-read rather "
            + "than assumed — the guard is still right to ask the document, but this test's "
            + "account of why is now wrong.");

        Assert.Contains(
            Marker,
            Cim.Project(body).AllFields(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A unique-value renderer classifying by a column and drawing every class alike.</summary>
    private const string OneColourForEveryClass = $$"""
    {
      "type": "CIMUniqueValueRenderer",
      "fields": ["{{Marker}}"],
      "useDefaultSymbol": true,
      "defaultLabel": "Other",
      "defaultSymbol": {{Fill}},
      "groups": [{ "classes": [
        { "label": "One", "visible": true,
          "values": [{ "type": "CIMUniqueValue", "fieldValues": ["a"] }],
          "symbol": {{Fill}} },
        { "label": "Two", "visible": true,
          "values": [{ "type": "CIMUniqueValue", "fieldValues": ["b"] }],
          "symbol": {{Fill}} }
      ] }]
    }
    """;

    /// <summary>
    /// Every renderer kind <c>Cim</c> declares, read off the type rather than typed here.
    /// </summary>
    /// <returns>The kind strings.</returns>
    private static IReadOnlyList<string> DeclaredKinds() =>
        [.. typeof(Cim)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => f.GetRawConstantValue() as string)
            .Where(v => v is { Length: > 0 }
                && v.StartsWith("CIM", StringComparison.Ordinal)
                && v.EndsWith("Renderer", StringComparison.Ordinal))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>A polygon symbol, because every document below needs one and none is about it.</summary>
    private const string Fill =
        """
        { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
          { "type": "CIMSolidFill",
            "color": { "type": "CIMRGBColor", "values": [200, 200, 200, 100] } }] } }
        """;

    /// <summary>One document per renderer kind, each drawing by <see cref="Marker"/>.</summary>
    /// <remarks>
    /// <b>The shapes are the ones the per-renderer suites already use</b>, with the field renamed.
    /// Inventing minimal documents here would test this file's guess at the format rather than
    /// the format, and a renderer that failed to compile would look like a missing field.
    /// </remarks>
    private static readonly Dictionary<string, string> Documents = new(StringComparer.Ordinal)
    {
        [Cim.Simple] = $$"""
        { "type": "CIMSimpleRenderer", "label": "all", "symbol": {{Fill}} }
        """,

        // <b>Two fields, and the marker is the second on purpose.</b> A single-field
        // classification is carried by `CimProjection.Field` as well as by `Fields`, so a
        // one-field document here passed even with the whole `Fields` list removed from
        // `AllFields` — measured. The second field is the one only the list can reach.
        [Cim.UniqueValue] = $$"""
        {
          "type": "CIMUniqueValueRenderer",
          "fields": ["other", "{{Marker}}"],
          "fieldDelimiter": " / ",
          "useDefaultSymbol": true,
          "defaultLabel": "Other",
          "defaultSymbol": {{Fill}},
          "groups": [{ "classes": [
            { "label": "One", "visible": true,
              "values": [{ "type": "CIMUniqueValue", "fieldValues": ["x", "a"] }],
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [230, 120, 60, 100] } }] } } },
            { "label": "Two", "visible": true,
              "values": [{ "type": "CIMUniqueValue", "fieldValues": ["x", "b"] }],
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [60, 120, 230, 100] } }] } } }
          ] }]
        }
        """,

        [Cim.ClassBreaks] = $$"""
        {
          "type": "CIMClassBreaksRenderer",
          "field": "{{Marker}}",
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
        """,

        [Cim.Proportional] = $$"""
        {
          "type": "CIMProportionalRenderer",
          "heading": "size",
          "field": "{{Marker}}",
          "minDataValue": 100,
          "maxDataValue": 10000,
          "minSymbol": { "symbol": { "type": "CIMPointSymbol", "symbolLayers": [
            { "type": "CIMVectorMarker", "size": 6,
              "markerGraphics": [ { "type": "CIMMarkerGraphic", "symbol": {
                "type": "CIMPolygonSymbol", "symbolLayers": [
                  { "type": "CIMSolidFill",
                    "color": { "type": "CIMRGBColor", "values": [0, 122, 194, 100] } }] } } ] }] } }
        }
        """,

        [Cim.HeatMap] = $$"""
        {
          "type": "CIMHeatMapRenderer",
          "field": "{{Marker}}",
          "radius": 12,
          "heading": "density",
          "maxPixelIntensity": 40,
          "colorScheme": {
            "type": "CIMLinearContinuousColorRamp",
            "fromColor": { "type": "CIMRGBColor", "values": [0, 0, 255, 100] },
            "toColor":   { "type": "CIMRGBColor", "values": [255, 0, 0, 100] }
          }
        }
        """,

        [Cim.DotDensity] = $$"""
        {
          "type": "CIMDotDensityRenderer",
          "fieldNames": ["{{Marker}}", "other"],
          "dotValue": 100,
          "dotSize": 3,
          "randomSeed": 7,
          "symbolLabel": "people",
          "dotDensitySymbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [220, 50, 40, 100] } },
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [40, 90, 200, 100] } }] } }
        }
        """,

        [Cim.Chart] = $$"""
        {
          "type": "CIMChartRenderer",
          "fieldNames": ["{{Marker}}", "other"],
          "label": "languages",
          "preventChartOverlap": true,
          "colorRamp": {
            "type": "CIMFixedColorRamp",
            "colors": [
              { "type": "CIMRGBColor", "values": [220, 50, 40, 100] },
              { "type": "CIMRGBColor", "values": [40, 90, 200, 100] }
            ]
          },
          "baseSymbol": {{Fill}}
        }
        """,
    };

    /// <summary>A simple renderer whose size varies by <see cref="Marker"/>.</summary>
    private const string SimpleWithSize = $$"""
    {
      "type": "CIMSimpleRenderer",
      "label": "sized",
      "symbol": { "symbol": { "type": "CIMPointSymbol", "symbolLayers": [
        { "type": "CIMVectorMarker", "size": 6,
          "markerGraphics": [ { "type": "CIMMarkerGraphic", "symbol": {
            "type": "CIMPolygonSymbol", "symbolLayers": [
              { "type": "CIMSolidFill",
                "color": { "type": "CIMRGBColor", "values": [0, 122, 194, 100] } }] } } ] }] } },
      "visualVariables": [
        { "type": "CIMSizeVisualVariable",
          "field": "{{Marker}}",
          "minSize": 4, "maxSize": 24,
          "minValue": 1, "maxValue": 100 }
      ]
    }
    """;
}
