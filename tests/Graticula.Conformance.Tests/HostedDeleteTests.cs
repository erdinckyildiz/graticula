using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Deleting a hosted service takes its tables with it, and a registered one keeps its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-034](../../docs/adr/ADR-034-server-and-studio.md) §5k, on the owner's instruction:</b>
/// *"servis hosted sa ve silindiyse, datastore dan silinmesi lazım."* Until 2026-08-19 this server had
/// two refusals and no route between them — a service that would not delete while it held layers, and an
/// unpublish that would not touch the table — so importing a geodatabase and changing your mind left the
/// tables and a database client.
/// </para>
/// <para>
/// <b>The table's absence is asserted through the API rather than through SQL.</b> The datasource
/// capability listing is what the publish form reads to offer tables, so a table that has left the
/// datastore leaves that listing — which is a stronger claim than the delete response's own word for it,
/// and does not need this suite to hold a connection string.
/// </para>
/// <para>
/// <b>This test creates what it destroys.</b> Nothing in the demo data is deleted: it imports a corpus
/// shapefile under a `zz_` name, deletes that, and asserts on its own fixture. The register's D-89 is
/// about suites racing over shared state, and the mitigation here is that the fixture exists for seconds
/// and is named so the other suites' filters skip it.
/// </para>
/// </remarks>
public sealed class HostedDeleteTests : ArcGisClient
{
    [Fact]
    public async Task Deleting_a_hosted_service_drops_its_table_and_says_which()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_drop_{Guid.NewGuid():N}"[..20];

        // ---------------------------------------------------------------- a service of our own making
        string table = await ImportCorpusAsync(root, token!, name);

        Assert.False(
            string.IsNullOrWhiteSpace(table),
            "The import did not report the table it created, so this test cannot say what should go.");

        Assert.True(
            await DatastoreHasAsync(root, token!, table),
            $"'{table}' is not in the datasource listing straight after being imported, so this test "
            + "cannot tell a dropped table from one it never saw.");

        // ---------------------------------------------------------------- and the refusal still stands
        (HttpStatusCode plain, string refused) = await DeleteAsync(root, token!, name, drop: false);

        Assert.Equal(HttpStatusCode.Conflict, plain);

        // <b>The old answer is unchanged for a caller who did not ask.</b> A script written against the
        // 409 still gets it; only a request that says `drop=true` empties the service.
        Assert.Contains("still holds", refused, StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- asked for, and it happens
        (HttpStatusCode asked, string body) = await DeleteAsync(root, token!, name, drop: true);

        Assert.Equal(HttpStatusCode.OK, asked);

        JsonElement answer = JsonDocument.Parse(body).RootElement;

        Assert.True(answer.GetProperty("removed").GetBoolean());

        JsonElement layers = answer.GetProperty("layers");

        Assert.Equal(1, layers.GetArrayLength());

        JsonElement one = layers[0];

        Assert.True(one.GetProperty("hosted").GetBoolean(), "The imported layer is not hosted.");
        Assert.True(one.GetProperty("unpublished").GetBoolean());
        Assert.True(
            one.GetProperty("dropped").GetBoolean(),
            $"The response did not say the table was dropped: {body}");

        // <b>And the table is gone from the datastore, which is the claim.</b> The response's own
        // `dropped` is this server reporting on itself; the listing is the datastore answering.
        Assert.False(
            await DatastoreHasAsync(root, token!, table),
            $"'{table}' is still in the datasource listing after a delete that reported dropping it. "
            + "The response and the database disagree, and the database is right.");

        // The service is gone from the directory too, which is the half that always worked.
        using HttpRequestMessage gone = new(
            HttpMethod.Get, $"{root}/rest/services/hosted/{name}/FeatureServer?f=json");

        using HttpResponseMessage answered = await Http.SendAsync(gone);

        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
    }

    /// <summary>Imports a corpus shapefile and returns the table it landed in.</summary>
    private async Task<string> ImportCorpusAsync(string root, string token, string name)
    {
        string path = System.IO.Path.Combine(
            RepositoryRoot(), "tests", "Graticula.Core.Tests", "corpus", "shapefile", "points.zip");

        Assert.True(System.IO.File.Exists(path), $"The corpus archive is not at {path}.");

        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(await System.IO.File.ReadAllBytesAsync(path));

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(bytes, "file", "points.zip");
        form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/hosted/import")
        {
            Content = form,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"The import of '{name}' failed with {(int)response.StatusCode}: {body}");

        JsonElement made = JsonDocument.Parse(body).RootElement;

        return made.TryGetProperty("table", out JsonElement had) ? had.GetString() ?? "" : "";
    }

    /// <summary>Whether any registered datasource still offers that table.</summary>
    private async Task<bool> DatastoreHasAsync(string root, string token, string table)
    {
        // The publish form reads this to offer tables, so it is the datastore's own answer about what
        // exists rather than the catalogue's about what is published.
        string bare = table.Contains('.', StringComparison.Ordinal)
            ? table[(table.IndexOf('.', StringComparison.Ordinal) + 1)..]
            : table;

        using HttpRequestMessage list = new(HttpMethod.Get, $"{root}/admin/datasources");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage listed = await Http.SendAsync(list);

        JsonElement sources = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("dataSources");

        foreach (JsonElement source in sources.EnumerateArray())
        {
            string id = source.GetProperty("id").GetString() ?? "";

            using HttpRequestMessage ask = new(
                HttpMethod.Get, $"{root}/admin/datasources/{id}/capability");

            ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage got = await Http.SendAsync(ask);

            if (!got.IsSuccessStatusCode)
            {
                continue;
            }

            if ((await got.Content.ReadAsStringAsync()).Contains(bare, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<(HttpStatusCode Status, string Body)> DeleteAsync(
        string root, string token, string name, bool drop)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Delete,
            $"{root}/admin/featureservices/{name}?folder=hosted" + (drop ? "&drop=true" : ""));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string RepositoryRoot()
    {
        System.IO.DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null
            && !System.IO.File.Exists(System.IO.Path.Combine(at.FullName, "CLAUDE.md")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);

        return at!.FullName;
    }
}
