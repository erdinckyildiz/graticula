using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A service's description is written by <c>PUT /admin/services/{name}/description</c> and read back by the
/// content listing.
/// </summary>
/// <remarks>
/// <b>2026-09-30.</b> The column has been on <c>service</c> since the catalogue began and only the publish
/// composition wrote it, so Studio's item page asked for a description it gave no way to write — the design
/// review's comparison with Portal, where an item's summary and description are edited from its page.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ServiceDescriptionConformanceTests : ArcGisClient
{
    /// <summary>A description written is the one listed; an empty one clears it.</summary>
    [Fact]
    public async Task A_description_written_is_the_one_the_content_listing_returns()
    {
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE")
            ?? await AnyServiceNameAsync()
            ?? throw new InvalidOperationException("No service to describe.");

        int cut = service.LastIndexOf('/');
        string bare = cut < 0 ? service : service[(cut + 1)..];
        string path = $"/admin/services/{Uri.EscapeDataString(bare)}/description{FolderQuery(service)}";

        string? before = await ListedDescriptionAsync(service);
        string text = "Written by the conformance suite " + Guid.NewGuid().ToString("N")[..8];

        try
        {
            (int status, string body) = await AdminAsync(
                HttpMethod.Put, path, JsonSerializer.Serialize(new { description = text }));

            Assert.True(status == 200, $"Writing a description answered {status}: {body}");
            Assert.Equal(text, await ListedDescriptionAsync(service));

            (status, body) = await AdminAsync(
                HttpMethod.Put, path, JsonSerializer.Serialize(new { description = "  " }));

            Assert.True(status == 200, $"Clearing a description answered {status}: {body}");
            Assert.True(string.IsNullOrEmpty(await ListedDescriptionAsync(service)), "A blank description was stored rather than cleared.");
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, path, JsonSerializer.Serialize(new { description = before }));
        }
    }

    /// <summary>
    /// Credits written are what every face's <c>copyrightText</c> says, the layer's too, and Portal's
    /// <c>accessInformation</c>; empty clears them; past the limit is refused.
    /// </summary>
    /// <remarks>
    /// Every document wrote <c>copyrightText</c> as an empty string and nothing stored one, so the showcase's Copernicus
    /// attribution sat in a description no client's attribution reads (the ArcGIS veteran's review, 2026-10-13).
    /// </remarks>
    [Fact]
    public async Task Credits_written_are_every_faces_copyright_text_and_the_items_credits()
    {
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE")
            ?? await AnyServiceNameAsync()
            ?? throw new InvalidOperationException("No service to credit.");

        int cut = service.LastIndexOf('/');
        string bare = cut < 0 ? service : service[(cut + 1)..];
        string path = $"/admin/services/{Uri.EscapeDataString(bare)}/credits{FolderQuery(service)}";
        string text = "© Conformance suite " + Guid.NewGuid().ToString("N")[..8];

        try
        {
            (int status, string body) = await AdminAsync(HttpMethod.Put, path, JsonSerializer.Serialize(new { credits = text }));
            Assert.True(status == 200, $"Writing credits answered {status}: {body}");

            foreach (string face in new[] { "FeatureServer", "FeatureServer/0", "MapServer", "MapServer/0", "VectorTileServer" })
            {
                (int read, string document) = await AdminAsync(HttpMethod.Get, $"/rest/services/{service}/{face}?f=json");
                Assert.True(read == 200, $"{face} answered {read}");
                Assert.Equal(text, JsonDocument.Parse(document).RootElement.GetProperty("copyrightText").GetString());
            }

            (_, string listing) = await AdminAsync(HttpMethod.Get, "/content/items");
            Assert.Equal(text, JsonDocument.Parse(listing).RootElement.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("name").GetString() == service).GetProperty("credits").GetString());

            (_, string search) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/search?q={Uri.EscapeDataString(bare)}&num=100&f=json");
            Assert.Contains(JsonDocument.Parse(search).RootElement.GetProperty("results").EnumerateArray(),
                r => r.TryGetProperty("accessInformation", out JsonElement a) && a.GetString() == text);

            (status, body) = await AdminAsync(HttpMethod.Put, path, JsonSerializer.Serialize(new { credits = new string('x', 1001) }));
            Assert.True(status == 400 && body.Contains("1000", StringComparison.Ordinal), $"Too long answered {status}: {body}");

            (status, _) = await AdminAsync(HttpMethod.Put, path, JsonSerializer.Serialize(new { credits = " " }));
            Assert.Equal(200, status);
            (_, string cleared) = await AdminAsync(HttpMethod.Get, $"/rest/services/{service}/FeatureServer?f=json");
            Assert.Equal(string.Empty, JsonDocument.Parse(cleared).RootElement.GetProperty("copyrightText").GetString());
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, path, JsonSerializer.Serialize(new { credits = (string?)null }));
        }
    }

    /// <summary>Past the limit is refused with the limit named; a service that is not there is a 404.</summary>
    [Fact]
    public async Task Too_long_is_refused_and_a_missing_service_is_not_found()
    {
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE")
            ?? await AnyServiceNameAsync()
            ?? throw new InvalidOperationException("No service to describe.");

        int cut = service.LastIndexOf('/');
        string bare = cut < 0 ? service : service[(cut + 1)..];

        (int status, string body) = await AdminAsync(
            HttpMethod.Put,
            $"/admin/services/{Uri.EscapeDataString(bare)}/description{FolderQuery(service)}",
            JsonSerializer.Serialize(new { description = new string('x', 4001) }));

        Assert.Equal(400, status);
        Assert.Contains("4000", body, StringComparison.Ordinal);

        (status, body) = await AdminAsync(
            HttpMethod.Put,
            "/admin/services/zz_no_such_service_anywhere/description?folder=hosted",
            JsonSerializer.Serialize(new { description = "nothing" }));

        Assert.True(status == 404, $"Describing a service that does not exist answered {status}: {body}");
    }

    private async Task<string?> ListedDescriptionAsync(string service)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/content/items");

        Assert.Equal(200, status);

        JsonElement item = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("name").GetString() == service);

        return item.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
    }
}
