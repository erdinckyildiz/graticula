using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A whole service is published in one act, or none of it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-057](../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5a and §5h.</b>
/// Until <c>POST /admin/publish</c> existed, building a service meant three calls in the order
/// the API wanted: create an empty container, add group layers, then publish layers naming a
/// group by a numeric index that a design review could find nowhere on that surface. The owner's
/// rule removes the first of those — *katmansız servis yaratılamaz* — and with it the sequence.
/// </para>
/// <para>
/// <b>What is worth pinning is the tree, not the call.</b> A composition is a list an operator
/// dragged into an order; what a client receives is ArcGIS's <c>subLayerIds</c> graph. Those are
/// different shapes, the translation happens in one place, and this asserts the shape that
/// leaves the server rather than the one that arrived.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class PublishCompositionConformanceTests : ArcGisClient
{
    /// <summary>A name nothing else in the fixture will collide with.</summary>
    private static string AName() =>
        "ZZZComposed" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// One request makes the service, its group and its layers, and the tree comes back right.
    /// </summary>
    [Fact]
    public async Task A_composition_becomes_one_service_with_its_group_tree()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (Guid source, string schema, string table, string geometry, string identity, int srid) =
            await ATableAsync(root, token!);

        (_, string schema2, string table2, string geometry2, string identity2, int srid2) =
            await ATableAsync(root, token!, skip: 1);

        string name = AName();

        string body = JsonSerializer.Serialize(new
        {
            name,
            folder = "hosted",
            description = "published in one act",
            sharing = "private",

            // <b>Deliberately not the table's.</b> §5c: the service names the reference it is
            // served in, and a composition is where that choice is made — so this asserts the
            // two decisions together rather than trusting that they compose.
            srid = srid == 4326 ? 3857 : 4326,
            nodes = new object[]
            {
                new { layer = Layer($"top{name}", source, schema, table, geometry, identity, srid) },
                new
                {
                    group = "Reference",
                    layers = new[]
                    {
                        Layer($"inside{name}", source, schema2, table2, geometry2, identity2, srid2),
                    },
                },
            },
        });

        (HttpStatusCode status, string said) = await SendAsync(
            HttpMethod.Post, $"{root}/admin/publish", token!, body);

        Assert.True(
            status == HttpStatusCode.Created,
            $"Publishing a composition answered {(int)status}: {said}");

        try
        {
            JsonElement made = JsonDocument.Parse(said).RootElement;

            Assert.Equal(2, made.GetProperty("layers").GetArrayLength());
            Assert.Equal(1, made.GetProperty("groups").GetArrayLength());

            // <b>One index space, and this is where a collision would show.</b> A group and a
            // layer both occupy a numbered slot — `subLayerIds` addresses one list — and the
            // two live in different tables, so nothing in the schema stops them sharing a
            // number. Numbering is decided over the whole tree before anything is inserted;
            // this is what says so.
            int[] taken =
            [
                .. made.GetProperty("layers").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()),
                .. made.GetProperty("groups").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()),
            ];

            Assert.Equal(taken.Length, taken.Distinct().Count());
            Assert.Equal([0, 1, 2], [.. taken.OrderBy(x => x)]);

            // <b>And the document a client reads is ArcGIS's tree.</b>
            (HttpStatusCode read, string document) = await SendAsync(
                HttpMethod.Get,
                $"{root}/rest/services/hosted/{name}/FeatureServer?f=json", token!, null);

            Assert.Equal(HttpStatusCode.OK, read);

            JsonElement service = JsonDocument.Parse(document).RootElement;
            JsonElement[] layers = [.. service.GetProperty("layers").EnumerateArray()];

            Assert.Equal(3, layers.Length);

            JsonElement group = layers.Single(l =>
                string.Equals(l.GetProperty("type").GetString(), "Group Layer", StringComparison.Ordinal));

            Assert.Equal("Reference", group.GetProperty("name").GetString());

            int[] children =
                [.. group.GetProperty("subLayerIds").EnumerateArray().Select(x => x.GetInt32())];

            Assert.Single(children);

            JsonElement child = layers.Single(l => l.GetProperty("id").GetInt32() == children[0]);

            Assert.Equal($"inside{name}", child.GetProperty("name").GetString());
            Assert.Equal(group.GetProperty("id").GetInt32(), child.GetProperty("parentLayerId").GetInt32());

            // <b>The service is served in the reference the composition named</b>, not the
            // table's — the two decisions working together, which is the thing a composition
            // is for.
            Assert.Equal(
                srid == 4326 ? 3857 : 4326,
                service.GetProperty("fullExtent").GetProperty("spatialReference")
                    .GetProperty("latestWkid").GetInt32());
        }
        finally
        {
            await TearDownAsync(root, token!, name, [$"top{name}", $"inside{name}"], [1]);
        }
    }

    /// <summary>
    /// A composition with one bad entry writes none of itself, and names the entry.
    /// </summary>
    /// <remarks>
    /// <b>A half-published service is the residue §5h refuses to create on purpose.</b> It must
    /// not be created by accident either, which is what the transaction and the
    /// validate-everything-first are between them for. The entry number is in the message
    /// because *one of your layers is wrong* on a composition of twenty is not a repair anybody
    /// can start.
    /// </remarks>
    [Fact]
    public async Task A_composition_with_a_bad_entry_leaves_nothing_behind()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (Guid source, string schema, string table, string geometry, string identity, int srid) =
            await ATableAsync(root, token!);

        (_, string schema2, string table2, string geometry2, string identity2, int srid2) =
            await ATableAsync(root, token!, skip: 1);

        string name = AName();

        string body = JsonSerializer.Serialize(new
        {
            name,
            folder = "hosted",
            sharing = "private",
            nodes = new object[]
            {
                new { layer = Layer($"fine{name}", source, schema, table, geometry, identity, srid) },
                new { layer = Layer($"alsofine{name}", source, schema2, table2, geometry2, identity2, srid2) },

                // No table: refused by the reader every publish already goes through.
                new
                {
                    layer = new
                    {
                        name = "broken",
                        dataSourceId = source,
                        schemaName = schema,
                        tableName = "",
                        geometryColumn = geometry,
                        identityColumn = identity,
                        objectIdColumn = identity,
                        srid,
                        geometryType = "POLYGON",
                    },
                },
            },
        });

        (HttpStatusCode status, string said) = await SendAsync(
            HttpMethod.Post, $"{root}/admin/publish", token!, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);

        Assert.Contains(
            "Entry 3", said, StringComparison.Ordinal);

        // <b>And nothing of it exists.</b> The two good layers before the bad one are the ones
        // a non-transactional publish would have left behind.
        (HttpStatusCode looked, _) = await SendAsync(
            HttpMethod.Get,
            $"{root}/rest/services/hosted/{name}/FeatureServer?f=json", token!, null);

        Assert.True(
            looked is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"'{name}' answers {(int)looked} after a refused composition, so something of it "
            + "was written — which is the residue this refusal exists to prevent.");
    }

    /// <summary>
    /// A name already taken in that folder is refused, and the first service is untouched.
    /// </summary>
    [Fact]
    public async Task A_name_already_taken_in_that_folder_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (Guid source, string schema, string table, string geometry, string identity, int srid) =
            await ATableAsync(root, token!, skip: 2);

        // <b>A different table, so only the name collides.</b> Publishing the same composition
        // twice trips `layer_table_unique` first — one table is one layer, globally — and would
        // have tested that rule while claiming to test this one. Found by writing it the lazy
        // way and reading the refusal.
        (_, string schema2, string table2, string geometry2, string identity2, int srid2) =
            await ATableAsync(root, token!, skip: 3);

        string name = AName();

        string first = JsonSerializer.Serialize(new
        {
            name,
            folder = "hosted",
            sharing = "private",
            nodes = new object[]
            {
                new { layer = Layer($"only{name}", source, schema, table, geometry, identity, srid) },
            },
        });

        string second = JsonSerializer.Serialize(new
        {
            name,
            folder = "hosted",
            sharing = "private",
            nodes = new object[]
            {
                new { layer = Layer($"other{name}", source, schema2, table2, geometry2, identity2, srid2) },
            },
        });

        (HttpStatusCode made, string said) = await SendAsync(
            HttpMethod.Post, $"{root}/admin/publish", token!, first);

        Assert.True(made == HttpStatusCode.Created, $"The first publish answered {(int)made}: {said}");

        try
        {
            (HttpStatusCode again, string refused) = await SendAsync(
                HttpMethod.Post, $"{root}/admin/publish", token!, second);

            Assert.Equal(HttpStatusCode.Conflict, again);

            Assert.Contains("unique inside a folder", refused, StringComparison.OrdinalIgnoreCase);

            // <b>And the one that is there still is.</b> A refusal that damages what it refused
            // to replace is worse than one that succeeds.
            (HttpStatusCode read, string document) = await SendAsync(
                HttpMethod.Get,
                $"{root}/rest/services/hosted/{name}/FeatureServer?f=json", token!, null);

            Assert.Equal(HttpStatusCode.OK, read);

            Assert.Equal(
                1,
                JsonDocument.Parse(document).RootElement.GetProperty("layers").GetArrayLength());
        }
        finally
        {
            await TearDownAsync(root, token!, name, [$"only{name}"], []);
        }
    }

    /// <summary>
    /// Takes a published composition apart without touching the tables under it.
    /// </summary>
    /// <remarks>
    /// <b>Three steps, and `drop=true` is not one of them.</b> Deleting a service refuses while
    /// it holds anything, and the shortcut it offers — <c>drop=true</c> — *unpublishes the
    /// layers and drops the tables of the hosted ones*. These compositions point at the
    /// fixture's own tables, so that shortcut would delete the data every other test in this
    /// suite reads. Unpublish removes a registration and leaves the table alone; that is the
    /// one that is safe here.
    /// </remarks>
    /// <param name="root">The server.</param>
    /// <param name="token">The credential.</param>
    /// <param name="name">The service.</param>
    /// <param name="layers">The layers it holds, by name.</param>
    /// <param name="groups">The indices of its group layers.</param>
    /// <returns>The task.</returns>
    private async Task TearDownAsync(
        string root, string token, string name, string[] layers, int[] groups)
    {
        foreach (string layer in layers)
        {
            await SendAsync(HttpMethod.Delete, $"{root}/admin/layers/{layer}", token, null);
        }

        foreach (int index in groups)
        {
            await SendAsync(
                HttpMethod.Delete,
                $"{root}/admin/services/{name}/groups/{index}?folder=hosted", token, null);
        }

        await SendAsync(
            HttpMethod.Delete,
            $"{root}/admin/featureservices/{name}?folder=hosted", token, null);
    }

    /// <summary>One publishable table out of the datastore, whatever the fixture holds.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">The credential.</param>
    /// <returns>Enough to publish it.</returns>
    private async Task<(Guid Source, string Schema, string Table, string Geometry,
        string Identity, int Srid)> ATableAsync(string root, string token, int skip = 0)
    {
        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Get, $"{root}/admin/datasources", token, null);

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement datastore = JsonDocument.Parse(body).RootElement
            .GetProperty("dataSources").EnumerateArray()
            .First(d => string.Equals(
                d.GetProperty("name").GetString(), "datastore", StringComparison.Ordinal));

        Guid id = datastore.GetProperty("id").GetGuid();

        (HttpStatusCode probed, string what) = await SendAsync(
            HttpMethod.Get, $"{root}/admin/datasources/{id}/capability", token, null);

        Assert.Equal(HttpStatusCode.OK, probed);

        // <b>Free tables only, and CI is where that turned out to matter.</b>
        // `layer_table_unique` is on (source, schema, table, geometry) and is <b>global</b>: a
        // table already served by a layer cannot be served by a second one, here or in another
        // service. Locally the datastore holds ninety-one tables and almost none are published,
        // so taking the first worked; CI's seed publishes what it makes, so the first table is
        // always taken and both of these tests failed there and nowhere else.
        //
        // <b>Skipped rather than reused for the same reason</b> — a composition naming one
        // table twice is refused by the schema, which is the answer to ADR-057's open *two
        // layers, one table* and stricter than that question assumed (§5i).
        (HttpStatusCode listed, string served) = await SendAsync(
            HttpMethod.Get, $"{root}/admin/layers", token, null);

        Assert.Equal(HttpStatusCode.OK, listed);

        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement layer in JsonDocument.Parse(served).RootElement
            .GetProperty("layers").EnumerateArray())
        {
            if (layer.TryGetProperty("table", out JsonElement qualified)
                && qualified.GetString() is { Length: > 0 } where)
            {
                taken.Add(where);
            }
        }

        JsonElement[] publishable =
            [.. JsonDocument.Parse(what).RootElement
                .GetProperty("tables").EnumerateArray()
                .Where(t => t.TryGetProperty("objectIdColumn", out JsonElement oid)
                    && oid.ValueKind == JsonValueKind.String)
                .Where(t => !taken.Contains(
                    $"{t.GetProperty("schemaName").GetString()}."
                    + t.GetProperty("tableName").GetString()))];

        Assert.True(
            publishable.Length > skip,
            $"The datastore offers {publishable.Length} table(s) that are publishable and not "
            + $"already served, and this test needs at least {skip + 1}. A table is one layer "
            + "on this server, so a test cannot borrow one that is in use.");

        JsonElement table = publishable[skip];

        return (
            id,
            table.GetProperty("schemaName").GetString()!,
            table.GetProperty("tableName").GetString()!,
            table.GetProperty("geometryColumn").GetString()!,
            table.GetProperty("objectIdColumn").GetString()!,
            table.GetProperty("srid").GetInt32());
    }

    /// <summary>One layer of a composition, as the endpoint takes it.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="source">Which registered source it reads from.</param>
    /// <param name="schema">Its schema.</param>
    /// <param name="table">Its table.</param>
    /// <param name="geometry">Its geometry column.</param>
    /// <param name="identity">Its identity column.</param>
    /// <param name="srid">The reference its table is stored in.</param>
    /// <returns>The layer.</returns>
    private static object Layer(
        string name, Guid source, string schema, string table,
        string geometry, string identity, int srid) => new
        {
            name,
            dataSourceId = source,
            schemaName = schema,
            tableName = table,
            geometryColumn = geometry,
            identityColumn = identity,
            objectIdColumn = identity,
            srid,
            geometryType = "POLYGON",
        };

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string url, string token, string? json)
    {
        using HttpRequestMessage request = new(method, url);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
