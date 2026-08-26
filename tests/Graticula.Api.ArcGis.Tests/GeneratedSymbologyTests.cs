using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// The appearance a layer has before anybody says what it should look like.
/// </summary>
/// <remarks>
/// <b>ADR-033 §5b, and condition 1 is the last test here.</b> Three surfaces used to
/// choose a colour independently — the tile style from two constants, the feature document
/// from nothing at all, and our console in the browser — so one layer had three
/// appearances. The point of a single generator is that they agree by construction, and
/// the only way that stays true is a test that fails when they stop.
/// </remarks>
public sealed class GeneratedSymbologyTests
{
    /// <summary>
    /// The same name gives the same colour, and the value is pinned.
    /// </summary>
    /// <remarks>
    /// <b>Pinned deliberately, which is unusual and is the point.</b> Asserting only
    /// *stable within one run* would pass against <c>string.GetHashCode</c>, which .NET
    /// salts per process — so the palette would move on every restart and every node would
    /// disagree, while a same-run test stayed green. Writing the expected colour down means
    /// changing the algorithm has to be a decision rather than an accident. If this ever
    /// fails on purpose, every existing deployment's maps change colour.
    /// </remarks>
    [Theory]
    [InlineData("tr_ilce")]
    [InlineData("parcels")]
    [InlineData("look_EarlyAlert_routes")]
    public void A_layers_colour_is_stable(string name)
    {
        string once = GeneratedSymbology.ColourOf(name);

        Assert.Equal(once, GeneratedSymbology.ColourOf(name));
        Assert.Contains(once, GeneratedSymbology.Palette);
    }

    /// <summary>
    /// Capitalisation and the server's locale do not change the colour.
    /// </summary>
    /// <remarks>
    /// The Turkish case is the one that bites: a culture-sensitive lower-case maps
    /// <c>I</c> to <c>ı</c>, so a deployment in Turkey would colour <c>PARCELS</c>
    /// differently from one in Ireland. The fingerprint lower-cases invariantly, and this
    /// is what says so.
    /// </remarks>
    [Fact]
    public void Case_does_not_change_the_colour()
    {
        Assert.Equal(GeneratedSymbology.ColourOf("Istanbul"), GeneratedSymbology.ColourOf("istanbul"));
        Assert.Equal(GeneratedSymbology.ColourOf("TR_ILCE"), GeneratedSymbology.ColourOf("tr_ilce"));
    }

    /// <summary>
    /// Different layers mostly get different colours.
    /// </summary>
    /// <remarks>
    /// <b>Not a claim of uniqueness — a claim about spread.</b> Seven hues cannot colour
    /// twenty layers uniquely, and pretending otherwise would be the wrong fix. What this
    /// asserts is that the fingerprint distributes: a hash that returned the same bucket
    /// for everything would satisfy every other test in this file and reproduce the exact
    /// complaint the generator exists to answer.
    /// </remarks>
    [Fact]
    public void The_palette_is_actually_spread_across_layers()
    {
        string[] names =
        [
            "tr_il", "tr_ilce", "tr_kara", "tr_yer", "tr_yol", "parcels", "buildings",
            "roads", "rivers", "wards", "sites", "routes", "reports", "editable",
        ];

        int distinct = names.Select(GeneratedSymbology.ColourOf).Distinct(StringComparer.Ordinal).Count();

        Assert.True(
            distinct >= 5,
            $"Fourteen layers produced {distinct} distinct colours out of "
            + $"{GeneratedSymbology.Palette.Length}. A generator that clusters is the same "
            + "problem as a generator with one colour.");
    }

    [Theory]
    [InlineData(GeometryKind.Point, AppearanceKind.Marker)]
    [InlineData(GeometryKind.MultiPoint, AppearanceKind.Marker)]
    [InlineData(GeometryKind.LineString, AppearanceKind.Line)]
    [InlineData(GeometryKind.MultiLineString, AppearanceKind.Line)]
    [InlineData(GeometryKind.Polygon, AppearanceKind.Fill)]
    [InlineData(GeometryKind.MultiPolygon, AppearanceKind.Fill)]
    public void The_paint_shape_follows_the_geometry(GeometryKind geometry, AppearanceKind expected)
    {
        Assert.Equal(expected, GeneratedSymbology.For("anything", geometry).Kind);
    }

    /// <summary>
    /// A polygon fill is translucent, because polygons overlap.
    /// </summary>
    /// <remarks>
    /// An opaque fill hides whatever is beneath it, including the ground, and a map of two
    /// opaque layers is a map of one. Asserted rather than left to taste because it is the
    /// difference between a usable default and one every operator has to override.
    /// </remarks>
    [Fact]
    public void A_fill_lets_what_is_under_it_through()
    {
        Appearance fill = GeneratedSymbology.For("parcels", GeometryKind.Polygon);

        Assert.InRange(fill.Opacity, 0.2, 0.6);
        Assert.NotNull(fill.Outline);
    }

