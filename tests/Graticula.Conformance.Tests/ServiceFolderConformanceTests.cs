using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Two URL spaces: hosted services and registered ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the ArcGIS Enterprise shape and it carries a real distinction.</b>
/// A hosted feature class lives in the datastore and is ours to create, alter
/// and drop. A registered one points at a table in somebody else's database and
/// must never be touched. One namespace means every operation re-derives which
/// kind it is holding, and one day gets it wrong.
/// </para>
/// <para>
/// Over HTTP against a real process, like the rest of this suite, and
/// referencing none of our assemblies — the folder name is a string a client
/// types, so a test that reads it from a constant would agree with the server
/// while both were wrong.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because this class walks the catalogue.</b>
/// xUnit runs test classes in parallel, and another class in this assembly publishes,
/// deletes and reconfigures services. A walker outside the collection sees the
/// catalogue mid-change and reports it as a defect in whatever it was testing —
/// [D-75](../../docs/architecture-debt.md), three times on 2026-08-20.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ServiceFolderConformanceTests : ArcGisClient
{
    /// <summary>A client that does not follow redirects, so they can be seen.</summary>
    private static HttpClient Blunt() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private async Task<HttpResponseMessage> HeadOnAsync(string path)
    {
        string root = await RequireServerAsync();
        using HttpClient http = Blunt();

        // The blunt client still signs in. It is blunt about <em>redirects</em>
        // — that is what it is for — not about identity, and a hosted layer
        // shared with the organisation answers 404 to a stranger. Without this
        // the test would be checking sharing while claiming to check
        // capitalisation.
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        return await http.SendAsync(request);
    }

    // ---------- the catalogue ----------

    [Fact]
    public async Task The_root_advertises_the_hosted_folder()
    {
        JsonElement folders = Require(
            await GetJsonAsync("/rest/services"),
            "folders",
            "A client has no way to discover hosted services.");

        Assert.Contains(
            folders.EnumerateArray().Select(f => f.GetString()),
            f => string.Equals(f, "hosted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_folder_lists_services_with_the_folder_on_the_front_of_their_names()
    {
        // A client builds its request URL from this string. A bare name here
        // produces a catalogue whose every entry 404s.
        JsonElement services = (await GetJsonAsync("/rest/services/hosted"))
            .GetProperty("services");

        Assert.All(
            services.EnumerateArray(),
            s => Assert.StartsWith(
                "hosted/", s.GetProperty("name").GetString()!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_folder_listing_does_not_advertise_folders_of_its_own()
    {
        Assert.Empty(
            (await GetJsonAsync("/rest/services/hosted")).GetProperty("folders").EnumerateArray());
    }

    [Fact]
    public async Task The_root_and_the_folder_do_not_list_the_same_service()
    {
        // The whole point of the split. A service in both places means the
        // separation is cosmetic.
        string[] root = [.. (await GetJsonAsync("/rest/services")).GetProperty("services")
            .EnumerateArray().Select(s => s.GetProperty("name").GetString()!)];

        string[] hosted = [.. (await GetJsonAsync("/rest/services/hosted")).GetProperty("services")
            .EnumerateArray().Select(s => s.GetProperty("name").GetString()!["hosted/".Length..])];

        Assert.Empty(root.Intersect(hosted, StringComparer.OrdinalIgnoreCase));
    }

    // ---------- the redirects ----------

    [Fact]
    public async Task A_hosted_service_asked_for_at_the_root_is_redirected_rather_than_missing()
    {
        // 404 would tell a client the service does not exist, which is false.
        // A 301 tells them where it went, and every HTTP client follows it — so
        // URLs built before the folder existed keep working.
        string name = await AnyHostedServiceAsync();

        using HttpResponseMessage response =
            await HeadOnAsync($"/rest/services/{name}/FeatureServer?f=json");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);

        Assert.Contains(
            $"/rest/services/hosted/{name}/FeatureServer",
            response.Headers.Location!.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_redirect_keeps_the_query_string()
    {
        // Dropping it turns a redirect into a different request. A client
        // following it would get the service document instead of its query.
        string name = await AnyHostedServiceAsync();

        using HttpResponseMessage response =
            await HeadOnAsync($"/rest/services/{name}/FeatureServer/0/query?where=1%3D1&f=json");

        Assert.Contains(
            "where=1%3D1",
            response.Headers.Location!.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Following_the_redirect_reaches_the_service()
    {
        // The redirect is only useful if the destination answers.
        string name = await AnyHostedServiceAsync();

        JsonElement service = await GetJsonAsync($"/rest/services/hosted/{name}/FeatureServer");

        Assert.True(service.TryGetProperty("layers", out _)
            || service.TryGetProperty("currentVersion", out _));
    }

    [Fact]
    public async Task Esris_own_capitalisation_of_the_folder_works()
    {
        // ArcGIS writes it "Hosted". A client copying that convention must not
        // meet a 404 over a capital letter.
        string name = await AnyHostedServiceAsync();

        using HttpResponseMessage response =
            await HeadOnAsync($"/rest/services/Hosted/{name}/FeatureServer?f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<string> AnyHostedServiceAsync()
    {
        JsonElement services = (await GetJsonAsync("/rest/services/hosted"))
            .GetProperty("services");

        Assert.True(
            services.GetArrayLength() > 0,
            "No hosted services are visible, so the folder cannot be tested. Import or define "
            + "one and share it.");

        return services[0].GetProperty("name").GetString()!["hosted/".Length..];
    }

    // ---------- more than one folder ----------

    /// <summary>
    /// A second folder does not inherit the first folder's contents.
    /// </summary>
    /// <remarks>
    /// <b>This is a regression test with a short and embarrassing history.</b>
    /// The catalogue decided "is this the hosted folder?" by asking whether the
    /// folder was non-null, which was correct for exactly as long as there was
    /// one folder. The moment Utilities existed, /rest/services/Utilities listed
    /// all five hosted layers under names that 404. Found by opening the URL.
    /// </remarks>
    [Fact]
    public async Task Another_folder_does_not_list_the_hosted_layers()
    {
        JsonElement services = (await GetJsonAsync("/rest/services/Utilities"))
            .GetProperty("services");

        Assert.DoesNotContain(
            services.EnumerateArray(),
            s => string.Equals(
                s.GetProperty("type").GetString(), "FeatureServer", StringComparison.Ordinal));
    }

    /// <summary>Every service the catalogue lists can actually be fetched.</summary>
    /// <remarks>
    /// <b>The property a catalogue exists to have.</b> A client reads this list
    /// and builds URLs from it; an entry that 404s is worse than an absent one,
    /// because the client reports the server as broken rather than as empty.
    /// This walks both folders and asks for each service document in turn.
    /// </remarks>
    [Theory]
    [InlineData("/rest/services")]
    [InlineData("/rest/services/hosted")]
    [InlineData("/rest/services/Utilities")]
    public async Task Every_listed_service_resolves(string catalogue)
    {
        JsonElement services = (await GetJsonAsync(catalogue)).GetProperty("services");

        foreach (JsonElement service in services.EnumerateArray())
        {
            string name = service.GetProperty("name").GetString()!;
            string type = service.GetProperty("type").GetString()!;

            // GetJsonAsync asserts a success status, which is the assertion.
            _ = await GetJsonAsync($"/rest/services/{name}/{type}");
        }
    }

    // ---------- D-28: nothing under /rest/services is ungoverned ----------

    /// <summary>
    /// Every data route has a sharing decision behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-018 condition 5, and the class of defect it closes has one known
    /// member.</b> The geometry service answered anonymously from the day it
    /// shipped — not by anyone's decision, but because sharing was a property of
    /// a layer and that service has none. No amount of reading the sharing code
    /// would have found it: the sharing code was correct. What was missing was a
    /// place for something that is not content, and an absence has nothing for a
    /// reviewer to look at.
    /// </para>
    /// <para>
    /// <b>Asks the running server, not the source.</b> A route added without the
    /// marker appears here, whatever the file it was written in looks like.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task No_data_route_is_reachable_without_a_sharing_decision()
    {
        JsonElement audit = await GetJsonAsync("/admin/routes");

        string[] ungoverned =
        [
            .. audit.GetProperty("routes").EnumerateArray()
                .Where(r => !r.GetProperty("governed").GetBoolean())
                .Select(r =>
                    string.Join(",", r.GetProperty("methods").EnumerateArray()
                        .Select(m => m.GetString()))
                    + " " + r.GetProperty("pattern").GetString()),
        ];

        Assert.True(
            ungoverned.Length == 0,
            "These routes under /rest/services have no sharing decision behind them:\n  "
            + string.Join("\n  ", ungoverned)
            + "\n\nEach either needs to resolve through ServiceLookup, sit in a group with a "
            + "sharing filter, or be marked .Governed(Public) with a reason. This is ADR-018 "
            + "condition 5 and it exists because the geometry service shipped ungoverned.");

        Assert.Equal(0, audit.GetProperty("ungoverned").GetInt32());
    }

    [Fact]
    public async Task The_audit_covers_every_kind_of_service_route()
    {
        // <b>A check that enumerates nothing passes trivially.</b> If the
        // pattern filter in /admin/routes ever stopped matching, this suite
        // would report zero ungoverned routes out of zero routes and read as
        // green.
        JsonElement audit = await GetJsonAsync("/admin/routes");

        string[] patterns =
        [
            .. audit.GetProperty("routes").EnumerateArray()
                .Select(r => r.GetProperty("pattern").GetString()!),
        ];

        Assert.True(patterns.Length > 20, $"Only {patterns.Length} routes were audited.");

        // <b>/wfs joined this list on 2026-08-19 and it is the reason the list
        // matters.</b> The audit filtered on /rest/services alone, so the WFS
        // surface carried its markers and nothing read them — the endpoint
        // reported zero ungoverned about routes it had never looked at. Naming
        // each family here is what turns "forgot to audit a surface" from silence
        // into a failure.
        //
        // <b>And on 2026-08-20 it failed anyway, because the list was in two
        // places.</b> The audit's own prefixes and this array had to be edited
        // together for one new surface, so WMS, MapServer and OGC API Features were
        // added to neither and `/admin/routes` went on reporting *ungoverned: 0*
        // about eight routes it never saw. **A list edited in two places is edited in
        // one.** These families are the ArcGIS sub-surfaces, which have no prefix of
        // their own; the prefixes come from the server's own list below.
        foreach (string family in (string[])
            ["FeatureServer", "VectorTileServer", "GeometryServer", "attachments",
             "queryRelatedRecords"])
        {
            Assert.Contains(patterns, p => p.Contains(family, StringComparison.Ordinal));
        }

        // <b>Every surface this suite knows about is one the audit looked at.</b>
        // The list stays this suite's own — it references none of our assemblies on
        // purpose, because a test reading the server's constant cannot notice the
        // server being wrong about it. What the audit publishes is its *scope*, and
        // these two independent lists are then compared.
        string[] scope =
        [
            .. audit.GetProperty("filteredOn").EnumerateArray().Select(p => p.GetString()!),
        ];

        foreach (string surface in (string[])["/rest/services", "/wfs", "/wms", "/ogc/features"])
        {
            Assert.Contains(
                scope,
                p => p.StartsWith(surface, StringComparison.OrdinalIgnoreCase));

            Assert.Contains(
                patterns,
                p => p.StartsWith(surface, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task The_audit_itself_is_administrative()
    {
        // It lists every route including ones the caller may not reach, which is
        // the opposite of what the directory does — so it is not something an
        // anonymous caller gets.
        using HttpClient http = Anonymous();

        using HttpResponseMessage response =
            await http.GetAsync(new Uri(await RequireServerAsync() + "/admin/routes"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static HttpClient Anonymous() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });
}
