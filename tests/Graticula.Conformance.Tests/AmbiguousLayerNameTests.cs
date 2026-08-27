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
/// Two layers of one name, and what the server does when asked about that name.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-109](../../docs/architecture-debt.md): a bare layer name is not unique, and nothing said
/// so.</b> `FindAsync` answers `order by s.name limit 1`, so the layer whose service sorts first
/// wins — silently — and an operator can open the settings of a layer they were not looking at.
/// One archive publishes fifty-five layers under names their owner chose, and an Esri estate has
/// `Segment_Boundary` in three of them.
/// </para>
/// <para>
/// <b>Staged rather than waited for.</b> The collision is made here, out of two tables this
/// server already publishes, under a name nothing else uses, in two services this test creates
/// and removes. A second registration writes no data and touches nothing that was already there
/// — and it has to be two tables rather than one, because `layer_table_unique` refuses the same
/// table twice while `layer_name_unique_in_service` allows the same name in two services. That
/// asymmetry is the defect.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AmbiguousLayerNameTests : ArcGisClient
{
    private const string Layer = "zz_d109_twice";

    private const string First = "zz_d109_a";

    private const string Second = "zz_d109_b";

    /// <summary>
    /// An ambiguous name is refused, and the refusal names the services.
    /// </summary>
    /// <remarks>
    /// <b>409 and not a guess.</b> The operator knows which one they meant and the server does
    /// not; the only useful thing it can do is say what the choices are and how to express one.
    /// Both endpoints that act on a named layer are asked, because the dangerous one is the
    /// delete and the quiet one is the refresh.
    /// </remarks>
    [Fact]
    public async Task Two_layers_of_one_name_make_the_name_a_refusal()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs an administrator's token");

        await CleanAsync(root, token!);

        try
        {
            (string, string, string, int)[] tables =
                await TwoFreeTablesAsync(root, token!);

            await PublishAsync(root, token!, First, tables[0]);
            await PublishAsync(root, token!, Second, tables[1]);

            (HttpStatusCode refresh, string why) = await AdminAsync(
                root, token!, HttpMethod.Post, $"/admin/layers/{Layer}/refresh", null);

            Assert.True(
                refresh == HttpStatusCode.Conflict,
                $"refreshing an ambiguous name answered {(int)refresh}: {why}");

            Assert.Contains(First, why, StringComparison.Ordinal);
            Assert.Contains(Second, why, StringComparison.Ordinal);
            Assert.Contains("?service=", why, StringComparison.Ordinal);

            (HttpStatusCode remove, string said) = await AdminAsync(
                root, token!, HttpMethod.Delete, $"/admin/layers/{Layer}", null);

            Assert.True(
                remove == HttpStatusCode.Conflict,
                $"unpublishing an ambiguous name answered {(int)remove}: {said}");

            // And nothing went: the delete used to be `where name = @name`, which removed both.
            Assert.Equal(2, await NamedAsync(root, token!));
        }
        finally
        {
            await CleanAsync(root, token!);
        }
    }

    /// <summary>
    /// Saying which service resolves it, and removes exactly one.
    /// </summary>
    /// <remarks>
    /// <b>The refusal has to have an answer, or it is a wall.</b> This is that answer, and it is
    /// also the assertion that the delete now takes one row: after removing one of the two, the
    /// other is still there.
    /// </remarks>
    [Fact]
    public async Task Naming_the_service_resolves_it_and_removes_one()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs an administrator's token");

        await CleanAsync(root, token!);

        try
        {
            (string, string, string, int)[] tables =
                await TwoFreeTablesAsync(root, token!);

            await PublishAsync(root, token!, First, tables[0]);
            await PublishAsync(root, token!, Second, tables[1]);

            Assert.Equal(2, await NamedAsync(root, token!));

            (HttpStatusCode removed, string said) = await AdminAsync(
                root, token!, HttpMethod.Delete,
                $"/admin/layers/{Layer}?service=hosted/{First}", null);

            Assert.True(
                removed == HttpStatusCode.OK,
                $"unpublishing the named one answered {(int)removed}: {said}");

            Assert.Equal(1, await NamedAsync(root, token!));
        }
        finally
        {
            await CleanAsync(root, token!);
        }
    }

    /// <summary>How many layers carry the fixture's name right now.</summary>
    private async Task<int> NamedAsync(string root, string token) =>
        JsonDocument.Parse((await AdminAsync(root, token, HttpMethod.Get, "/admin/layers", null)).Body)
            .RootElement.GetProperty("layers").EnumerateArray()
            .Count(l => string.Equals(l.GetProperty("name").GetString(), Layer, StringComparison.Ordinal));

    /// <summary>Registers a table this server already publishes, under the fixture's name.</summary>
    /// <remarks>
    /// <b>A second registration of an existing table, which writes no data.</b> The collision is
    /// made out of what is already there rather than by importing anything.
    /// </remarks>
    /// <summary>
    /// Two tables in the datastore that no layer is published from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>From the probe, not from the layer listing, and the first two versions of this got that
    /// wrong.</b> `layer_table_unique` covers (data source, schema, table, geometry column), so a
    /// table something already publishes cannot be registered again — the fixture needs two the
    /// server can see and is not using. The probe answers exactly that question and the layer
    /// listing answers the opposite one.
    /// </para>
    /// <para>
    /// <b>Two tables and not one, because the same table cannot be published twice while the same
    /// *name* can.</b> `layer_name_unique_in_service` is per service. That asymmetry is the defect
    /// this file is about, and it is also why the fixture is possible at all.
    /// </para>
    /// </remarks>
    private async Task<(string Schema, string Table, string Geometry, int Srid)[]> TwoFreeTablesAsync(
        string root, string token)
    {
        string source = JsonDocument.Parse(
                (await AdminAsync(root, token, HttpMethod.Get, "/admin/datasources", null)).Body)
            .RootElement.GetProperty("dataSources").EnumerateArray()
            .First(d => string.Equals(d.GetProperty("name").GetString(), "datastore",
                StringComparison.Ordinal))
            .GetProperty("id").GetString()!;

        HashSet<string> taken =
        [
            .. JsonDocument.Parse(
                    (await AdminAsync(root, token, HttpMethod.Get, "/admin/layers", null)).Body)
                .RootElement.GetProperty("layers").EnumerateArray()
                .Select(l => l.TryGetProperty("table", out JsonElement t) ? t.GetString() : null)
                .Where(t => t is { Length: > 0 })
                .Select(t => t!),
        ];

        (string Schema, string Table, string Geometry, int Srid)[] free =
        [
            .. JsonDocument.Parse(
                    (await AdminAsync(
                        root, token, HttpMethod.Get,
                        $"/admin/datasources/{source}/capability", null)).Body)
                .RootElement.GetProperty("tables").EnumerateArray()
                .Select(t => (
                    Schema: t.GetProperty("schemaName").GetString()!,
                    Table: t.GetProperty("tableName").GetString()!,
                    Geometry: t.GetProperty("geometryColumn").GetString()!,
                    Srid: t.GetProperty("srid").GetInt32()))
                .Where(t => !taken.Contains($"{t.Schema}.{t.Table}")),
        ];

        Assert.True(
            free.Length >= 2,
            $"the datastore has {free.Length} tables nothing publishes and the fixture needs two");

        Source = source;

        return [free[0], free[1]];
    }

    /// <summary>The datastore's id, read once by <see cref="TwoFreeTablesAsync"/>.</summary>
    private string Source { get; set; } = string.Empty;

    private async Task PublishAsync(
        string root,
        string token,
        string service,
        (string Schema, string Table, string Geometry, int Srid) table)
    {
        (HttpStatusCode status, string body) = await AdminAsync(
            root, token, HttpMethod.Post, "/admin/layers",
            JsonSerializer.Serialize(new
            {
                name = Layer,
                dataSourceId = Source,
                schemaName = table.Schema,
                tableName = table.Table,
                geometryColumn = table.Geometry,
                identityColumn = "id",
                srid = table.Srid,
                geometryType = "Point",
                sharing = "private",
                serviceName = service,
            }));

        Assert.True(
            status is HttpStatusCode.OK or HttpStatusCode.Created,
            $"publishing the fixture into '{service}' answered {(int)status}: {body}");
    }

    /// <summary>Removes both fixture services and anything in them.</summary>
    private async Task CleanAsync(string root, string token)
    {
        foreach (string service in new[] { First, Second })
        {
            await AdminAsync(
                root, token, HttpMethod.Delete,
                $"/admin/layers/{Layer}?service=hosted/{service}", null);

            await AdminAsync(
                root, token, HttpMethod.Delete,
                $"/admin/featureservices/{service}?folder=hosted&drop=true", null);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> AdminAsync(
        string root, string token, HttpMethod method, string path, string? body)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
