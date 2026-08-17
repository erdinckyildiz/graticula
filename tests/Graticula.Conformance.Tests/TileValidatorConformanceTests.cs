using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>ETag</c> and <c>If-None-Match</c> on tiles.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this buys: expiry costs a header instead of a tile.</b>
/// <c>Cache-Control: max-age=3600</c> stops a client asking for an hour; when
/// the hour is up it asks again, and without a validator the only possible
/// answer is the whole tile. Most tiles never change — a cadastral pyramid is
/// rebuilt when somebody edits a parcel, not hourly — so the common case after
/// expiry was re-sending bytes the caller already had.
/// </para>
/// <para>
/// <b>The header is a list, and that is where implementations quietly fail.</b>
/// A client may send several tags; a proxy revalidating whatever it holds may
/// send <c>*</c>; a cache may weaken a tag to <c>W/"x"</c>. Treating the header
/// as one opaque string re-sends the tile, breaks nothing, and turns the feature
/// off without telling anybody — so each form is tested separately.
/// </para>
/// </remarks>
public sealed class TileValidatorConformanceTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private static string? Configured => Environment.GetEnvironmentVariable(ServiceVariable);

    private async Task<string> RequireTileServiceAsync()
    {
        await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Configured),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip.");

        return Configured!.Trim('/');
    }

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    /// <summary>
    /// A tile that actually has bytes in it.
    /// </summary>
    /// <remarks>
    /// <b>Found from the service's own extent, not hardcoded.</b> Most of a
    /// pyramid is empty, and an empty tile is a 204 with no body and therefore
    /// no validator — a test pointed at one would assert nothing and pass.
    /// </remarks>
    private async Task<Uri> PopulatedTileAsync()
    {
        string root = await RequireServerAsync();
        string service = await RequireTileServiceAsync();

        JsonElement document = await GetJsonAsync($"/rest/services/{service}/VectorTileServer");

        JsonElement extent = document.TryGetProperty("fullExtent", out JsonElement full)
            ? full
            : document.GetProperty("initialExtent");

        double x = (extent.GetProperty("xmin").GetDouble()
                    + extent.GetProperty("xmax").GetDouble()) / 2;

        double y = (extent.GetProperty("ymin").GetDouble()
                    + extent.GetProperty("ymax").GetDouble()) / 2;

        const double Half = 20037508.342789244;

        using HttpClient http = Client();

        foreach (int z in (int[])[13, 12, 14, 10, 16])
        {
            int n = 1 << z;
            int tx = (int)((x + Half) / (2 * Half) * n);
            int ty = (int)((Half - y) / (2 * Half) * n);

            Uri candidate = new(string.Create(
                CultureInfo.InvariantCulture,
                $"{root}/rest/services/{service}/VectorTileServer/tile/{z}/{ty}/{tx}.pbf"));

            using HttpResponseMessage response = await http.GetAsync(candidate);

            if (response.StatusCode == HttpStatusCode.OK
                && (await response.Content.ReadAsByteArrayAsync()).Length > 0)
            {
                return candidate;
            }
        }

        Assert.Fail(
            "No tile with any content was found at the centre of the service's own extent. "
            + "Either the fixture layer is empty or the extent is wrong, and this test cannot "
            + "say anything about validators without bytes to validate.");

        throw new InvalidOperationException();
    }

    private static async Task<(HttpStatusCode Status, long Bytes, string? ETag)> FetchAsync(
        Uri tile, string? ifNoneMatch = null)
    {
        using HttpClient http = Client();
        using HttpRequestMessage request = new(HttpMethod.Get, tile);

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        using HttpResponseMessage response = await http.SendAsync(request);

        byte[] body = await response.Content.ReadAsByteArrayAsync();

        return (response.StatusCode, body.Length, response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task A_tile_with_bytes_carries_a_strong_validator()
    {
        Uri tile = await PopulatedTileAsync();

        (HttpStatusCode status, long bytes, string? etag) = await FetchAsync(tile);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(bytes > 0);

        Assert.NotNull(etag);
        Assert.StartsWith("\"", etag, StringComparison.Ordinal);
        Assert.DoesNotContain("W/", etag, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same tile, twice, yields the same validator.
    /// </summary>
    /// <remarks>
    /// <b>Otherwise the feature is worse than absent.</b> A tag that changes per
    /// response never matches, so every revalidation re-sends the tile and the
    /// client also pays for the conditional request it made first.
    /// </remarks>
    [Fact]
    public async Task The_validator_is_stable_across_requests()
    {
        Uri tile = await PopulatedTileAsync();

        (_, _, string? first) = await FetchAsync(tile);
        (_, _, string? second) = await FetchAsync(tile);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Every form of <c>If-None-Match</c> a real client sends.
    /// </summary>
    [Fact]
    public async Task A_caller_that_already_holds_the_tile_gets_no_bytes()
    {
        Uri tile = await PopulatedTileAsync();

        (_, long full, string? etag) = await FetchAsync(tile);

        Assert.NotNull(etag);
        Assert.True(full > 0);

        foreach ((string header, string what) in ((string, string)[])
        [
            (etag!, "the exact tag"),
            ("*", "a proxy revalidating anything it holds"),
            ("W/" + etag!, "a cache that weakened the tag"),
            ("\"stale-one\", " + etag!, "a list, ours second"),
        ])
        {
            (HttpStatusCode status, long bytes, _) = await FetchAsync(tile, header);

            Assert.Equal(HttpStatusCode.NotModified, status);
            Assert.Equal(0, bytes);
        }
    }

    [Fact]
    public async Task A_caller_holding_something_else_gets_the_tile()
    {
        Uri tile = await PopulatedTileAsync();

        (HttpStatusCode status, long bytes, _) = await FetchAsync(tile, "\"not-this-one\"");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(bytes > 0);
    }

    /// <summary>
    /// The 304 still carries the caching directives.
    /// </summary>
    /// <remarks>
    /// A 304 that omits <c>Cache-Control</c> tells the client its stored copy has
    /// no freshness any more, so the next request is conditional again — the
    /// revalidation saves the body and then happens every single time.
    /// </remarks>
    [Fact]
    public async Task The_not_modified_response_still_says_how_long_it_is_good_for()
    {
        Uri tile = await PopulatedTileAsync();

        (_, _, string? etag) = await FetchAsync(tile);

        using HttpClient http = Client();
        using HttpRequestMessage request = new(HttpMethod.Get, tile);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag!);

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);

        CacheControlHeaderValue? caching = response.Headers.CacheControl;

        Assert.NotNull(caching);
        Assert.True(caching!.MaxAge > TimeSpan.Zero);
        Assert.Equal(etag, response.Headers.ETag?.ToString());
    }

    /// <summary>
    /// An empty tile carries no validator, because it has nothing to validate.
    /// </summary>
    /// <remarks>
    /// Most of a pyramid is emptiness. A 204 has no body, so a tag on it would
    /// be an identifier for the absence of bytes — and a client sending it back
    /// would be asking whether nothing has changed.
    /// </remarks>
    [Fact]
    public async Task An_empty_tile_has_no_validator()
    {
        string root = await RequireServerAsync();
        string service = await RequireTileServiceAsync();

        // Zoom 20 far from anything: empty with near-certainty.
        Uri empty = new(
            $"{root}/rest/services/{service}/VectorTileServer/tile/20/1/1.pbf");

        using HttpClient http = Client();
        using HttpResponseMessage response = await http.GetAsync(empty);

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            return; // The fixture has data there after all; nothing to assert.
        }

        Assert.Null(response.Headers.ETag);
    }
}
