using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The ArcGIS MapServer face, driven the way an Esri client drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) §5.5.</b> This face and
/// the WMS one draw through the same renderer with the same stored symbology, so
/// most of what could go wrong here is in the vocabulary rather than in the picture:
/// <c>size</c> against <c>WIDTH</c>, <c>bboxSR</c> against <c>CRS</c>,
/// <c>layers=show:0</c> against <c>LAYERS</c>.
/// </para>
/// <para>
/// <b>So this asserts the translation, and asserts that both faces agree.</b> Two
/// rendered faces that drew independently would eventually disagree, and the person
/// who found out would be a user comparing them.
/// </para>
/// </remarks>
public sealed class MapServerConformanceTests : ArcGisClient
{
    private async Task<(string MediaType, byte[] Body)> RawAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>A service that has a MapServer, and its full extent.</summary>
    private async Task<(string Service, string Bbox, int Srid)?> DrawableAsync()
    {
        foreach (string service in await EveryServiceNameAsync())
        {
            JsonElement document;

            try
            {
                document = await GetJsonAsync($"/rest/services/{service}/MapServer");
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                continue;
            }

            if (!document.TryGetProperty("fullExtent", out JsonElement extent)
                || extent.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double minX = extent.GetProperty("xmin").GetDouble();
            double minY = extent.GetProperty("ymin").GetDouble();
            double maxX = extent.GetProperty("xmax").GetDouble();
            double maxY = extent.GetProperty("ymax").GetDouble();

            if (maxX <= minX || maxY <= minY)
            {
                continue;
            }

            int srid = extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32();

            string bbox = string.Join(
                ',',
                new[] { minX, minY, maxX, maxY }
                    .Select(n => n.ToString("0.##########", CultureInfo.InvariantCulture)));

            return (service, bbox, srid);
        }

        return null;
    }

    private static string Number(double value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);

    // ---------- documents ----------

    [Fact]
    public async Task The_catalogue_lists_a_map_server_for_a_service_that_can_be_drawn()
    {
        // <b>A face nobody can find is a face only somebody who already knew the URL
        // can use.</b> The tile face is listed for the same reason, and this one was
        // not listed until it was noticed missing.
        JsonElement folder = await GetJsonAsync("/rest/services/hosted");

        bool found = folder.GetProperty("services").EnumerateArray().Any(
            s => string.Equals(
                s.GetProperty("type").GetString(), "MapServer", StringComparison.Ordinal));

        Assert.True(found, "The hosted folder lists no MapServer at all.");
    }

    [Fact]
    public async Task A_map_service_document_says_what_it_can_and_cannot_do()
    {
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        JsonElement document = await GetJsonAsync($"/rest/services/{drawable!.Value.Service}/MapServer");

        Assert.Equal("Layers", document.GetProperty("mapName").GetString());

        // <b>Claimed capabilities are promises.</b> A document advertising a tile
        // cache this server has no tiles for sends every client down a path that
        // ends in a 404, and it looks like a broken cache rather than an untrue
        // document.
        Assert.False(document.GetProperty("singleFusedMapCache").GetBoolean());
        Assert.False(document.GetProperty("supportsDynamicLayers").GetBoolean());
        Assert.False(document.GetProperty("exportTilesAllowed").GetBoolean());

        Assert.Contains("Map", document.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);

        Assert.True(document.GetProperty("maxImageWidth").GetInt32() > 0);
        Assert.True(document.GetProperty("layers").GetArrayLength() > 0);
    }

    // ---------- export ----------

    [Fact]
    public async Task Export_draws_a_png_and_a_jpeg_of_the_asked_size()
    {
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        (string service, string bbox, int srid) = drawable!.Value;

        (string png, byte[] image) = await RawAsync(
            $"/rest/services/{service}/MapServer/export?bbox={bbox}&bboxSR={srid}"
            + "&size=300,200&format=png&transparent=true&f=image");

        Assert.Equal("image/png", png);
        Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], image[..4]);

        (string jpeg, byte[] photo) = await RawAsync(
            $"/rest/services/{service}/MapServer/export?bbox={bbox}&bboxSR={srid}"
            + "&size=300,200&format=jpg&f=image");