    [Fact]
    public void An_unnamed_layer_is_refused_rather_than_coloured_black()
    {
        Assert.Throws<ArgumentException>(() => GeneratedSymbology.ColourOf(" "));
    }

    /// <summary>
    /// The colour in the feature document is the colour in the tile style.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-033 condition 1.</b> Both faces are asked for the same layer and the colours
    /// are compared after being converted into a common form — the tile face speaks hex,
    /// the feature face speaks <c>[r, g, b, a]</c>. Nothing else in either writer would
    /// notice if one of them started deciding for itself, which is exactly how the three
    /// appearances arose in the first place.
    /// </para>
    /// <para>
    /// The geometry kinds are covered together because each face has a separate branch per
    /// kind, and a branch is where a constant gets left behind.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(GeometryKind.Point)]
    [InlineData(GeometryKind.LineString)]
    [InlineData(GeometryKind.Polygon)]
    public void Both_faces_draw_the_layer_in_the_same_colour(GeometryKind geometry)
    {
        const string Name = "tr_ilce";

        (byte red, byte green, byte blue) =
            GeneratedSymbology.Bytes(GeneratedSymbology.ColourOf(Name));

        // The feature face: dig the symbol colour out of the document a client reads.
        JsonElement drawing = JsonSerializer.SerializeToElement(
            FeatureServerMetadataWriter.DrawingInfo(Name, geometry));

        JsonElement symbolColour = drawing
            .GetProperty("renderer").GetProperty("symbol").GetProperty("color");

        Assert.Equal(red, symbolColour[0].GetByte());
        Assert.Equal(green, symbolColour[1].GetByte());
        Assert.Equal(blue, symbolColour[2].GetByte());

        // The tile face: the same colour, spelled as hex, in whichever paint property this
        // geometry uses.
        JsonElement style = JsonSerializer.SerializeToElement(
            VectorTileServerMetadataWriter.Style([(Name, geometry, null)]));

        JsonElement paint = style.GetProperty("layers")[0].GetProperty("paint");

        string painted = paint.EnumerateObject()
            .Single(p => p.Name.EndsWith("-color", StringComparison.Ordinal)
                         && !p.Name.Contains("stroke", StringComparison.Ordinal)
                         && !p.Name.Contains("outline", StringComparison.Ordinal))
            .Value.GetString()!;

        Assert.Equal(GeneratedSymbology.ColourOf(Name), painted);
    }

    /// <summary>
    /// The document says the appearance is generated.
    /// </summary>
    /// <remarks>
    /// <b>The distinction ADR-033 §5b takes from the reference.</b> A default presented as
    /// a decision is a decision nobody made, and an operator has no way to tell whether
    /// somebody chose this blue on purpose. The flag is what lets a console say *nobody has
    /// styled this yet* — and what lets the generator change later without overwriting
    /// anybody's intent.
    /// </remarks>
    [Fact]
    public void The_layer_document_admits_the_appearance_is_generated()
    {
        JsonElement document = JsonSerializer.SerializeToElement(
            FeatureServerMetadataWriter.Layer(
                Layer(), GeometryKind.Polygon, Description(), "Query"));

        Assert.True(document.GetProperty("drawingInfoGenerated").GetBoolean());
        Assert.Equal("simple",
            document.GetProperty("drawingInfo").GetProperty("renderer")
                .GetProperty("type").GetString());
    }

    /// <summary>
    /// Opacity is carried once, in the symbol's alpha, and never twice.
    /// </summary>
    /// <remarks>
    /// A client multiplies <c>transparency</c> by the symbol's alpha, so setting both would
    /// take a 45% fill to about 20% — visible as washed-out polygons and indistinguishable
    /// from a rendering bug. The generator decides opacity once; this asserts the writer
    /// does not apply it a second time.
    /// </remarks>
    [Fact]
    public void Opacity_is_in_the_alpha_channel_and_not_also_in_transparency()
    {
        Appearance fill = GeneratedSymbology.For("parcels", GeometryKind.Polygon);

        JsonElement drawing = JsonSerializer.SerializeToElement(
            FeatureServerMetadataWriter.DrawingInfo("parcels", GeometryKind.Polygon));

        int alpha = drawing.GetProperty("renderer").GetProperty("symbol")
            .GetProperty("color")[3].GetInt32();

        Assert.Equal((int)Math.Round(fill.Opacity * 255), alpha);
        Assert.Equal(0, drawing.GetProperty("transparency").GetInt32());
    }

    private static LayerDefinition Layer() =>
        new(
            name: "tr_ilce",
            schemaName: "hosted",
            tableName: "tr_ilce_511f6767",
            geometryColumn: "geom",
            srid: 4326,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: true);

    private static LayerDescription Description() =>
        new(
            [new FieldDescription("objectid", FieldType.Integer, false, null)],
            new Envelope(24.7, 34.9, 45.6, 42.8));
}
