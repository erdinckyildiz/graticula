using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

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
/// </remarks>
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
}
