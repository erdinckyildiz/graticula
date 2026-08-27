using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// What unpublishing the last layer of a service leaves behind, and what it says about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-157](../../docs/architecture-debt.md), and half of that row was wrong.</b> It
/// said <em>nothing can remove it</em> — <c>DELETE /admin/featureservices/{name}</c> has
/// existed all along, takes a folder, optionally drops the hosted tables, and says which.
/// The row's own proposed fix, a delete with a decision under it about the tables, was
/// already built and the decision already taken.
/// </para>
/// <para>
/// <b>The state it describes is real, and this is where it stops being read from a route
/// table.</b> The row says plainly that it was <em>not measured against a running
/// server</em>. It is now: an emptied service is listed in the directory, its
/// <c>/FeatureServer</c> answers 200, and <c>/FeatureServer/0</c> answers 404.
/// </para>
/// <para>
/// <b>What was missing is that nobody arriving there is told.</b> The unpublish response
/// now names the state and the route out of it, and this test drives that route to prove
/// the sentence is true rather than reassuring — a message naming an endpoint that does
/// not work is worse than no message.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class EmptiedServiceTests : ArcGisClient
{
    /// <summary>A table the fixture leaves unpublished — the same pair D-157 came from.</summary>
    private const string FreeTable = "zz_free_one";

    [Fact]
    public async Task Unpublishing_the_last_layer_says_the_service_is_empty_and_names_the_way_out()
    {
        string root = await RequireServerAsync();
        string? datastore = await DatastoreIdAsync(root);

        Assert.False(
            string.IsNullOrEmpty(datastore),
            "No datastore data source, so nothing can be published for this test to unpublish.");

        const string layer = "zz_d157_probe";
        const string service = "zz_d157_probe_svc";

        (int published, _) = await AdminAsync(
            root,
            HttpMethod.Post,
            "/admin/layers",
            $$"""
            {"name":"{{layer}}","dataSourceId":"{{datastore}}","schemaName":"hosted",
             "tableName":"{{FreeTable}}","geometryColumn":"shape","geometryType":"Polygon",
             "identityColumn":"objectid","srid":3857,"serviceName":"{{service}}",
             "folder":"hosted"}
            """);

        Assert.True(
            published is 200 or 201,
            $"Could not publish over hosted.{FreeTable}: {published}. This test needs the free "
            + "table tools/ci-free-tables.sql creates.");

        try
        {
            (int status, string body) = await AdminAsync(
                root, HttpMethod.Delete, $"/admin/layers/{layer}");

            Assert.Equal(200, status);

            JsonElement about = JsonDocument.Parse(body).RootElement.GetProperty("service");

            Assert.True(
                about.GetProperty("isEmpty").GetBoolean(),
                "Unpublishing the only layer of a service did not report the service as empty, "
                + "so the operator is not told that the directory now holds a shell.");

            string note = about.GetProperty("note").GetString() ?? string.Empty;

            Assert.Contains("/admin/featureservices/", note, StringComparison.Ordinal);

            // <b>The shell is real, which is the half of D-157 that was right.</b>
            Assert.Equal(200, await AuthenticatedStatusAsync(
                root, $"/rest/services/hosted/{service}/FeatureServer?f=json"));

            Assert.Equal(404, await AuthenticatedStatusAsync(
                root, $"/rest/services/hosted/{service}/FeatureServer/0?f=json"));

            // <b>And the route the message names actually removes it.</b> A sentence
            // pointing at an endpoint is a promise; this is the test that keeps it.
            (int deleted, _) = await AdminAsync(
                root, HttpMethod.Delete, $"/admin/featureservices/{service}?folder=hosted");

            Assert.Equal(200, deleted);

            Assert.Equal(404, await AuthenticatedStatusAsync(
                root, $"/rest/services/hosted/{service}/FeatureServer?f=json"));
        }
        finally
        {
            // Whatever failed above, leave no shell behind: this suite must not create
            // the state it is testing for and walk away, which is how D-157 was found.
            await AdminAsync(root, HttpMethod.Delete, $"/admin/layers/{layer}");
            await AdminAsync(root, HttpMethod.Delete, $"/admin/featureservices/{service}?folder=hosted");
        }
    }

    private async Task<string?> DatastoreIdAsync(string root)
    {
        (int status, string body) = await AdminAsync(root, HttpMethod.Get, "/admin/datasources");

        if (status != 200)
        {
            return null;
        }

        foreach (JsonElement source in JsonDocument.Parse(body)
            .RootElement.GetProperty("dataSources").EnumerateArray())
        {
            if (source.TryGetProperty("name", out JsonElement name)
                && string.Equals(name.GetString(), "datastore", StringComparison.Ordinal))
            {
                return source.GetProperty("id").GetString();
            }
        }

        return null;
    }

    private async Task<(int Status, string Body)> AdminAsync(
        string root, HttpMethod method, string path, string? json = null)
    {
        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));

        await AuthenticateAsync(request, root);

        if (json is not null)
        {
            request.Content = new StringContent(
                json, System.Text.Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> AuthenticatedStatusAsync(string root, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}{path}"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (int)response.StatusCode;
    }
}
