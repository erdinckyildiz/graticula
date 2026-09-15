using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A designed layer may name its spatial reference and its text lengths, and the document reports
/// both.
/// </summary>
/// <remarks>
/// Written 2026-09-15: <c>/admin/hosted/define</c> had no reference to choose — every designed layer
/// was Web Mercator — and no length, so every designed text field said <c>length: null</c>.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ADesignedLayerNamesItsReferenceTests : ArcGisClient
{
    [Fact]
    public async Task A_layer_designed_in_4326_with_a_bounded_text_field_says_so()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_designed_ref_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                srid = 4326,
                fields = new[] { new { name = "label", type = "text", nullable = true, length = 20 } },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            JsonElement document = await GetJsonAsync(feature);

            JsonElement label = document.GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "label");

            Assert.Equal(20, label.GetProperty("length").GetInt32());
            // An empty layer has no extent, so the reference has to be said on its own.
            Assert.Equal(4326, document.GetProperty("sourceSpatialReference").GetProperty("wkid").GetInt32());
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true",
                token!,
                json: null);
        }
    }

    [Fact]
    public async Task A_reference_the_datastore_does_not_know_is_refused_by_value()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = "zz_designed_bad_" + Guid.NewGuid().ToString("N")[..8],
                geometryType = "Point",
                srid = 987654,
                fields = Array.Empty<object>(),
            }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("987654", body, StringComparison.Ordinal);
    }
}
