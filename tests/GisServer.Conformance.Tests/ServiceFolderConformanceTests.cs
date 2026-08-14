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
        return await http.GetAsync(new Uri(root + path));
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
}
