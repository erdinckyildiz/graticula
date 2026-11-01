using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A read-only PostGIS layer's query answer carries a validator, and asking again with it is answered 304 — D-266.
/// </summary>
/// <remarks>
/// <b>Until 2026-11-01 only a GeoParquet layer had one</b>; a PostGIS layer's answer had <c>max-age</c> and no tag, so
/// after its lifetime it was fetched whole again. The version comes from the table's statistics
/// (<c>PostGisFeatureSource.CacheValidatorAsync</c>); that every write moves it is held in Platform.Postgres.Tests
/// (<c>ATablesVersionComesFromItsStatisticsTests</c>), against the database, where the writes can be made.
/// </remarks>
public sealed class APostgisLayerRevalidatesTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    [Fact]
    public async Task A_read_only_layers_query_carries_a_tag_and_is_answered_304_with_it()
    {
        string root = await RequireServerAsync();
        string? service = Environment.GetEnvironmentVariable(ServiceVariable);
        Assert.False(string.IsNullOrWhiteSpace(service), $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        string query = $"{root}/rest/services/{service}/FeatureServer/0/query?where=1%3D1&outFields=*&f=json";
        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        using HttpResponseMessage first = await http.GetAsync(new Uri(query));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string? cache = first.Headers.CacheControl?.ToString();
        Assert.True(
            first.Headers.CacheControl?.MaxAge is { } age && age > TimeSpan.Zero,
            $"{service} is the fixture's read-only PostGIS layer and its query should be kept for a while; it said '{cache}'.");

        string? tag = first.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrEmpty(tag), $"A read-only PostGIS layer's query carried no ETag (Cache-Control '{cache}').");

        // Asked again with it: nothing changed, so 304 and no body.
        using HttpRequestMessage again = new(HttpMethod.Get, new Uri(query));
        again.Headers.TryAddWithoutValidation("If-None-Match", tag);
        using HttpResponseMessage revalidated = await http.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Empty(await revalidated.Content.ReadAsByteArrayAsync());

        // A different question is a different representation, and its tag is not this one.
        using HttpResponseMessage other = await http.GetAsync(new Uri(query.Replace("outFields=*", "outFields=objectid", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.NotEqual(tag, other.Headers.ETag?.ToString());
    }
}
