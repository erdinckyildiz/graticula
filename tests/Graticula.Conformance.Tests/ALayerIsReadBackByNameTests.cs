using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-017 condition 2: <c>GET /admin/layers/{name}</c> reads one layer back, as the listing describes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same row, byte for byte.</b> Both answers come from one method, and the test holds that: a second shape for
/// one layer is how a console ends up showing two different things about it.
/// </para>
/// <para>
/// <b>And the same name rule as every other route under the path</b> — D-109: a name two services share is a 409
/// naming both, and <c>?service=</c> chooses. The ambiguity is made here, by publishing one layer name into two
/// services, because a fixture that happened to have one would be a fixture that could stop having it.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ALayerIsReadBackByNameTests : ArcGisClient
{
    [Fact]
    public async Task One_layer_is_read_back_as_the_listing_describes_it_and_an_ambiguous_name_says_which()
    {
        await RequireServerAsync();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string layer = $"zz_readback_{suffix}";
        string first = $"zz_readback_a_{suffix}";
        string second = $"zz_readback_b_{suffix}";

        try
        {
            (int one, string oneSaid) = await PublishOneAsync(first, layer);
            Assert.True(one is 200 or 201, $"Publishing the first service answered {one}: {oneSaid}");

            // ---- one layer of that name: the listing's row, and the GET's answer, are the same text ----
            (int listed, string listing) = await AdminAsync(HttpMethod.Get, "/admin/layers");
            Assert.Equal(200, listed);

            string row = JsonDocument.Parse(listing).RootElement.GetProperty("layers").EnumerateArray()
                .Single(l => l.GetProperty("name").GetString() == layer).GetRawText();

            (int got, string read) = await AdminAsync(HttpMethod.Get, $"/admin/layers/{layer}");
            Assert.True(got == 200, $"Reading the layer back answered {got}: {read}");
            Assert.Equal(row, JsonDocument.Parse(read).RootElement.GetRawText());

            // ---- a name nothing has ----
            (int missing, string missingSaid) = await AdminAsync(HttpMethod.Get, $"/admin/layers/zz_no_such_{suffix}");
            Assert.True(missing == 404, $"A name nothing has answered {missing}: {missingSaid}");

            // ---- the same name in two services: refused, naming both, and ?service= chooses ----
            (int two, string twoSaid) = await PublishOneAsync(second, layer);
            Assert.True(two is 200 or 201, $"Publishing the second service answered {two}: {twoSaid}");

            (int ambiguous, string ambiguousSaid) = await AdminAsync(HttpMethod.Get, $"/admin/layers/{layer}");
            Assert.True(ambiguous == 409, $"A name two services share answered {ambiguous}: {ambiguousSaid}");
            Assert.Contains(first, ambiguousSaid, StringComparison.Ordinal);
            Assert.Contains(second, ambiguousSaid, StringComparison.Ordinal);

            (int chosen, string chosenSaid) = await AdminAsync(HttpMethod.Get, $"/admin/layers/{layer}?service={second}");
            Assert.True(chosen == 200, $"Choosing with ?service= answered {chosen}: {chosenSaid}");
            Assert.Equal(second, JsonDocument.Parse(chosenSaid).RootElement.GetProperty("service").GetString());
        }
        finally
        {
            // Each layer by its service: the bare name is the ambiguity this test made, and a DELETE that sends it
            // is refused 409 like the GET — which is how the first run of this test left both services behind.
            foreach (string service in new[] { second, first })
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/layers/{layer}?service={service}");
                await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{service}");
            }

            (int _, string after) = await AdminAsync(HttpMethod.Get, "/admin/layers");
            Assert.DoesNotContain(layer, after, StringComparison.Ordinal);
        }
    }
}
