using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A symbology preview draws the document it was handed, and keeps none of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner request 2026-09-03: *bana gui editör lazım*.</b> The half of an appearance editor
/// that decides anything is the picture, and a picture is only useful if it is of the *candidate*
/// — the document on screen, not the one already stored. A preview that quietly drew the stored
/// appearance would look completely correct: it would show a map, it would show the right layer,
/// and it would never change.
/// </para>
/// <para>
/// <b>Asserted by comparing three answers rather than by decoding one.</b> The same candidate
/// twice must give the same bytes, and a different candidate must give different bytes — which
/// together say that the document is what the picture is a function of. Decoding the PNG and
/// naming the colour would be a second implementation of the renderer's own palette; what is in
/// question here is not *is crimson crimson*, it is *does the request reach the paint*.
/// </para>
/// <para>
/// <b>And it keeps nothing.</b> The endpoint is a `POST` because it carries a whole style
/// document, not because it writes; the layer's stored symbology is read before and after and has
/// to be the same text. A preview that stored what it drew would turn every keystroke in an
/// editor into a published appearance.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class SymbologyPreviewDrawsTheCandidateTests : ArcGisClient
{
    /// <summary>The layer the last <c>AFillLayerAsync</c> chose, with the box it is drawn in.</summary>
    private (string Name, double[] Box, int Sr) Found { get; set; }

    /// <summary>A `drawingInfo` with one solid fill, in whatever colour is asked for.</summary>
    /// <param name="red">Its red channel.</param>
    /// <param name="green">Its green channel.</param>
    /// <param name="blue">Its blue channel.</param>
    /// <returns>The document, as it would be pasted from ArcGIS.</returns>
    private static string Solid(int red, int green, int blue, int width = 1) =>
        System.String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"renderer\":{{\"type\":\"simple\",\"symbol\":"
            + $"{{\"type\":\"esriSFS\",\"style\":\"esriSFSSolid\","
            + $"\"color\":[{red},{green},{blue},255],"
            + $"\"outline\":{{\"type\":\"esriSLS\",\"style\":\"esriSLSSolid\","
            + $"\"color\":[0,0,0,255],\"width\":{width}}}}}}}}}");

    [Fact]
    public async Task The_picture_follows_the_candidate_document_and_nothing_is_stored()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        string before = await StoredSymbologyAsync(root, layer);

        byte[] crimson = await PreviewAsync(root, layer, Solid(220, 20, 60));
        byte[] again = await PreviewAsync(root, layer, Solid(220, 20, 60));
        byte[] sea = await PreviewAsync(root, layer, Solid(46, 139, 87));

        Assert.True(
            crimson.Length > 100,
            $"The preview answered {crimson.Length} bytes, which is not a picture of anything.");

        Assert.True(
            crimson.AsSpan().SequenceEqual(again),
            "The same document drew two different pictures, so something other than the "
            + $"document is deciding what is painted ({crimson.Length} bytes, then "
            + $"{again.Length}).");

        Assert.False(
            crimson.AsSpan().SequenceEqual(sea),
            "Two different documents drew byte-identical pictures, so the candidate is not "
            + "reaching the renderer — which is what a preview showing the stored appearance "
            + "looks like from the outside, and it looks entirely correct.");

        // <b>Read back rather than assumed.</b> `no-store` on the response says nothing about
        // what the handler did to the catalogue.
        Assert.Equal(before, await StoredSymbologyAsync(root, layer));
    }

    /// <summary>
    /// A frame named in another reference is projected, not read as the layer's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written from the defect it describes.</b> The console's symbology map works in Web
    /// Mercator, because that is what the basemap tiles are, and it sent the map's extent as
    /// <c>bbox</c> — which this endpoint read as the layer's own coordinates. Every seeded
    /// fixture is 3857, so the two agreed in every test that has ever run here, and disagreed on
    /// the first real layer: a Turkish extent in degrees, read as metres, is a box nineteen
    /// metres wide near where the equator meets the prime meridian. The owner opened Symbology
    /// and saw open ocean.
    /// </para>
    /// <para>
    /// <b>So the fixture's own reference is asserted rather than assumed.</b> This test builds a
    /// second, honest expression of one box, and it can only do that from a reference it knows
    /// the closed form for. If the seed moves off Web Mercator this FAILS and says so, which is
    /// the right answer — a test that quietly stopped checking is how this defect survived.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_frame_in_another_reference_is_projected_rather_than_read_as_the_layers_own()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        (_, double[] box, int sr) = Found;

        Assert.True(
            sr is 3857 or 102100,
            $"The fixture layer is published in {sr}, and this test needs Web Mercator to write "
            + "the same box a second way. Give it a layer in 3857 or teach it another closed "
            + "form — do not delete the assertion, which is the one this defect got past.");

        string document = Solid(220, 20, 60);

        // <b>The same box, in degrees.</b> The inverse of the spherical Mercator, which is exact
        // and four lines — a projection call here would be testing the projector rather than
        // this endpoint's use of it.
        static double Lon(double x) => x / 6378137.0 * 180.0 / System.Math.PI;

        static double Lat(double y) =>
            (2.0 * System.Math.Atan(System.Math.Exp(y / 6378137.0)) - (System.Math.PI / 2.0))
            * 180.0 / System.Math.PI;

        string degrees = System.String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Lon(box[0])},{Lat(box[1])},{Lon(box[2])},{Lat(box[3])}");

        byte[] projected = await PreviewAsync(
            root, layer, document, $"?bbox={degrees}&bboxSR=4326&size=256x256");

        byte[] readAsOwn = await PreviewAsync(
            root, layer, document, $"?bbox={degrees}&size=256x256");

        // <b>A blank PNG is small and a drawn one is not.</b> Those degree numbers read as metres
        // are a box a few tens of metres across in the Gulf of Guinea, where this layer has
        // nothing — so the picture is transparent, and transparent compresses to almost nothing.
        // The ratio is the assertion rather than either figure, because both depend on the
        // fixture's shape and neither is worth pinning.
        Assert.True(
            projected.Length > readAsOwn.Length * 3,
            $"The frame given in 4326 drew {projected.Length} bytes and the same numbers read as "
            + $"the layer's own drew {readAsOwn.Length}. They are the same size, so 'bboxSR' "
            + "changed nothing and the picture is of wherever those numbers happen to land.");

        Assert.False(
            projected.AsSpan().SequenceEqual(readAsOwn),
            "Two different frames drew byte-identical pictures, so neither is being used.");
    }

    /// <summary>
    /// The picture is drawn in the reference the frame was named in, not in the layer's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Projecting the frame is not the same as drawing in the frame's reference, and the
    /// difference is where every feature lands.</b> The first repair took the four numbers, put
    /// them into the layer's coordinates, and drew that box — so the box's *edges* were right and
    /// its *interior* was linear in the wrong reference. Web Mercator's y is logarithmic in
    /// latitude; a caller that lays the picture over a Mercator viewport therefore sees the layer
    /// slide north or south of the ground under it, most at the top and bottom of the frame and
    /// not at all in the middle.
    /// </para>
    /// <para>
    /// <b>Reported by the owner as *haritalar örtüşmüyor*, on a 4326 layer of Turkish
    /// districts.</b> Measured before this test existed, on a 40°-tall frame at 40°N: the layer
    /// was drawn <b>19.5 pixels of 256</b> from where the frame said it was. Nothing in the suite
    /// could see it, because every previous assertion about `bboxSR` compared byte counts — and a
    /// picture drawn in the wrong reference is exactly as many bytes as one drawn in the right
    /// one.
    /// </para>
    /// <para>
    /// <b>So this one reads the pixels.</b> Two requests for the same ground, one framed in
    /// degrees and one in metres, and the assertion is *which row is the layer on* against the
    /// closed form for each — which differ by twenty rows here and by nothing at all at the
    /// equator, so the test refuses rather than passes if the fixture ever moves there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_frame_in_another_reference_decides_where_in_the_picture_the_layer_lands()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        (_, double[] box, int sr) = Found;

        Assert.True(
            sr is 3857 or 102100,
            $"The fixture layer is published in {sr}, and this test needs Web Mercator to write "
            + "the same ground a second way in closed form.");

        const double Radius = 6378137.0;
        const int Size = 256;

        // <b>Twenty degrees each way, which is what makes the two answers twenty rows apart.</b>
        // Over a small frame Mercator and a linear degree grid agree to within a pixel, so a
        // gentle frame would pass whichever reference the picture was drawn in.
        const double Span = 20.0;

        static double Lon(double x) => x / Radius * 180.0 / System.Math.PI;

        static double Lat(double y) =>
            (2.0 * System.Math.Atan(System.Math.Exp(y / Radius)) - (System.Math.PI / 2.0))
            * 180.0 / System.Math.PI;

        static double MetresX(double lon) => lon * System.Math.PI / 180.0 * Radius;

        static double MetresY(double lat) => Radius * System.Math.Log(
            System.Math.Tan((System.Math.PI / 4.0) + (lat * System.Math.PI / 180.0 / 2.0)));

        double middleLon = Lon((box[0] + box[2]) / 2.0);
        double middleLat = Lat((box[1] + box[3]) / 2.0);

        double south = middleLat - Span;
        double north = middleLat + Span;

        // <b>Where the layer's own row is, if the picture is linear in each reference.</b> Both
        // are arithmetic rather than measurement: the frame is known and the layer's middle is
        // known, so the only question the picture answers is which of the two it agrees with.
        double inDegrees = (north - middleLat) / (north - south) * Size;

        double inMetres = (MetresY(north) - MetresY(middleLat))
            / (MetresY(north) - MetresY(south)) * Size;

        Assert.True(
            System.Math.Abs(inDegrees - inMetres) > 10.0,
            $"The two references put this layer {System.Math.Abs(inDegrees - inMetres):F1} rows "
            + $"apart at {middleLat:F1}°, which is not far enough to tell them apart. Near the "
            + "equator they agree — move the fixture back off it rather than loosening this.");

        // <b>A fat outline, because the layer is two kilometres across and the frame is four
        // thousand.</b> At seventeen kilometres to the pixel the polygons themselves are
        // invisible; the stroke is drawn in pixels whatever the scale, so what is measured is
        // where the renderer put a feature rather than how big it is.
        string document = Solid(220, 20, 60, 40);

        string degrees = System.String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{middleLon - Span},{south},{middleLon + Span},{north}");

        string metres = System.String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{MetresX(middleLon - Span)},{MetresY(south)},"
            + $"{MetresX(middleLon + Span)},{MetresY(north)}");

        byte[] framedInDegrees = await PreviewAsync(
            root, layer, document, $"?bbox={degrees}&bboxSR=4326&size={Size}x{Size}");

        byte[] framedInMetres = await PreviewAsync(
            root, layer, document, $"?bbox={metres}&size={Size}x{Size}");

        (_, _, _, int topOfDegrees, _, int bottomOfDegrees, int paintedDegrees) =
            PngInk.Ink(framedInDegrees);

        (_, _, _, int topOfMetres, _, int bottomOfMetres, int paintedMetres) =
            PngInk.Ink(framedInMetres);

        Assert.True(
            paintedDegrees > 100 && paintedMetres > 100,
            $"One of the two pictures is empty ({paintedDegrees} and {paintedMetres} painted "
            + "pixels), so there is no row to compare. A blank picture would satisfy every "
            + "comparison below by having no ink anywhere.");

        double rowInDegrees = (topOfDegrees + bottomOfDegrees) / 2.0;
        double rowInMetres = (topOfMetres + bottomOfMetres) / 2.0;

        // Half the stroke's width, which is the most the ink's middle can miss the feature's by.
        const double Tolerance = 6.0;

        Assert.True(
            System.Math.Abs(rowInDegrees - inDegrees) < Tolerance,
            $"Framed in degrees, the layer was drawn on row {rowInDegrees:F1}. A picture linear "
            + $"in that frame puts it on {inDegrees:F1} and one linear in the layer's own "
            + $"reference puts it on {inMetres:F1} — so the frame was projected and the drawing "
            + "was not.");

        Assert.True(
            System.Math.Abs(rowInMetres - inMetres) < Tolerance,
            $"Framed in the layer's own metres, the layer was drawn on row {rowInMetres:F1} "
            + $"rather than {inMetres:F1}. This half asks for no projection at all, so it is the "
            + "control: if it moved, the measurement rather than the projection is wrong.");
    }

    /// <summary>
    /// Naming the layer's own reference changes nothing.
    /// </summary>
    /// <remarks>
    /// <b>The other half, because the fix could have been made by projecting always.</b> A caller
    /// that says what was already true must get the picture it would have got in silence —
    /// otherwise every existing caller's frame moves by whatever a round trip through PROJ costs.
    /// </remarks>
    [Fact]
    public async Task Naming_the_layers_own_reference_draws_the_same_picture_as_naming_none()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        (_, double[] box, int sr) = Found;

        string frame = System.String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{box[0]},{box[1]},{box[2]},{box[3]}");

        string document = Solid(46, 139, 87);

        byte[] silent = await PreviewAsync(root, layer, document, $"?bbox={frame}&size=256x256");

        byte[] named = await PreviewAsync(
            root, layer, document, $"?bbox={frame}&bboxSR={sr}&size=256x256");

        Assert.True(
            silent.AsSpan().SequenceEqual(named),
            $"Naming {sr} — which is what the layer is already in — drew a different picture "
            + $"({silent.Length} bytes against {named.Length}). A no-op that is not one moves "
            + "every frame a caller has ever asked for.");
    }

    /// <summary>
    /// A reference that is not a number is refused in a sentence.
    /// </summary>
    /// <remarks>
    /// <b>Because the alternative is drawing the wrong place.</b> A parameter that is quietly
    /// ignored when it cannot be read leaves the caller believing a conversion happened, and the
    /// picture that comes back looks entirely correct — it is simply of somewhere else.
    /// </remarks>
    [Fact]
    public async Task A_reference_that_is_not_a_number_is_refused_rather_than_ignored()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        (_, double[] box, _) = Found;

        string frame = System.String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{box[0]},{box[1]},{box[2]},{box[3]}");

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"{root}/admin/layers/{Uri.EscapeDataString(layer)}/symbology/preview"
                + $"?bbox={frame}&bboxSR=WGS84"))
        {
            Content = new StringContent(Solid(0, 0, 0), Encoding.UTF8, "application/json"),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string said = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"A 'bboxSR' nothing can read answered {(int)response.StatusCode}: {said}");

        Assert.Contains("bboxSR", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_is_refused_in_a_sentence()
    {
        string root = await RequireServerAsync();
        string layer = await AFillLayerAsync(root);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"{root}/admin/layers/{Uri.EscapeDataString(layer)}/symbology/preview"))
        {
            Content = new StringContent(
                """{"renderer":{"type":"tessellation"}}""",
                Encoding.UTF8,
                "application/json"),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string said = await response.Content.ReadAsStringAsync();

        // <b>A status a form can act on, and a sentence it can print.</b> An editor that got a
        // broken image back would show an empty frame, which reads as *this style draws
        // nothing* rather than *this style could not be read*.
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"A renderer nothing can read answered {(int)response.StatusCode}: {said}");

        Assert.Contains("tessellation", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks for a picture of a candidate document.</summary>
    /// <param name="root">The server.</param>
    /// <param name="layer">The layer to draw.</param>
    /// <param name="candidate">The document to draw it with.</param>
    /// <returns>The PNG.</returns>
    private async Task<byte[]> PreviewAsync(
        string root, string layer, string candidate, string query = "")
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"{root}/admin/layers/{Uri.EscapeDataString(layer)}/symbology/preview{query}"))
        {
            Content = new StringContent(candidate, Encoding.UTF8, "application/json"),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"The preview of '{layer}' answered {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        // <b>`no-store`, and it is worth asserting.</b> The one thing a preview must never do is
        // hand somebody an earlier edit and let them keep it.
        Assert.Contains(
            "no-store",
            response.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>The document a layer has stored, as text, or `""` when it has none.</summary>
    /// <param name="root">The server.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The stored document.</returns>
    private async Task<string> StoredSymbologyAsync(string root, string layer)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/admin/layers/{Uri.EscapeDataString(layer)}/symbology"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Reading '{layer}' back answered {(int)response.StatusCode}.");

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("stored", out JsonElement stored)
            && stored.ValueKind == JsonValueKind.True
            && document.RootElement.TryGetProperty("symbology", out JsonElement had)
                ? had.GetRawText()
                : string.Empty;
    }

    /// <summary>
    /// A polygon layer, by name.
    /// </summary>
    /// <remarks>
    /// <b>Found rather than named.</b> A constant here would tie the test to one fixture and
    /// fail as a missing layer whenever the fixtures move, which is a failure about the suite
    /// rather than about the product. A fill is asked for because a solid fill is the symbol
    /// whose colour covers the most pixels, so a preview that ignored the candidate would still
    /// have to produce a byte-identical answer.
    /// </remarks>
    /// <param name="root">The server.</param>
    /// <returns>The layer's name.</returns>
    private async Task<string> AFillLayerAsync(string root)
    {
        foreach (string service in await EveryServiceNameAsync())
        {
            if (await AboutServiceAsync(service, $"/rest/services/{service}/FeatureServer")
                is not { } about
                || !about.TryGetProperty("layers", out JsonElement layers))
            {
                continue;
            }

            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("id", out JsonElement id))
                {
                    continue;
                }

                if (await AboutServiceAsync(
                        service, $"/rest/services/{service}/FeatureServer/{id.GetInt32()}")
                    is not { } described
                    || !described.TryGetProperty("geometryType", out JsonElement geometry)
                    || geometry.GetString() != "esriGeometryPolygon"
                    || !described.TryGetProperty("name", out JsonElement name))
                {
                    continue;
                }

                if (described.TryGetProperty("extent", out JsonElement extent)
                    && extent.ValueKind == JsonValueKind.Object
                    && name.GetString() is { Length: > 0 } found)
                {
                    // <b>The extent comes back with it, because it was already read.</b> A second
                    // walk to fetch the same document is the shape D-46 records, and the frame
                    // tests below need the box this one just looked at.
                    Found = (
                        found,
                        [
                            extent.GetProperty("xmin").GetDouble(),
                            extent.GetProperty("ymin").GetDouble(),
                            extent.GetProperty("xmax").GetDouble(),
                            extent.GetProperty("ymax").GetDouble(),
                        ],
                        extent.TryGetProperty("spatialReference", out JsonElement said)
                            && (said.TryGetProperty("latestWkid", out JsonElement latest)
                                || said.TryGetProperty("wkid", out latest))
                            ? latest.GetInt32()
                            : 0);

                    return found;
                }
            }
        }

        Assert.Fail(
            $"No polygon layer with an extent is published on {root}, so there is nothing to "
            + "preview. This FAILS rather than skips: a conformance run with no drawable layer "
            + "is a run that proves nothing about drawing.");

        return string.Empty;
    }
}
