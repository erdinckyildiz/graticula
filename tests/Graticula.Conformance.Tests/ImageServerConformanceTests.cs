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

        Assert.Contains("Image", claimed, StringComparison.Ordinal);

        // Nothing else is claimed, because nothing else is answered: no catalogue, no
        // download, no histograms. ADR-043 condition 5.
        Assert.DoesNotContain("Catalog", claimed, StringComparison.Ordinal);
        Assert.DoesNotContain("Download", claimed, StringComparison.Ordinal);
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
