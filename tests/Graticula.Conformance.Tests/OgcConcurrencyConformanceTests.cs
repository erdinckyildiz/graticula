using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Two writers editing one feature, and what the second one is told.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-186](../../docs/architecture-debt.md), and
/// [ADR-005](../../docs/adr/ADR-005-api-architecture.md) condition 4 calls the thing this
/// suite checks for <em>the worst defect class an editing API can have</em>.</b> Before this
/// existed, two clients editing one feature both got 204 and one of them lost its work with
/// nothing anywhere saying so — not a status code, not a log line, not a difference either
/// client could see.
/// </para>
/// <para>
/// <b>It drives the real thing rather than a simulation.</b> The second writer here is a
/// second HTTP request, and what it sends back is the entity tag the first read handed out.
/// A test that compared versions in process would prove the comparison and not the round
/// trip, and the round trip is where the header syntax, the quoting and the database's own
/// version have to agree.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class OgcConcurrencyConformanceTests : ArcGisClient
{
    private const string GeoJson = "application/geo+json";

    private static string Collection()
    {
        string? qualified =
            Environment.GetEnvironmentVariable(OgcWriteConformanceTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(qualified),
            $"{OgcWriteConformanceTests.LayerVariable} is not set, so these tests FAIL rather "
            + "than skip. Name an editable layer, e.g. hosted/ci_editable.");

        int slash = qualified!.LastIndexOf('/');

        return slash < 0 ? qualified : qualified[(slash + 1)..];
    }

    private async Task<(HttpStatusCode Status, string Body, string? ETag, string? Location)>
        SendAsync(HttpMethod method, string path, string? json = null, string? ifMatch = null)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));

        await AuthenticateAsync(request, root);

        if (ifMatch is not null)
        {
            // Added without validation, because half of what this suite sends is deliberately
            // not a well-formed entity tag and the point is what the *server* does with it.
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

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
            response.Headers.ETag?.ToString(),
            response.Headers.Location?.ToString());
    }

    /// <summary>Creates a feature to edit and returns its item path and text property.</summary>
    private async Task<(string Item, string Property)> SeedAsync()
    {
        string items =
            $"/ogc/features/v1/collections/{Uri.EscapeDataString(Collection())}/items";

        (HttpStatusCode listed, string page, _, _) =
            await SendAsync(HttpMethod.Get, $"{items}?limit=1");

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

        (HttpStatusCode created, string refusal, _, string? location) = await SendAsync(
            HttpMethod.Post,
            items,
            "{\"type\":\"Feature\","
            + "\"geometry\":{\"type\":\"Point\",\"coordinates\":[32.8712,39.9601]},"
            + "\"properties\":{\"" + text + "\":\"concurrency-probe\"}}");

        Assert.True(created == HttpStatusCode.Created, $"POST answered {(int)created}: {refusal}");
        Assert.False(string.IsNullOrEmpty(location), "A create answered 201 with no Location.");

        return (new Uri(location!).AbsolutePath, text!);
    }

    private static string Feature(string property, string value) =>
        "{\"type\":\"Feature\","
        + "\"geometry\":{\"type\":\"Point\",\"coordinates\":[32.8712,39.9601]},"
        + "\"properties\":{\"" + property + "\":\"" + value + "\"}}";

    [Fact]
    public async Task A_single_feature_read_hands_out_the_tag_a_write_can_carry()
    {
        (string item, string property) = await SeedAsync();

        try
        {
            (HttpStatusCode read, _, string? tag, _) = await SendAsync(HttpMethod.Get, item);

            Assert.Equal(HttpStatusCode.OK, read);

            Assert.False(
                string.IsNullOrEmpty(tag),
                "The single-feature read returned no ETag. A client cannot send `If-Match` it "
                + "was never given, so optimistic concurrency would be unreachable over HTTP "
                + "however well the writer implements it.");

            Assert.False(
                tag!.StartsWith("W/", StringComparison.Ordinal),
                $"The ETag `{tag}` is weak. RFC 9110 §13.1.1 says a weak tag never satisfies "
                + "`If-Match`, so this server would refuse every conditional write it invited.");

            (HttpStatusCode wrote, string body, _, _) =
                await SendAsync(HttpMethod.Put, item, Feature(property, "first"), tag);

            Assert.True(
                wrote == HttpStatusCode.NoContent,
                $"A PUT carrying the tag the read handed out answered {(int)wrote}: {body}");

            // <b>And the tag moves.</b> A version that never changes would pass the test
            // above forever and protect nothing: every stale client would keep matching.
            (_, _, string? after, _) = await SendAsync(HttpMethod.Get, item);

            Assert.NotEqual(tag, after);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, item);
        }
    }

    [Fact]
    public async Task The_second_writer_is_refused_rather_than_allowed_to_overwrite()
    {
        (string item, string property) = await SeedAsync();

        try
        {
            (_, _, string? stale, _) = await SendAsync(HttpMethod.Get, item);

            Assert.False(string.IsNullOrEmpty(stale), "No ETag to go stale.");

            // Somebody else writes. This is the client that wins, and it is unconditional
            // on purpose: the loser must be refused whether or not the winner was careful.
            (HttpStatusCode other, string otherBody, _, _) =
                await SendAsync(HttpMethod.Put, item, Feature(property, "winner"));

            Assert.True(
                other == HttpStatusCode.NoContent,
                $"The first write answered {(int)other}: {otherBody}");

            (HttpStatusCode refused, string refusal, _, _) =
                await SendAsync(HttpMethod.Put, item, Feature(property, "loser"), stale);

            Assert.True(
                refused == HttpStatusCode.PreconditionFailed,
                $"A write carrying a version that has moved answered {(int)refused} rather "
                + $"than 412: {refusal}");

            // <b>The assertion with teeth.</b> A 412 that still applied the edit would be
            // worse than no precondition at all, because the client would believe its work
            // was rejected while the other writer's was quietly destroyed.
            (_, string current, _, _) = await SendAsync(HttpMethod.Get, item);

            Assert.Equal(
                "winner",
                JsonDocument.Parse(current).RootElement
                    .GetProperty("properties").GetProperty(property).GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, item);
        }
    }

    [Fact]
    public async Task A_delete_carrying_a_version_that_moved_leaves_the_feature_there()
    {
        (string item, string property) = await SeedAsync();
        bool gone = false;

        try
        {
            (_, _, string? stale, _) = await SendAsync(HttpMethod.Get, item);

            await SendAsync(HttpMethod.Put, item, Feature(property, "moved"));

            (HttpStatusCode refused, string refusal, _, _) =
                await SendAsync(HttpMethod.Delete, item, ifMatch: stale);

            Assert.True(
                refused == HttpStatusCode.PreconditionFailed,
                $"A conditional delete on a version that moved answered {(int)refused} rather "
                + $"than 412: {refusal}");

            (HttpStatusCode still, _, string? fresh, _) = await SendAsync(HttpMethod.Get, item);

            Assert.True(
                still == HttpStatusCode.OK,
                "The delete was refused with 412 and removed the row anyway, which is the one "
                + "outcome worse than not checking at all.");

            (HttpStatusCode removed, string body, _, _) =
                await SendAsync(HttpMethod.Delete, item, ifMatch: fresh);

            Assert.True(
                removed == HttpStatusCode.NoContent,
                $"A conditional delete carrying the current tag answered {(int)removed}: {body}");

            gone = true;
        }
        finally
        {
            if (!gone)
            {
                await SendAsync(HttpMethod.Delete, item);
            }
        }
    }

    [Fact]
    public async Task An_If_Match_that_cannot_be_compared_is_refused_rather_than_ignored()
    {
        (string item, string property) = await SeedAsync();

        try
        {
            // A weak tag is syntactically fine and can never satisfy If-Match. Ignoring it
            // would apply the edit and answer 204 to a client that believes it is protected.
            (HttpStatusCode refused, string refusal, _, _) = await SendAsync(
                HttpMethod.Put, item, Feature(property, "unprotected"), "W/\"1\"");

            Assert.True(
                refused == HttpStatusCode.BadRequest,
                $"An `If-Match` this server cannot compare answered {(int)refused} rather than "
                + $"400: {refusal}");

            (_, string current, _, _) = await SendAsync(HttpMethod.Get, item);

            Assert.Equal(
                "concurrency-probe",
                JsonDocument.Parse(current).RootElement
                    .GetProperty("properties").GetProperty(property).GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, item);
        }
    }

    [Fact]
    public async Task A_star_precondition_asks_only_that_the_feature_exist()
    {
        (string item, string property) = await SeedAsync();

        try
        {
            (HttpStatusCode wrote, string body, _, _) =
                await SendAsync(HttpMethod.Put, item, Feature(property, "starred"), "*");

            Assert.True(
                wrote == HttpStatusCode.NoContent,
                $"`If-Match: *` on a feature that exists answered {(int)wrote} rather than 204: "
                + body);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, item);
        }
    }

    [Fact]
    public async Task A_precondition_on_a_feature_that_is_not_there_cannot_hold()
    {
        // RFC 9110 §13.2.2 evaluates `If-Match` before the method's own semantics, and no
        // entity tag — `*` included — matches a resource with no representation. The same
        // request without `If-Match` is 404, which the write suite asserts.
        string item =
            $"/ogc/features/v1/collections/{Uri.EscapeDataString(Collection())}/items/2147483000";

        (HttpStatusCode refused, string refusal, _, _) =
            await SendAsync(HttpMethod.Delete, item, ifMatch: "*");

        Assert.True(
            refused == HttpStatusCode.PreconditionFailed,
            $"A conditional delete of a feature that is not there answered {(int)refused} "
            + $"rather than 412: {refusal}");
    }
}
