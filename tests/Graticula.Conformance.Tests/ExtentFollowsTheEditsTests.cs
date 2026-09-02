using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A feature written outside a layer's extent moves the extent.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-199](../../docs/architecture-debt.md).</b> A layer's extent comes from PostgreSQL's
/// statistics — <c>ST_EstimatedExtent</c>, because <c>ST_Extent</c> reads every geometry and
/// costs 1.2–5 s on this project's corpus. The importer runs <c>ANALYZE</c> after every import
/// and says why; nothing did the same after an edit, so a layer this server writes to kept the
/// extent it was imported with.
/// </para>
/// <para>
/// <b>The damaging direction is growth, and it was measured.</b> A probe table of 500 points:
/// insert 50 more a hundred thousand units away and, with no <c>ANALYZE</c>, the estimate stays
/// where it was while the data has moved. The declared extent then covers a fraction of the
/// layer — and the extent is what a client zooms to, what <c>GetCapabilities</c> publishes, and
/// what the console's thumbnail frames. A client cannot see the feature it just wrote.
/// </para>
/// <para>
/// <b>This drives it over HTTP rather than against the database</b>, because the extent that
/// matters is the one the service document publishes, and the path from a statistic to that
/// document runs through a cache with a lifetime of its own.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ExtentFollowsTheEditsTests : ArcGisClient
{
    private const string GeoJson = "application/geo+json";

    private static string Collection()
    {
        string? qualified =
            Environment.GetEnvironmentVariable(OgcWriteConformanceTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(qualified),
            $"{OgcWriteConformanceTests.LayerVariable} is not set, so this test FAILS rather than "
            + "skips. Name an editable layer, e.g. hosted/ci_editable.");

        int slash = qualified!.LastIndexOf('/');

        return slash < 0 ? qualified : qualified[(slash + 1)..];
    }

    private async Task<(HttpStatusCode Status, string Body, string? Location)> SendAsync(
        HttpMethod method, string path, string? json = null)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));

        await AuthenticateAsync(request, root);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(GeoJson);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Headers.Location?.ToString());
    }

    /// <summary>The layer's declared extent, in WGS 84, from the OGC collection description.</summary>
    private async Task<double[]> ExtentAsync(string collection)
    {
        (HttpStatusCode status, string body, _) = await SendAsync(
            HttpMethod.Get,
            $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}");

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement box = JsonDocument.Parse(body).RootElement
            .GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0];

        double[] read = new double[box.GetArrayLength()];

        for (int i = 0; i < read.Length; i++)
        {
            read[i] = box[i].GetDouble();
        }

        Assert.True(read.Length >= 4, "The collection's bbox has fewer than four numbers in it.");

        return read;
    }

    [Fact]
    public async Task A_feature_written_outside_the_extent_moves_the_extent()
    {
        string collection = Collection();
        string items = $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}/items";

        double[] before = await ExtentAsync(collection);

        // <b>Far enough to be unmistakable, near enough to be a plausible mistake.</b> Two
        // degrees east and one north of whatever the layer holds, which no rounding, no
        // reprojection and no 1% statistical padding can account for.
        double longitude = before[2] + 2;
        double latitude = before[3] + 1;

        Assert.True(
            longitude < 180 && latitude < 90,
            $"The editable layer already reaches {before[2]},{before[3]}, so there is nowhere "
            + "outside it left on the planet to write to.");

        (HttpStatusCode listed, string page, _) = await SendAsync(HttpMethod.Get, $"{items}?limit=1");

        Assert.Equal(HttpStatusCode.OK, listed);

        string? text = null;

        foreach (JsonProperty property in JsonDocument.Parse(page).RootElement
            .GetProperty("features")[0].GetProperty("properties").EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                text = property.Name;
                break;
            }
        }

        Assert.False(text is null, "The editable layer has no text property to write to.");

        (HttpStatusCode created, string refusal, string? location) = await SendAsync(
            HttpMethod.Post,
            items,
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":["
            + longitude.ToString("R", CultureInfo.InvariantCulture) + ","
            + latitude.ToString("R", CultureInfo.InvariantCulture)
            + "]},\"properties\":{\"" + text + "\":\"extent-probe\"}}");

        Assert.True(created == HttpStatusCode.Created, $"POST answered {(int)created}: {refusal}");

        string item = new Uri(location!).AbsolutePath;

        try
        {
            // <b>The description is cached, so this waits rather than asserting once.</b>
            // `ServiceContexts` holds a layer's description for thirty seconds; the statistic
            // is refreshed the moment the write commits, and the document catches up when the
            // entry expires. Asserting immediately would be asserting the cache's lifetime.
            double[] after = before;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                after = await ExtentAsync(collection);

                if (after[2] >= longitude - 0.001 && after[3] >= latitude - 0.001)
                {
                    break;
                }

                await Task.Delay(1000);
            }

            Assert.True(
                after[2] >= longitude - 0.001 && after[3] >= latitude - 0.001,
                $"A feature was written at {longitude},{latitude} and the collection still "
                + $"declares an extent reaching only {after[2]},{after[3]}. The extent is what a "
                + "client zooms to and what the console's thumbnail frames, so a feature outside "
                + "it is a feature nobody can find. That is D-199: the import path runs ANALYZE "
                + "and the write path did not.");
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, item);
        }
    }
}
