using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A layer's document says, as <c>cacheMaxAge</c>, the lifetime its <c>query</c> answers are sent with — as ArcGIS's
/// documents do (owner decision 2026-10-15, ADR-101 condition 3: <i>"ArcGIS'in istediği gibi davranalım"</i>).
/// </summary>
/// <remarks>
/// <b>Read from both and compared, rather than either asserted alone</b>: the document and the header are written in
/// two places, and a number each computes for itself is two numbers the day one of them learns something.
/// </remarks>
public sealed class ALayerSaysHowLongItsAnswersKeepTests : ArcGisClient
{
    [Fact]
    public async Task The_document_says_the_lifetime_the_query_answer_is_sent_with()
    {
        string root = await RequireServerAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings";

        using HttpClient http = Unverified();

        using HttpResponseMessage document = await http.GetAsync(new Uri($"{root}/rest/services/{service}/FeatureServer/0?f=json"));
        using JsonDocument layer = JsonDocument.Parse(await document.Content.ReadAsStringAsync());

        Assert.True(layer.RootElement.TryGetProperty("cacheMaxAge", out JsonElement said),
            "The layer document carries no cacheMaxAge, which ArcGIS's do.");

        using HttpResponseMessage answer = await http.GetAsync(
            new Uri($"{root}/rest/services/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"));

        string control = answer.Headers.CacheControl?.ToString() ?? string.Empty;
        int sent = answer.Headers.CacheControl?.MaxAge is { } age ? (int)age.TotalSeconds : 0;

        Assert.True(said.GetInt32() == sent,
            $"The document says cacheMaxAge {said.GetInt32()} and the query was sent `Cache-Control: {control}`.");
    }

    private static HttpClient Unverified() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });
}
