using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Creating, replacing, updating and deleting a feature over OGC API Features.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-44](../../docs/open-questions.md), owner decision 2026-08-25: build the shape
/// Part 4 defines rather than an extension of our own.</b> The shape is plain HTTP verbs
/// on the item addresses the read side already publishes, and this suite drives all four
/// against a real layer — including the round trip, because a create that answers 201 and
/// stores the wrong coordinates is the failure this surface is most exposed to.
/// </para>
/// <para>
/// <b>It edits the seeded editable layer and cleans up after itself.</b> A conformance
/// suite that leaves rows behind changes the fixture every other test reads, and a suite
/// whose fixture drifts is one whose failures are about the runs before it.
/// </para>
/// </remarks>
public sealed class OgcWriteConformanceTests : ArcGisClient
{
    /// <summary>The layer to edit — the same one the ArcGIS edit suite uses.</summary>
    public const string LayerVariable = "GRATICULA_TEST_EDITABLE";

    private const string GeoJson = "application/geo+json";

    private static string Collection()
    {
        string? qualified = Environment.GetEnvironmentVariable(LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(qualified),
            $"{LayerVariable} is not set, so these tests FAIL rather than skip. Name an editable "
            + "layer, e.g. hosted/ci_editable.");

        // The collection id is the layer's own name, which is the last segment.
        int slash = qualified!.LastIndexOf('/');

        return slash < 0 ? qualified : qualified[(slash + 1)..];
    }

