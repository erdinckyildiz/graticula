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