        Assert.Equal("image/jpeg", jpeg);
        Assert.Equal<byte>([0xFF, 0xD8, 0xFF], photo[..3]);
    }

    [Fact]
    public async Task Export_as_json_points_at_an_address_that_returns_the_image()
    {
        // <b>The JavaScript API places an image element from this document.</b> If
        // the href does not fetch the picture, every map drawn through that client is
        // a broken image icon — and the export itself succeeded, so nothing in the
        // server's own logs says anything is wrong.
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        (string service, string bbox, int srid) = drawable!.Value;

        JsonElement document = await GetJsonAsync(
            $"/rest/services/{service}/MapServer/export?bbox={bbox}&bboxSR={srid}"
            + "&size=256,256&format=png&f=json");

        Assert.Equal(256, document.GetProperty("width").GetInt32());
        Assert.True(document.GetProperty("scale").GetDouble() > 0);

        string href = document.GetProperty("href").GetString()!;

        Assert.Contains("f=image", href, StringComparison.Ordinal);

        string root = await RequireServerAsync();

        Assert.StartsWith(root, href, StringComparison.Ordinal);

        (string mediaType, byte[] image) = await RawAsync(href[root.Length..]);

        Assert.Equal("image/png", mediaType);
        Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], image[..4]);
    }

    [Fact]
    public async Task Export_and_wms_draw_the_same_map()
    {
        // <b>One renderer, asserted rather than intended.</b> Two rendered faces that
        // each fetched and drew would eventually differ, and the difference would be
        // found by a user comparing them rather than here.
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        (string service, string bbox, int srid) = drawable!.Value;

        JsonElement document = await GetJsonAsync($"/rest/services/{service}/MapServer");
        JsonElement layers = document.GetProperty("layers");

        Assert.True(layers.GetArrayLength() > 0);

        string layer = layers[0].GetProperty("name").GetString()!;
        int id = layers[0].GetProperty("id").GetInt32();

        (_, byte[] arcgis) = await RawAsync(
            $"/rest/services/{service}/MapServer/export?bbox={bbox}&bboxSR={srid}"
            + $"&size=240,180&format=png&transparent=true&layers=show:{id}&f=image");

        // The same extent through WMS 1.1.1, which is longitude-first like ArcGIS and
        // therefore takes the bbox unchanged. Using 1.3.0 here would be testing the
        // axis rule twice and this operation not at all.
        (_, byte[] wms) = await RawAsync(
            $"/wms?service=WMS&version=1.1.1&request=GetMap&layers={Uri.EscapeDataString(layer)}"
            + $"&styles=&srs=EPSG:{srid.ToString(CultureInfo.InvariantCulture)}&bbox={bbox}"
            + "&width=240&height=180&format=image/png&transparent=true");

        Assert.Equal(arcgis, wms);
    }

    [Theory]
    [InlineData("size=99999,10", "limit")]
    [InlineData("format=svg", "format")]
    [InlineData("layers=hide:0", "hide")]
    [InlineData("bbox=1,2,3", "four")]
    public async Task An_export_this_server_cannot_do_is_refused_with_a_reason(
        string replacement, string expected)
    {
        // <b>Every refusal names what is wrong.</b> An Esri client shows the message
        // verbatim, so a bare "bad request" sends its user to a log file they cannot
        // read — the same argument the WMS face makes for its locators.
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        (string service, string bbox, int srid) = drawable!.Value;

        string url = $"/rest/services/{service}/MapServer/export?bbox={bbox}&bboxSR={srid}"
            + "&size=100,100&format=png&f=image";

        string key = replacement[..replacement.IndexOf('=', StringComparison.Ordinal)];
        string[] parts = url.Split('&');

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = replacement;
            }
        }

        string amended = string.Join('&', parts);

        if (!amended.Contains(replacement, StringComparison.Ordinal))
        {
            amended += "&" + replacement;
        }

        JsonElement document = await GetJsonAsync(amended);

        Assert.True(
            document.TryGetProperty("error", out JsonElement error),
            $"'{replacement}' was answered rather than refused.");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(
            expected, error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- identify and legend ----------

    [Fact]
    public async Task Identify_needs_a_scale_and_refuses_without_one()
    {
        // <b>A tolerance in pixels is meaningless without a scale.</b> Guessing one
        // makes a click find nothing whenever the client's map differs from the
        // guess, which reads as a layer with no data.
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        JsonElement document = await GetJsonAsync(
            $"/rest/services/{drawable!.Value.Service}/MapServer/identify"
            + "?geometry=0,0&geometryType=esriGeometryPoint&tolerance=3&f=json");

        Assert.True(document.TryGetProperty("error", out JsonElement error));
        Assert.Contains(
            "mapExtent", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identify_finds_what_is_under_a_click()
    {
        // <b>Aimed at a feature's own coordinate, not at a grid.</b> The first version
        // of this walked a nine-by-nine grid over the layer's extent and found
        // nothing — correctly, because the layer is sparse points over four
        // kilometres and the tolerance was forty-five metres. A test that can fail
        // for a reason unrelated to what it tests is a test that gets ignored.
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        (string service, string bbox, int srid) = drawable!.Value;

        JsonElement layers = (await GetJsonAsync($"/rest/services/{service}/MapServer"))
            .GetProperty("layers");

        foreach (JsonElement entry in layers.EnumerateArray())
        {
            int id = entry.GetProperty("id").GetInt32();

            JsonElement features = await GetJsonAsync(
                $"/rest/services/{service}/FeatureServer/{id.ToString(CultureInfo.InvariantCulture)}"
                + $"/query?where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=1"
                + $"&outSR={srid.ToString(CultureInfo.InvariantCulture)}&f=json");

            if (!features.TryGetProperty("features", out JsonElement rows)
                || rows.GetArrayLength() == 0
                || !rows[0].TryGetProperty("geometry", out JsonElement geometry))
            {
                continue;
            }

            if (Somewhere(geometry) is not { } at)
            {
                continue;
            }

            JsonElement document = await GetJsonAsync(
                $"/rest/services/{service}/MapServer/identify"
                + $"?geometry={Number(at.X)},{Number(at.Y)}&geometryType=esriGeometryPoint"
                + $"&sr={srid.ToString(CultureInfo.InvariantCulture)}&mapExtent={bbox}"
                + "&imageDisplay=400,400,96&tolerance=6&layers=all&f=json");

            Assert.True(
                document.TryGetProperty("results", out JsonElement results),
                "identify answered without a results array.");

            Assert.True(
                results.GetArrayLength() > 0,
                $"identify found nothing at a coordinate taken from layer {id}'s own first "
                + "feature, which is the one place something must be.");

            JsonElement hit = results[0];

            Assert.True(hit.TryGetProperty("layerId", out _));
            Assert.True(hit.TryGetProperty("attributes", out JsonElement attributes));
            Assert.True(
                attributes.EnumerateObject().Any(), "An identify result carries no attributes.");

            return;
        }

        Assert.Fail("No layer of this service returned a feature with geometry.");
    }

    /// <summary>One coordinate out of an Esri geometry, whatever shape it is.</summary>
    /// <remarks>
    /// <b>The first vertex of the first part.</b> A centroid would be outside a
    /// crescent and off the line of a curve; a vertex is on the feature by
    /// construction, which is what identify has to find.
    /// </remarks>
    private static (double X, double Y)? Somewhere(JsonElement geometry)
    {
        if (geometry.TryGetProperty("x", out JsonElement x)
            && geometry.TryGetProperty("y", out JsonElement y))
        {
            return (x.GetDouble(), y.GetDouble());
        }

        foreach (string name in (string[])["rings", "paths", "points"])
        {
            if (!geometry.TryGetProperty(name, out JsonElement parts) || parts.GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement first = parts[0];

            // `points` is a flat array of coordinates; rings and paths are arrays of
            // arrays of them.
            JsonElement point = first.ValueKind == JsonValueKind.Array
                && first.GetArrayLength() > 0
                && first[0].ValueKind == JsonValueKind.Array
                    ? first[0]
                    : first;

            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
            {
                return (point[0].GetDouble(), point[1].GetDouble());
            }
        }

        return null;
    }

    [Fact]
    public async Task A_legend_carries_an_inline_swatch_per_layer()
    {
        (string Service, string Bbox, int Srid)? drawable = await DrawableAsync();
        Assert.NotNull(drawable);

        JsonElement document = await GetJsonAsync(
            $"/rest/services/{drawable!.Value.Service}/MapServer/legend?f=json");

        JsonElement layers = document.GetProperty("layers");

        Assert.True(layers.GetArrayLength() > 0);

        JsonElement entry = layers[0].GetProperty("legend")[0];

        Assert.Equal("image/png", entry.GetProperty("contentType").GetString());

        byte[] swatch = Convert.FromBase64String(entry.GetProperty("imageData").GetString()!);

        // <b>Decoded, not just present.</b> A base64 string of the wrong bytes is a
        // legend that renders as a broken image in every client and as a valid
        // document in every test that only checks the field exists.
        Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], swatch[..4]);
    }
}