    private async Task<(HttpStatusCode Status, string Body, string? Location)> SendAsync(
        HttpMethod method, string path, string? json = null, string? contentType = null)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));

        await AuthenticateAsync(request, root);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(contentType ?? GeoJson);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_feature_can_be_created_read_back_updated_replaced_and_deleted()
    {
        string collection = Collection();
        string items = $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}/items";

        // <b>The properties are read from an existing feature rather than invented.</b> A
        // suite that hard-codes column names is a suite about one seed script, and this
        // one has already been rewritten once for exactly that (D-65).
        (HttpStatusCode listed, string page, _) = await SendAsync(HttpMethod.Get, $"{items}?limit=1");

        Assert.Equal(HttpStatusCode.OK, listed);

        JsonElement first = JsonDocument.Parse(page).RootElement
            .GetProperty("features")[0];

        string? text = null;

        foreach (JsonProperty property in first.GetProperty("properties").EnumerateObject())
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
            "{\"type\":\"Feature\","
            + "\"geometry\":{\"type\":\"Point\",\"coordinates\":[32.8712,39.9601]},"
            + "\"properties\":{\"" + text + "\":\"ogc-write-probe\"}}");

        Assert.True(
            created == HttpStatusCode.Created,
            $"POST answered {(int)created}: {refusal}");

        Assert.False(
            string.IsNullOrEmpty(location),
            "A create answered 201 with no Location. A client that posts a feature and is "
            + "not told where it went has to list the collection and guess.");

        string item = new Uri(location!).AbsolutePath;

        try
        {
            // <b>The round trip, which is the assertion with teeth.</b> GeoJSON is
            // longitude/latitude in WGS 84 and the layer is usually not, so a create that
            // stored the numbers verbatim would answer 201 and put the feature in the Gulf
            // of Guinea. Reading it back in the same reference is what proves it did not.
            (HttpStatusCode read, string body, _) = await SendAsync(HttpMethod.Get, item);

            Assert.Equal(HttpStatusCode.OK, read);

            JsonElement geometry = JsonDocument.Parse(body).RootElement.GetProperty("geometry");
            JsonElement coordinates = geometry.GetProperty("coordinates");

            Assert.Equal(32.8712, coordinates[0].GetDouble(), 4);
            Assert.Equal(39.9601, coordinates[1].GetDouble(), 4);

            // A partial update changes what it names and leaves the rest, including the
            // geometry it did not mention.
            (HttpStatusCode patched, string patchBody, _) = await SendAsync(
                HttpMethod.Patch,
                item,
                "{\"properties\":{\"" + text + "\":\"patched\"}}",
                "application/merge-patch+json");

            Assert.True(
                patched == HttpStatusCode.NoContent,
                $"PATCH answered {(int)patched}: {patchBody}");

            (_, string afterPatch, _) = await SendAsync(HttpMethod.Get, item);
            JsonElement patchedFeature = JsonDocument.Parse(afterPatch).RootElement;

            Assert.Equal(
                "patched",
                patchedFeature.GetProperty("properties").GetProperty(text!).GetString());

            Assert.Equal(
                32.8712,
                patchedFeature.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble(),
                4);

            (HttpStatusCode replaced, string replaceBody, _) = await SendAsync(
                HttpMethod.Put,
                item,
                "{\"type\":\"Feature\","
                + "\"geometry\":{\"type\":\"Point\",\"coordinates\":[32.9000,40.0000]},"
                + "\"properties\":{\"" + text + "\":\"replaced\"}}");

            Assert.True(
                replaced == HttpStatusCode.NoContent,
                $"PUT answered {(int)replaced}: {replaceBody}");

            (_, string afterPut, _) = await SendAsync(HttpMethod.Get, item);

            Assert.Equal(
                32.9,
                JsonDocument.Parse(afterPut).RootElement
                    .GetProperty("geometry").GetProperty("coordinates")[0].GetDouble(),
                4);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, item);
        }

        (HttpStatusCode gone, _, _) = await SendAsync(HttpMethod.Get, item);

        Assert.Equal(HttpStatusCode.NotFound, gone);
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task A_feature_that_is_not_there_is_not_found_rather_than_bad_request(string verb)
    {
        // <b>404 rather than 400, and it was 400 until the writer said so structurally.</b>
        // The first version read the writer's message looking for *no such*; the writer
        // says "No feature with object id 5 exists", so a missing row was reported to the
        // client as a malformed request and sent it looking for a mistake it did not make.
        string collection = Collection();

        string item =
            $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}/items/999999999";

        (HttpStatusCode status, string body, _) = verb == "DELETE"
            ? await SendAsync(HttpMethod.Delete, item)
            : await SendAsync(
                new HttpMethod(verb),
                item,
                """
                {"type":"Feature","geometry":{"type":"Point","coordinates":[32.8,39.9]},
                 "properties":{}}
                """,
                verb == "PATCH" ? "application/merge-patch+json" : GeoJson);

        Assert.True(
            status == HttpStatusCode.NotFound,
            $"{verb} on a feature that does not exist answered {(int)status}: {body}");
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_write()
    {
        string collection = Collection();
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Delete,
            new Uri(
                $"{root}/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}"
                + "/items/1"));

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound,
            $"An anonymous DELETE answered {(int)response.StatusCode}. A write surface that "
            + "answers anything else to a caller who has not signed in is the finding this "
            + "assertion exists for.");
    }

    [Fact]
    public async Task The_identity_property_is_refused_rather_than_ignored()
    {
        // Silently dropping the one property that decides which row this is would let a
        // client believe it had moved a feature's identity.
        string collection = Collection();
        string items = $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection)}/items";

        (_, string page, _) = await SendAsync(HttpMethod.Get, $"{items}?limit=1");

        JsonElement first = JsonDocument.Parse(page).RootElement.GetProperty("features")[0];

        Assert.True(
            first.TryGetProperty("id", out _),
            "The read surface publishes no `id`, so there is no identity to refuse.");

        (HttpStatusCode status, string body, _) = await SendAsync(
            HttpMethod.Post,
            items,
            """
            {"type":"Feature","geometry":{"type":"Point","coordinates":[32.8,39.9]},
             "properties":{"objectid":123456}}
            """);

        Assert.True(
            status == HttpStatusCode.BadRequest,
            $"Posting a feature that names the identity column answered {(int)status}: {body}");
    }

    [Fact]
    public async Task The_conformance_document_does_not_claim_a_class_nobody_has_checked()
    {
        // <b>The surface works and the claim waits.</b> CLAUDE.md §5 makes a public
        // specification the citation for anything it defines, and this was built to the
        // shape rather than read clause by clause. Advertising a Part 4 conformance class
        // nobody has verified is the over-claim Q-101 closed on, one specification along.
        (HttpStatusCode status, string body, _) =
            await SendAsync(HttpMethod.Get, "/ogc/features/v1/conformance?f=json");

        Assert.Equal(HttpStatusCode.OK, status);

        Assert.DoesNotContain(
            "ogcapi-features-4",
            body,
            StringComparison.OrdinalIgnoreCase);
    }
}
