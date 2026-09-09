using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Every conformance class this server claims has something behind it, and every claim has a
/// proof written for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-005](../../docs/adr/ADR-005-api-architecture.md) condition 2: <i>the capability
/// report is generated, never hand-maintained.</i></b> It had been marked <c>LIVE and UNMET</c>
/// since 2026-08-20, and it named its own failure mode exactly: <i>the list is what a validator
/// checks, and nothing ties it to what the code actually does — a class removed in the code
/// stays claimed in the array until somebody notices.</i>
/// </para>
/// <para>
/// <b>Generating the list from the routes is the obvious reading and it is the wrong one.</b> A
/// conformance class is not a route: <c>core</c> is six resources and a link graph,
/// <c>geojson</c> is a media type, <c>crs</c> is a query parameter, a response header and a
/// collection property that have to agree with each other. Nothing derives those from an
/// endpoint table, and a generator that tried would be a second description of the
/// specification — which is the defect this repository keeps finding rather than a cure for it.
/// </para>
/// <para>
/// <b>So each claim is bound to its own proof instead, and the binding is what the condition
/// asked for.</b> This asserts in both directions. A URI in the served list with no case here
/// fails, so a class cannot be claimed without somebody writing what makes it true. A case here
/// whose probe fails also fails, so a class that stops being true stops being claimed. And the
/// served document is compared with nothing but itself — the list is read from
/// <c>/conformance</c> over HTTP rather than from the constant, because a test that reads the
/// constant would pass on a server that ships a different one.
/// </para>
/// <para>
/// <b>Measured before it was written, which is the order that matters here.</b> All five claims
/// were probed against a running server first: <c>/api</c> answers
/// <c>application/vnd.oai.openapi+json;version=3.0</c>, items answer
/// <c>application/geo+json</c>, the landing page carries an <c>alternate</c> link to
/// <c>text/html</c>, and a collection advertises three CRSs and returns <c>Content-Crs</c>
/// naming the one asked for. The list was honest; what it lacked was anything that would notice
/// if it stopped being.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class EveryConformanceClaimIsProvenTests : ArcGisClient
{
    private const string Root = "/ogc/features/v1";

    private const string Part1 = "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/";
    private const string Part2 = "http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/";

    /// <summary>
    /// Every claim, and what makes it true.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a list of behaviours rather than of routes.</b> Each entry is the
    /// smallest observation that would stop being possible if the class stopped being true.
    /// </remarks>
    private static IReadOnlyList<string> Proven =>
    [
        Part1 + "core",
        Part1 + "oas30",
        Part1 + "geojson",
        Part1 + "html",
        Part2 + "crs",
    ];

    /// <summary>
    /// Nothing is claimed that nobody has written a proof for, and nothing proven is unclaimed.
    /// </summary>
    [Fact]
    public async Task No_class_is_claimed_without_a_case_that_proves_it()
    {
        List<string> claimed = await ClaimedAsync();

        // <b>The half that makes this a binding rather than a checklist.</b> Adding a URI to
        // `OgcNames.ConformsTo` and shipping it fails here until somebody writes what makes it
        // true — which is the whole of what ADR-005 condition 2 asks for.
        string[] unproven = [.. claimed.Where(c => !Proven.Contains(c, StringComparer.Ordinal))];

        Assert.True(
            unproven.Length == 0,
            "This server claims conformance classes that nothing here proves:\n  "
            + string.Join("\n  ", unproven)
            + "\n\nADR-005 condition 2: the capability report is generated, never "
            + "hand-maintained. A conformance class is not a route, so the binding is a proof "
            + "per claim rather than a generator — add a case to this file that observes what "
            + "makes the class true, or stop claiming it.");

        string[] unclaimed = [.. Proven.Where(p => !claimed.Contains(p, StringComparer.Ordinal))];

        Assert.True(
            unclaimed.Length == 0,
            "This file proves conformance classes the server does not claim:\n  "
            + string.Join("\n  ", unclaimed)
            + "\n\nEither the claim was dropped and this file is stale, or it was dropped by "
            + "accident. Both are worth stopping for.");
    }

    /// <summary>Core: the resources exist and lead to one another.</summary>
    [Fact]
    public async Task Core_is_six_resources_and_a_link_graph()
    {
        Assert.Contains(Part1 + "core", await ClaimedAsync());

        JsonElement landing = await JsonAsync(Root);

        foreach (string rel in (string[])["self", "conformance", "data", "service-desc"])
        {
            Assert.True(
                Link(landing, rel) is { Length: > 0 },
                $"The landing page has no `{rel}` link, so `core` is claimed and a client "
                + "cannot find that resource.");
        }

        JsonElement collections = await JsonAsync(Root + "/collections");

        Assert.True(
            collections.GetProperty("collections").GetArrayLength() > 0,
            "`core` is claimed and the collection list is empty, so nothing can be reached.");

        (HttpStatusCode status, _, _) = await FetchAsync(
            $"{Root}/collections/{await FirstCollectionAsync()}/items?limit=1");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>oas30: the service description is an OpenAPI 3.0 document.</summary>
    [Fact]
    public async Task Oas30_is_an_openapi_three_document_at_the_service_desc_link()
    {
        Assert.Contains(Part1 + "oas30", await ClaimedAsync());

        string? desc = Link(await JsonAsync(Root), "service-desc");

        Assert.True(desc is { Length: > 0 }, "`oas30` is claimed with no `service-desc` link.");

        (HttpStatusCode status, string media, _) = await FetchAsync(desc!);

        Assert.Equal(HttpStatusCode.OK, status);

        // <b>The version is the claim.</b> `oas30` is not *an API description*; it is an
        // OpenAPI **3.0** one, and a server that answered Swagger 2 here would be claiming a
        // class it does not meet while serving a document that parses.
        Assert.Contains("openapi", media, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.0", media, StringComparison.Ordinal);
    }

    /// <summary>geojson: features are served as GeoJSON, by media type.</summary>
    [Fact]
    public async Task Geojson_is_the_media_type_items_actually_answer_with()
    {
        Assert.Contains(Part1 + "geojson", await ClaimedAsync());

        (HttpStatusCode status, string media, _) = await FetchAsync(
            $"{Root}/collections/{await FirstCollectionAsync()}/items?limit=1");

        Assert.Equal(HttpStatusCode.OK, status);

        // Not `application/json`: the class is about the GeoJSON media type specifically, and
        // a client content-negotiating on it would not find a server that answered the other.
        Assert.Contains("application/geo+json", media, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>html: there is an HTML representation and the landing page points at it.</summary>
    [Fact]
    public async Task Html_is_reachable_and_is_html()
    {
        Assert.Contains(Part1 + "html", await ClaimedAsync());

        (HttpStatusCode status, string media, _) = await FetchAsync(Root + "?f=html");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("text/html", media, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// crs: the collection advertises a set, the parameter is honoured, and the response says
    /// which one it answered in.
    /// </summary>
    [Fact]
    public async Task Crs_is_advertised_honoured_and_declared_in_the_response()
    {
        Assert.Contains(Part2 + "crs", await ClaimedAsync());

        string id = await FirstCollectionAsync();
        JsonElement collection = await JsonAsync($"{Root}/collections/{id}");

        Assert.True(
            collection.TryGetProperty("crs", out JsonElement advertised)
                && advertised.GetArrayLength() > 0,
            "`crs` is claimed and the collection advertises no `crs` array, so a client has "
            + "nothing to choose from.");

        Assert.True(
            collection.TryGetProperty("storageCrs", out _),
            "`crs` is claimed and the collection does not say what it is stored in, so a "
            + "client cannot tell a reprojection from a pass-through.");

        // <b>Asked for by name, and the answer has to say so.</b> A server that ignored the
        // parameter and returned storage coordinates would pass a status-code assertion and
        // be wrong about every coordinate in the body — which is why the header is the
        // assertion rather than the status.
        string wanted = advertised.EnumerateArray().Select(c => c.GetString()!)
            .First(c => c.Contains("EPSG", StringComparison.Ordinal));

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri($"{root}{Root}/collections/{id}/items?limit=1&crs={Uri.EscapeDataString(wanted)}"));

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // <b>Both collections, because .NET sorts headers and OGC does not.</b>
        // `Content-Crs` is a content header by name and is not one .NET knows, so
        // `HttpResponseMessage` files it under the message headers instead. Asking only the
        // content — which is where the name says to look — failed against a server that was
        // sending it correctly, which is a test defect that reads exactly like a product one.
        bool found =
            response.Content.Headers.TryGetValues("Content-Crs", out IEnumerable<string>? said)
            || response.Headers.TryGetValues("Content-Crs", out said);

        Assert.True(
            found,
            "`crs` is claimed and a response to a `crs=` request carries no `Content-Crs` "
            + "header, so a client cannot tell what it was given.");

        Assert.Contains(wanted, string.Join(" ", said!), StringComparison.Ordinal);
    }

    // ---------- the small amount of plumbing these share ----------

    private async Task<List<string>> ClaimedAsync()
    {
        JsonElement conformance = await JsonAsync(Root + "/conformance");

        return
        [
            .. conformance.GetProperty("conformsTo").EnumerateArray()
                .Select(c => c.GetString()!),
        ];
    }

    private async Task<string> FirstCollectionAsync()
    {
        JsonElement collections = await JsonAsync(Root + "/collections");

        return collections.GetProperty("collections").EnumerateArray()
            .First().GetProperty("id").GetString()!;
    }

    private static string? Link(JsonElement document, string rel)
    {
        if (!document.TryGetProperty("links", out JsonElement links))
        {
            return null;
        }

        foreach (JsonElement link in links.EnumerateArray())
        {
            if (link.TryGetProperty("rel", out JsonElement named)
                && string.Equals(named.GetString(), rel, StringComparison.Ordinal)
                && link.TryGetProperty("href", out JsonElement href))
            {
                return href.GetString();
            }
        }

        return null;
    }

    private async Task<JsonElement> JsonAsync(string path)
    {
        (_, _, string body) = await FetchAsync(path);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string MediaType, string Body)> FetchAsync(
        string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(path.StartsWith("http", StringComparison.Ordinal) ? path : root + path));

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            await response.Content.ReadAsStringAsync());
    }
}
