using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The ImageServer face, asked from outside.
/// </summary>
/// <remarks>
/// <para>
/// <b>The face [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)
/// shipped, and the questions the 2026-08-20 gates taught this suite to ask.</b> Does
/// the document claim only what it answers; is a private service indistinguishable
/// from a missing one; does a refusal name what was wrong; is an empty answer a valid
/// image rather than an error.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because it walks the catalogue.</b> xUnit runs
/// test classes in parallel and another class in this assembly reconfigures live
/// services — [D-75](../../docs/architecture-debt.md), whose cause turned out to be
/// exactly this attribute being missing.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ImageServerConformanceTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body, string? Type)> FetchAsync(
        string path, bool anonymous = false)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));

        if (!anonymous)
        {
            await AuthenticateAsync(request, root);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Any registered image service, or null when none is published.</summary>
    private async Task<string?> AnyImageServiceAsync()
    {
        (HttpStatusCode status, string body, _) = await FetchAsync("/admin/coverages");

        if (status != HttpStatusCode.OK)
        {
            return null;
        }

        return JsonDocument.Parse(body).RootElement
            .GetProperty("coverages")
            .EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    [Fact]
    public async Task The_service_document_describes_the_coverage_it_serves()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, _) =
            await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement document = JsonDocument.Parse(body).RootElement;

        Assert.True(document.GetProperty("bandCount").GetInt32() > 0);
        Assert.NotEqual(0, document.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        // <b>An extent with area, because a service whose extent is a point cannot be
        // framed.</b> Two layers with zero-height extents already made WMS refuse a
        // client's own published bbox on 2026-08-20.
        JsonElement extent = document.GetProperty("fullExtent");

        Assert.True(extent.GetProperty("xmax").GetDouble() > extent.GetProperty("xmin").GetDouble());
        Assert.True(extent.GetProperty("ymax").GetDouble() > extent.GetProperty("ymin").GetDouble());
    }

    [Fact]
    public async Task Every_capability_the_document_claims_is_one_it_answers()
    {
        // Correctness gate 2's fifth finding, asked of the newest face before anybody
        // has a chance to find it from outside. `Map,Query,Data` was untrue on
        // MapServer and was repaired by removing the claim rather than adding a route.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string body, _) = await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        string claimed = JsonDocument.Parse(body).RootElement
            .GetProperty("capabilities").GetString() ?? string.Empty;

        // <b>The whole string, not a substring of it.</b> This read
        // `Contains("Image")` and `DoesNotContain("Catalog")`, which would have passed a
        // document claiming anything at all beside `Image` — and one did, for the length
        // of an afternoon on 2026-08-22, while `tilemap` was still not a route.
        //
        // <b>Naming the exact string is what makes ADR-043 condition 5 testable.</b>
        // Adding a capability now means editing this line by hand, and editing it is the
        // moment somebody has to say which route answers the new claim. `Tilemap` was
        // added the same day the two routes below were, and the test below asks them to
        // answer rather than taking the document's word for it.
        Assert.Equal("Image,Tilemap", claimed);
    }

    [Fact]
    public async Task Every_capability_the_document_claims_has_a_route_that_answers()
    {
        // <b>The other half of condition 5, and the half that a string comparison cannot
        // do.</b> Reading `Image,Tilemap` out of the document proves the claim was made,
        // not that it was true. Each word here is asked to answer, from outside, over
        // HTTP, the way a client would.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string body, _) = await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        JsonElement document = JsonDocument.Parse(body).RootElement;
        string claimed = document.GetProperty("capabilities").GetString() ?? string.Empty;

        foreach (string capability in claimed.Split(','))
        {
            switch (capability.Trim())
            {
                case "Image":
                {
                    (HttpStatusCode status, _, string? type) = await FetchAsync(
                        $"/rest/services/{service}/ImageServer/exportImage"
                        + "?size=16,16&format=png&f=image");

                    Assert.Equal(HttpStatusCode.OK, status);
                    Assert.Equal("image/png", type);
                    break;
                }

                case "Tilemap":
                {
                    // A scheme has to exist for a tile to be nameable at all, so the
                    // claim is checked against `tileInfo` before the route is asked.
                    JsonElement info = document.GetProperty("tileInfo");

                    Assert.Equal(256, info.GetProperty("rows").GetInt32());
                    Assert.NotEqual(0, info.GetProperty("lods").GetArrayLength());

                    // <b>One tile, not a 2 by 2 block.</b> Level 0 of ArcGIS's WGS 84
                    // scheme is two tiles across and *one* down, so a 2 by 2 block at row
                    // zero reaches past the bottom of the grid and is refused — correctly.
                    // A single tile at the origin is inside every scheme this server
                    // builds, which is what a capability check needs: the narrowest
                    // request that still exercises the route.
                    (HttpStatusCode status, string map, _) = await FetchAsync(
                        $"/rest/services/{service}/ImageServer/tilemap/0/0/0/1/1?f=json");

                    Assert.Equal(HttpStatusCode.OK, status);

                    JsonElement answer = JsonDocument.Parse(map).RootElement;

                    Assert.True(
                        answer.TryGetProperty("data", out JsonElement data),
                        "`tilemap` is claimed in capabilities and answered: " + map);

                    Assert.Equal(1, data.GetArrayLength());
                    Assert.True(answer.GetProperty("valid").GetBoolean());
                    break;
                }

                default:
                    Assert.Fail(
                        $"The document claims `{capability}` and this test does not know "
                        + "which route answers it. Either the route is missing or this "
                        + "test is, and both are ADR-043 condition 5.");

                    break;
            }
        }
    }

    [Fact]
    public async Task A_tile_is_the_same_picture_as_an_export_of_the_same_ground()
    {
        // <b>The two paths share their drawing code and this is what asserts that they
        // still do.</b> A tile is an export whose extent and size came from a scheme
        // instead of a query string; if the two ever produce different pictures of the
        // same ground, a tiled map disagrees with the image beside it and nothing in
        // either answer says so.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string body, _) = await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        JsonElement document = JsonDocument.Parse(body).RootElement;
        JsonElement info = document.GetProperty("tileInfo");
        JsonElement extent = document.GetProperty("extent");

        double originX = info.GetProperty("origin").GetProperty("x").GetDouble();
        double originY = info.GetProperty("origin").GetProperty("y").GetDouble();
        int size = info.GetProperty("rows").GetInt32();

        // The level whose pixels are nearest the coverage's own, which is the one a
        // client drawing it at full detail would ask for.
        double native = document.GetProperty("pixelSizeX").GetDouble();

        JsonElement chosen = info.GetProperty("lods").EnumerateArray()
            .OrderBy(l => Math.Abs(l.GetProperty("resolution").GetDouble() - native))
            .First();

        int level = chosen.GetProperty("level").GetInt32();
        double span = chosen.GetProperty("resolution").GetDouble() * size;

        double centreX =
            (extent.GetProperty("xmin").GetDouble() + extent.GetProperty("xmax").GetDouble()) / 2;

        double centreY =
            (extent.GetProperty("ymin").GetDouble() + extent.GetProperty("ymax").GetDouble()) / 2;

        int column = (int)((centreX - originX) / span);
        int row = (int)((originY - centreY) / span);

        double minX = originX + (column * span);
        double maxY = originY - (row * span);

        string root = await RequireServerAsync();

        byte[] tile = await BytesAsync(
            $"{root}/rest/services/{service}/ImageServer/tile/{level}/{row}/{column}", root);

        string box = JsonSerializer.Serialize(new
        {
            xmin = minX,
            ymin = maxY - span,
            xmax = minX + span,
            ymax = maxY,
        });

        byte[] exported = await BytesAsync(
            $"{root}/rest/services/{service}/ImageServer/exportImage"
            + $"?bbox={Uri.EscapeDataString(box)}"
            + $"&bboxSR={info.GetProperty("spatialReference").GetProperty("wkid").GetInt32()}"
            + $"&size={size},{size}&format=png&f=image",
            root);

        Assert.Equal<byte[]>(exported, tile);
    }

    [Fact]
    public async Task A_tile_off_the_edge_of_the_coverage_is_a_picture_rather_than_an_error()
    {
        // <b>A client walking a grid asks for the corners.</b> Answering an error there
        // turns an ordinary map view into a screen of broken tiles; `tilemap` exists so a
        // client can avoid asking, and this is what happens when it does not.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        string root = await RequireServerAsync();

        // <b>Inside the grid and off the coverage, which is the case the condition is
        // about.</b> This asked for row and column 4000 at level 2 — outside the grid
        // entirely — and passed only because out-of-grid tiles used to be answered as
        // blank pictures too. They are refused by name now, so the test had to start
        // naming a tile that actually exists: level 2 of any scheme this server builds is
        // at least 4 tiles each way, and its north-west corner holds no coverage this
        // suite publishes.
        byte[] tile = await BytesAsync(
            $"{root}/rest/services/{service}/ImageServer/tile/2/0/0", root);

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], tile[..4]);
    }

    [Fact]
    public async Task A_tile_outside_the_scheme_is_refused_and_the_grid_is_named()
    {
        // <b>Outside the coverage and outside the grid are different questions, and the
        // answers differ on purpose.</b> A tile inside the grid with no coverage under it
        // is a transparent picture, because a client walking a grid asks for the corners.
        // A tile the scheme has no name for is a refusal, because there is nothing to draw
        // and nothing to say about it — and answering blank there taught a client that its
        // arithmetic was right when it was not.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/tile/2/4000/4000?f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

        Assert.Equal(400, error.GetProperty("code").GetInt32());

        string message = error.GetProperty("message").GetString() ?? string.Empty;

        // It names the grid, because *outside it* without a size is not actionable.
        Assert.Contains("tiles across", message, StringComparison.Ordinal);
        Assert.Contains("4000", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tile_and_a_tilemap_agree_about_a_tile_that_cannot_exist()
    {
        // <b>The two routes had two guards and the guards disagreed.</b> `tile/0/-1/0` was
        // a refusal and `tilemap/0/-1/0/2/2` was an answer with data in it — one server
        // saying two things about the same tile. They share one guard now, and this asserts
        // the agreement rather than the implementation.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string tile, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/tile/0/-1/0?f=json");

        (_, string map, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/tilemap/0/-1/0/2/2?f=json");

        foreach (string body in new[] { tile, map })
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;

            Assert.True(
                root.TryGetProperty("error", out JsonElement error),
                "A negative row was answered rather than refused: " + body);

            Assert.Equal(400, error.GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task A_tilemap_says_which_tiles_hold_ground_and_which_do_not()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        // The grid's north-west corner: inside every scheme this server builds, and
        // holding none of the coverages this suite publishes.
        (HttpStatusCode status, string body, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/tilemap/2/0/0/2/2?f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement answer = JsonDocument.Parse(body).RootElement;

        // Off the coverage, so every tile is absent — and the block still comes back
        // whole, because a client indexes into it by position.
        Assert.Equal(4, answer.GetProperty("data").GetArrayLength());

        foreach (JsonElement present in answer.GetProperty("data").EnumerateArray())
        {
            Assert.Equal(0, present.GetInt32());
        }

        JsonElement location = answer.GetProperty("location");

        Assert.Equal(0, location.GetProperty("top").GetInt32());
        Assert.Equal(0, location.GetProperty("left").GetInt32());
        Assert.Equal(2, location.GetProperty("width").GetInt32());
        Assert.Equal(2, location.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task A_tilemap_block_larger_than_the_ceiling_is_refused_by_name()
    {
        // <b>The request names its own array size</b>, so the ceiling is stated rather
        // than clamped: a client that hits it knows to ask twice instead of quietly
        // getting a smaller answer than it indexed into.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/tilemap/2/0/0/200/200?f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

        Assert.Equal(400, error.GetProperty("code").GetInt32());

        string message = error.GetProperty("message").GetString() ?? string.Empty;

        Assert.Contains("4096", message, StringComparison.Ordinal);
        Assert.Contains("40000", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tile_at_a_level_this_service_does_not_have_is_refused_by_name()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/tile/9999/0/0?f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(
            "9999", error.GetProperty("message").GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>The body of a request, as bytes, authenticated.</summary>
    /// <param name="url">The whole URL.</param>
    /// <param name="root">The server, for the token.</param>
    /// <returns>Its body.</returns>
    private async Task<byte[]> BytesAsync(string url, string root)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task An_export_returns_an_image_of_the_size_that_was_asked_for()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/rest/services/{service}/ImageServer/exportImage"
                + "?size=128,96&format=png&f=image"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        byte[] png = await response.Content.ReadAsByteArrayAsync();

        Assert.True(png.Length > 8, "An export answered with no bytes.");
        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], png[..4]);

        // The IHDR width and height, big-endian at bytes 16 and 20. Read rather than
        // trusted, because "the server said 200" is what every one of correctness gate
        // 2's five defects also said.
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

        Assert.Equal(128, width);
        Assert.Equal(96, height);
    }

    [Fact]
    public async Task The_request_ArcGIS_Pro_actually_sends_is_drawn()
    {
        // <b>Pro's own exportImage, copied out of a proxy trace and replayed
        // verbatim.</b> Every parameter here is one Pro sent on 2026-08-22 while
        // ADR-043 condition 1 was being paid, and three of them were refused by a
        // parser that read the specification's other spelling:
        //
        //   bbox    an envelope object, not four numbers
        //   bboxSR  {"wkid":102100,"latestWkid":3857} — two codes, and the parser
        //           kept every digit of both and made 1021003857 of them
        //   format  None, which is a client saying *your choice* rather than naming
        //           a format called None
        //
        // <b>Kept as one long literal rather than tidied into named parts</b>, because
        // what is under test is a real client's real request and any tidying is a
        // chance to accidentally test the shape this server already handled. The three
        // fixes each have their own test below; this one asserts they compose.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/rest/services/{service}/ImageServer/exportImage"
                + "?bbox=%7b%22xmin%22%3a3300000%2c%22ymin%22%3a4974400%2c%22xmax%22"
                + "%3a3325600%2c%22ymax%22%3a5000000%7d"
                + "&bboxSR=%7b%22wkid%22%3a102100%2c%22latestWkid%22%3a3857%7d"
                + "&size=256%2c%20256&format=None&compression=None&pixelType=U8"
                + "&interpolation=RSP_NearestNeighbor&compressionQuality=90"
                + "&noData=0.000000%2c%200.000000%2c%200.000000&bsq=false"
                + "&noDataInterpretation=esriNoDataMatchAny&validateExtent=false"
                + "&f=image"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        byte[] png = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], png[..4]);

        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

        Assert.Equal(256, width);
        Assert.Equal(256, height);
    }

    [Fact]
    public async Task An_extent_written_as_an_envelope_object_is_read()
    {
        // The REST specification gives `bbox` two spellings and this server read one.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string document, _) =
            await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        JsonElement extent = JsonDocument.Parse(document).RootElement.GetProperty("extent");

        string envelope = JsonSerializer.Serialize(new
        {
            xmin = extent.GetProperty("xmin").GetDouble(),
            ymin = extent.GetProperty("ymin").GetDouble(),
            xmax = extent.GetProperty("xmax").GetDouble(),
            ymax = extent.GetProperty("ymax").GetDouble(),
        });

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/rest/services/{service}/ImageServer/exportImage"
                + $"?bbox={Uri.EscapeDataString(envelope)}&size=64,64&format=png&f=image"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_reference_object_carrying_two_codes_is_read_as_the_later_one()
    {
        // <b>102100 and 3857 are the same reference and a client sends both.</b> The
        // retired code first, the live one as `latestWkid`, so that a server of any age
        // finds one it knows. Reading them as one number is what refused every request
        // ArcGIS Pro made.
        //
        // Asserted through the refusal rather than the picture: a service in EPSG:3857
        // would draw either way, and what is under test is which number was understood.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/exportImage"
            + "?bbox=0,0,1,1"
            + $"&bboxSR={Uri.EscapeDataString("{\"wkid\":102100,\"latestWkid\":3857}")}"
            + "&size=32,32&format=png&f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        // 1021003857 appearing anywhere in the answer means the two codes were run
        // together again, whether the request was drawn or refused.
        Assert.DoesNotContain("1021003857", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_format_of_None_means_the_server_chooses()
    {
        // ArcGIS Pro sends `format=None&compression=None` for *your choice*. Refusing it
        // by name is the same mistake `jpgpng` was, one client later.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/rest/services/{service}/ImageServer/exportImage"
                + "?size=32,32&format=None&compression=None&f=image"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_operation_this_face_does_not_serve_is_refused_in_ArcGIS_terms()
    {
        // <b>An unserved operation answered with an empty-bodied 404 and ArcGIS Pro
        // asked for one of them forty times in a single workflow.</b> A client cannot
        // tell *no such operation* from *the server broke* when there is no body.
        //
        // The shape is Esri's own: their server answers `multidimensionalInfo` on a
        // service without multidimensional data with HTTP 200 and an error envelope in
        // the body. The status line is 200 and the refusal is inside it, which is
        // ADR-009's rule for this whole face.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, string? type) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/multidimensionalInfo?f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/json", type);

        JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

        Assert.Equal(400, error.GetProperty("code").GetInt32());

        string message = error.GetProperty("message").GetString() ?? string.Empty;

        // It names what was asked for and what this face does serve, because a refusal
        // that says neither sends the reader to somebody else's documentation.
        Assert.Contains("multidimensionalInfo", message, StringComparison.Ordinal);
        Assert.Contains("exportImage", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_extent_that_misses_the_coverage_is_a_valid_empty_image()
    {
        // ADR-041 condition 5's rule, applied to the raster face: a client panning off
        // the edge of its own data has not made a mistake.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, _, string? type) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/exportImage"
            + "?bbox=-170,-80,-160,-70&size=64,64&format=png&f=image");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("image/png", type);
    }

    [Fact]
    public async Task A_request_in_another_reference_is_drawn_rather_than_refused()
    {
        // <b>The behaviour this test asserted was the opposite until 2026-08-21.</b> The
        // face first shipped refusing every reference but the coverage's, because
        // reading a Web Mercator box as degrees would draw somewhere else and return it
        // with a 200 — correctness gate 2's whole subject. The warp exists now and its
        // error is measured at 0.0223 pixels (benchmarks/raster-warp), so refusing
        // became the more expensive of the two answers.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string body, _) = await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        JsonElement document = JsonDocument.Parse(body).RootElement;
        int native = document.GetProperty("spatialReference").GetProperty("wkid").GetInt32();

        if (native != 4326)
        {
            // The conversion below is WGS 84 to Web Mercator and nothing else, so a
            // coverage in another reference is not a case this test can construct.
            return;
        }

        JsonElement extent = document.GetProperty("fullExtent");

        (double x0, double y0) = Mercator(
            extent.GetProperty("xmin").GetDouble(), extent.GetProperty("ymin").GetDouble());

        (double x1, double y1) = Mercator(
            extent.GetProperty("xmax").GetDouble(), extent.GetProperty("ymax").GetDouble());

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}/rest/services/{service}/ImageServer/exportImage"
                + $"?bbox={Number(x0)},{Number(y0)},{Number(x1)},{Number(y1)}"
                + "&bboxSR=3857&size=128,128&format=png&f=image"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        byte[] png = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], png[..4]);

        // <b>Not blank.</b> A warp that landed every pixel outside the window would
        // return a valid transparent PNG and pass every check above, which is exactly
        // the shape of a wrong answer with a 200 on it. A PNG of a solid colour
        // compresses to a few hundred bytes; one carrying the ramp does not.
        Assert.True(
            png.Length > 1000,
            $"The reprojected image is {png.Length} bytes, which is about what an empty "
            + "one costs. A warp that misses draws nothing and still answers 200.");
    }

    [Fact]
    public async Task A_request_in_another_reference_with_no_extent_is_refused_by_name()
    {
        // There is no default extent to give it: the coverage's own extent is written in
        // the coverage's own reference, so answering would mean guessing which ground
        // the client meant.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string body, _) = await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        int native = JsonDocument.Parse(body).RootElement
            .GetProperty("spatialReference").GetProperty("wkid").GetInt32();

        int other = native == 3857 ? 4326 : 3857;

        (_, string refusal, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/exportImage"
            + $"?bboxSR={other.ToString(CultureInfo.InvariantCulture)}&f=image");

        JsonElement error = JsonDocument.Parse(refusal).RootElement.GetProperty("error");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(
            "bbox",
            error.GetProperty("message").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_different_references_in_one_request_are_refused()
    {
        // Esri allows bboxSR and imageSR to differ; this server does not, because the
        // extent would be read in one and the pixels laid out in the other — a picture
        // of the right ground at the wrong shape.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string refusal, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/exportImage"
            + "?bboxSR=4326&imageSR=3857&bbox=0,0,1,1&f=image");

        JsonElement error = JsonDocument.Parse(refusal).RootElement.GetProperty("error");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
    }

    /// <summary>WGS 84 to Web Mercator, which is a sphere and a logarithm.</summary>
    private static (double X, double Y) Mercator(double lon, double lat)
    {
        const double R = 20037508.342789244;

        return (
            lon * R / 180,
            Math.Log(Math.Tan(((90 + lat) * Math.PI) / 360)) / (Math.PI / 180) * R / 180);
    }

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Identify_answers_a_point_inside_the_coverage_with_a_value()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (_, string body, _) = await FetchAsync($"/rest/services/{service}/ImageServer?f=json");

        JsonElement extent = JsonDocument.Parse(body).RootElement.GetProperty("fullExtent");

        double x = (extent.GetProperty("xmin").GetDouble() + extent.GetProperty("xmax").GetDouble()) / 2;
        double y = (extent.GetProperty("ymin").GetDouble() + extent.GetProperty("ymax").GetDouble()) / 2;

        (HttpStatusCode status, string answer, _) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/identify?geometry="
            + x.ToString("R", CultureInfo.InvariantCulture) + ","
            + y.ToString("R", CultureInfo.InvariantCulture));

        Assert.Equal(HttpStatusCode.OK, status);

        string? value = JsonDocument.Parse(answer).RootElement.GetProperty("value").GetString();

        Assert.False(
            string.IsNullOrWhiteSpace(value),
            "The middle of a coverage answered with no value. A pixel that exists has one, "
            + "and null there is how a reader that silently failed would look.");
    }

    [Fact]
    public async Task A_coordinate_that_is_not_a_number_is_refused_rather_than_serialised()
    {
        // <b>This was a 500 reachable without signing in.</b> `double.TryParse` accepts
        // `NaN`, every comparison with `NaN` is false, so it passed the extent check as
        // neither inside nor outside — and then `System.Text.Json` threw, because a
        // non-finite number cannot be written as JSON at all. The client asked about a
        // pixel and got an unhandled exception.
        //
        // The same hole in `exportImage` was quieter and no better: a box of `NaN` passed
        // the has-area check and answered a blank picture with a 200 on it.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        foreach (string query in new[]
        {
            "identify?geometry=NaN,NaN",
            "identify?geometry=Infinity,0",
            "exportImage?bbox=NaN,NaN,NaN,NaN&size=16,16",
            "exportImage?bbox=0,0,Infinity,Infinity&size=16,16",
        })
        {
            (HttpStatusCode status, string body, _) = await FetchAsync(
                $"/rest/services/{service}/ImageServer/{query}&f=json");

            Assert.Equal(HttpStatusCode.OK, status);

            JsonElement root = JsonDocument.Parse(body).RootElement;

            Assert.True(
                root.TryGetProperty("error", out JsonElement error),
                $"`{query}` was answered rather than refused: {body}");

            Assert.Equal(400, error.GetProperty("code").GetInt32());

            Assert.Contains(
                "finite",
                error.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_tile_asked_for_in_the_wrong_shape_is_told_the_shape()
    {
        // <b>Every one of these was a bare, bodiless HTTP 404 — the exact failure the
        // unserved-operation route was written to abolish, one layer down.</b> A `tile`
        // request one segment short matched no route template at all, so it never reached
        // the handler that answers in this face's language.
        //
        // And `.../ImageServer/tile` with nothing after it reached that handler and was
        // told *`tile` is not an operation this image service serves. It serves
        // exportImage, identify, tile and tilemap* — denying and listing the same word in
        // one sentence.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        foreach (string path in new[]
        {
            "tile", "tile/0", "tile/0/0", "tile/abc/0/0", "tile/0/0/0/extra",
            "tilemap", "tilemap/0/0/0", "tilemap/0/0/0/2/2/2",
        })
        {
            (HttpStatusCode status, string body, string? type) = await FetchAsync(
                $"/rest/services/{service}/ImageServer/{path}?f=json");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("application/json", type);

            JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

            Assert.Equal(400, error.GetProperty("code").GetInt32());

            string message = error.GetProperty("message").GetString() ?? string.Empty;

            // The shape, spelled out. A count leaves the reader guessing which segments
            // and in what order, and the order is the part that is easy to get wrong.
            Assert.Contains("{level}", message, StringComparison.Ordinal);

            // And it does not deny an operation it serves in the same breath.
            Assert.DoesNotContain("is not an operation", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_export_asked_for_as_json_answers_where_the_picture_is()
    {
        // <b>This face ignored `f` completely and answered PNG bytes for `f=json`</b>,
        // while the sibling `MapServer/export` has answered the descriptor since it
        // shipped — two faces on one server disagreeing about a parameter both document.
        // The JavaScript API places an image element from this document and then fetches
        // the href, so the href has to be a request the same client can make.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode status, string body, string? type) = await FetchAsync(
            $"/rest/services/{service}/ImageServer/exportImage?size=64,48&format=png&f=json");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/json", type);

        JsonElement answer = JsonDocument.Parse(body).RootElement;

        Assert.Equal(64, answer.GetProperty("width").GetInt32());
        Assert.Equal(48, answer.GetProperty("height").GetInt32());
        Assert.True(answer.GetProperty("scale").GetDouble() > 0);

        string href = answer.GetProperty("href").GetString() ?? string.Empty;

        // <b>The href carries every other parameter and only `f` has changed.</b> One that
        // dropped the size would name a different picture, and the client would place it
        // in a frame sized for this one.
        Assert.Contains("f=image", href, StringComparison.Ordinal);
        Assert.Contains("size=64,48", href, StringComparison.Ordinal);
        Assert.DoesNotContain("f=json", href, StringComparison.Ordinal);

        // The extent is named in a reference, both spellings of the code, like every other
        // reference object this face writes.
        JsonElement reference = answer.GetProperty("extent").GetProperty("spatialReference");

        Assert.Equal(
            reference.GetProperty("wkid").GetInt32(),
            reference.GetProperty("latestWkid").GetInt32());

        // And the href really does return the picture it promises.
        string root = await RequireServerAsync();
        byte[] image = await BytesAsync(href, root);

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], image[..4]);
    }

    [Fact]
    public async Task A_reference_this_server_does_not_have_is_refused_the_same_way_every_time()
    {
        // <b>Some bad references raised inside PostGIS and some did not, and the client saw
        // the difference.</b> `bboxSR=999` reached the exception handler and came back as a
        // bare HTTP 400 carrying a database sentence; `bboxSR=1000000000` did not raise at
        // all and came back as a 91-byte transparent PNG with a 200 on it — a
        // correctly-framed map over empty ground, which is the failure a client cannot tell
        // from *there is nothing there*.
        //
        // The face asks whether the reference exists before it draws, which the sibling WFS
        // face has done since it shipped. What this test pins is that all of them are
        // refused *alike*: same status, same envelope, same shape of message.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        foreach (string srid in new[] { "999", "999999", "1000000000", "2147483647" })
        {
            (HttpStatusCode status, string body, string? type) = await FetchAsync(
                $"/rest/services/{service}/ImageServer/exportImage"
                + $"?bbox=30,39,33,41&bboxSR={srid}&size=16,16&f=image");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("application/json", type);

            JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

            Assert.Equal(400, error.GetProperty("code").GetInt32());

            string message = error.GetProperty("message").GetString() ?? string.Empty;

            // It names the code the client sent, which the database's own sentence did not:
            // PostGIS maps out-of-range codes into a reserved band and complains about
            // that instead, so a client asking about 100000000 was told about 999100.
            Assert.Contains(srid, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_private_image_service_is_indistinguishable_from_a_missing_one()
    {
        // The security gate's headline check on 2026-08-20, applied to the newest face:
        // identical status and identical message, so there is no oracle for whether
        // something exists that the caller may not see.
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        (HttpStatusCode privateStatus, string privateBody, _) =
            await FetchAsync($"/rest/services/{service}/ImageServer?f=json", anonymous: true);

        (HttpStatusCode missingStatus, string missingBody, _) = await FetchAsync(
            "/rest/services/hosted/no_such_image_service_9e3f/ImageServer?f=json",
            anonymous: true);

        Assert.Equal(missingStatus, privateStatus);

        static string Message(string body) =>
            JsonDocument.Parse(body).RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetProperty("message").GetString() ?? string.Empty
                : string.Empty;

        string one = Message(privateBody);
        string two = Message(missingBody);

        Assert.False(string.IsNullOrEmpty(one), "A private service answered with no refusal.");

        // The service name differs between the two sentences; what must match is the
        // shape, so the comparison is on everything after the name.
        Assert.Equal(
            one[(one.IndexOf("is visible to you", StringComparison.Ordinal) + 1)..],
            two[(two.IndexOf("is visible to you", StringComparison.Ordinal) + 1)..]);
    }

    [Fact]
    public async Task A_malformed_request_names_what_was_wrong()
    {
        string? service = await AnyImageServiceAsync();

        if (service is null)
        {
            return;
        }

        foreach ((string query, string wanted) in new[]
        {
            ("bbox=1,2,3", "bbox"),
            ("size=nonsense", "size"),
            ("format=tiff", "format"),
            ("bbox=10,10,5,5", "bbox"),
        })
        {
            (_, string body, _) = await FetchAsync(
                $"/rest/services/{service}/ImageServer/exportImage?{query}&f=image");

            JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");

            Assert.Equal(400, error.GetProperty("code").GetInt32());

            string message = error.GetProperty("message").GetString() ?? string.Empty;

            Assert.Contains(
                wanted,
                message,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
