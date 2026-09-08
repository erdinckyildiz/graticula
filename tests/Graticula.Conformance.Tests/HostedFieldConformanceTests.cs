using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The datastore's schema is edited through this server, and only where nothing depends on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-058](../../docs/adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md),
/// owner instruction 2026-09-08:</b> <i>"datastore üzerindeki fieldlerin değişimi kendi
/// sorumluluğunda, yani db'ye bağlanıp kimse değiştirmeyecek. Ekrandan yapılacak tüm
/// değişiklikler."</i> Until this existed a hosted layer's shape was frozen at creation and the
/// only repair was to drop it and import again.
/// </para>
/// <para>
/// <b>The delete is the half worth testing hard.</b> Adding a column is safe and reversible;
/// dropping one is neither, and its whole safety is a hand-maintained list of what reads a column
/// by name. Each refusal below is one entry in that list, asserted rather than assumed.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class HostedFieldConformanceTests : ArcGisClient
{
    /// <summary>A column name nothing in a fixture will already have.</summary>
    private static string AField() => "zzz_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A field is added, appears at once, and is dropped again.
    /// </summary>
    /// <remarks>
    /// <b>At once, which is §5g and not the thirty-second TTL.</b> For a registered table the
    /// TTL is the only bound available because nothing tells us; for a hosted one this server
    /// made the change, so keeping a stale field list afterwards would be a staleness it chose.
    /// A test that slept thirty seconds would pass against a server that had forgotten to drop
    /// the memory at all.
    /// </remarks>
    [Fact]
    public async Task A_field_is_added_and_is_visible_without_waiting()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = HostedLayerName();

        Assert.False(
            string.IsNullOrWhiteSpace(layer),
            "GRATICULA_TEST_QUERYABLE names no hosted layer, so there is nothing to alter.");

        string field = AField();

        (HttpStatusCode added, string said) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/{layer}/fields",
            token!,
            JsonSerializer.Serialize(new { name = field, type = "text" }));

        Assert.True(
            added == HttpStatusCode.Created,
            $"Adding a field answered {(int)added}: {said}");

        try
        {
            Assert.Equal(
                field,
                JsonDocument.Parse(said).RootElement.GetProperty("field").GetString());

            Assert.Contains(field, await FieldsAsync(root, token!, layer), StringComparer.Ordinal);
        }
        finally
        {
            (HttpStatusCode dropped, string gone) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/hosted/{layer}/fields/{field}", token!, null);

            Assert.True(
                dropped == HttpStatusCode.OK,
                $"Dropping the field answered {(int)dropped}: {gone}");
        }

        Assert.DoesNotContain(
            field, await FieldsAsync(root, token!, layer), StringComparer.Ordinal);
    }

    /// <summary>
    /// The columns a layer is addressed by are refused, and named as what they are.
    /// </summary>
    /// <remarks>
    /// <b>The geometry column is the one this was got wrong on first.</b> It is not in the field
    /// list a client reads — no client asks for it as a field — so the first version fell through
    /// to <i>has no field called 'geom'</i>, which tells an operator their geometry is already
    /// gone. Not in the list and not in the table are different facts and this asserts the
    /// difference.
    /// </remarks>
    [Fact]
    public async Task The_columns_a_layer_is_addressed_by_cannot_be_dropped()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = HostedLayerName();

        Assert.False(string.IsNullOrWhiteSpace(layer), "No hosted layer to test against.");

        (HttpStatusCode geometry, string aboutGeometry) = await RequestAsync(
            HttpMethod.Delete, $"{root}/admin/hosted/{layer}/fields/geom", token!, null);

        Assert.True(
            geometry == HttpStatusCode.Conflict,
            $"Dropping the geometry column answered {(int)geometry} rather than 409: "
            + aboutGeometry);

        Assert.Contains("geometry", aboutGeometry, StringComparison.OrdinalIgnoreCase);

        (HttpStatusCode identity, string aboutIdentity) = await RequestAsync(
            HttpMethod.Delete, $"{root}/admin/hosted/{layer}/fields/objectid", token!, null);

        Assert.True(
            identity == HttpStatusCode.Conflict,
            $"Dropping the object id answered {(int)identity} rather than 409: {aboutIdentity}");

        // <b>And a name that really is absent is still a 404.</b> Answering 409 for everything
        // would make the refusal above meaningless — the assertion is that this server tells the
        // two apart, not that it refuses a lot.
        (HttpStatusCode absent, _) = await RequestAsync(
            HttpMethod.Delete,
            $"{root}/admin/hosted/{layer}/fields/zzz_no_such_column", token!, null);

        Assert.Equal(HttpStatusCode.NotFound, absent);
    }

    /// <summary>
    /// A table this server did not create is refused, whatever source it is served from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-058 §5h, and this test found the guard was the wrong one.</b> It was written to
    /// exercise the registered branch and borrowed a table the way every other test here does —
    /// through the datastore source. That made the layer <i>hosted</i>, so the endpoint's
    /// <c>IsHosted</c> check passed and <c>PostGisImporter</c>'s own guard threw: the caller got
    /// a <b>500</b> for a state that should have been a sentence.
    /// </para>
    /// <para>
    /// <b>Hosted says the source is the datastore. It says nothing about the schema.</b> A
    /// datastore source can serve any schema of that database, and only what this server created
    /// is its to alter. The endpoint now checks both, and this is the assertion that keeps them
    /// checked.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_table_this_server_did_not_create_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (Guid source, string schema, string table, string geometry, string identity, int srid) =
            await ATableAsync(root, token!);

        string name = "ZZZRegistered" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode made, string said) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/publish",
            token!,
            JsonSerializer.Serialize(new
            {
                name,
                folder = "hosted",
                sharing = "private",
                nodes = new object[]
                {
                    new { layer = CompositionLayer($"only{name}", source, schema, table, geometry, identity, srid) },
                },
            }));

        Assert.True(made == HttpStatusCode.Created, $"The publish answered {(int)made}: {said}");

        try
        {
            (HttpStatusCode refused, string why) = await RequestAsync(
                HttpMethod.Post,
                $"{root}/admin/hosted/only{name}/fields",
                token!,
                JsonSerializer.Serialize(new { name = AField(), type = "text" }));

            Assert.True(
                refused == HttpStatusCode.Conflict,
                $"Adding a field to a table this server did not create answered {(int)refused} "
                + $"rather than refusing in a sentence: {why}");

            // <b>The schema is named, because that is the fact that decides it.</b> A refusal
            // that said only *this layer cannot be altered* would leave an operator with no way
            // to tell which of their layers can.
            Assert.Contains(schema, why, StringComparison.Ordinal);
            Assert.Contains("did not create", why, StringComparison.Ordinal);
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/layers/only{name}", token!, null);

            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/featureservices/{name}?folder=hosted", token!, null);
        }
    }

    /// <summary>The hosted layer this fixture names, without its folder.</summary>
    private static string HostedLayerName()
    {
        string? queryable = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE");

        return queryable is { Length: > 0 }
            ? queryable.Split('/').Last()
            : string.Empty;
    }

    /// <summary>The field names a layer's own document reports.</summary>
    private async Task<string[]> FieldsAsync(string root, string token, string layer)
    {
        (HttpStatusCode read, string document) = await RequestAsync(
            HttpMethod.Get,
            $"{root}/rest/services/hosted/{layer}/FeatureServer/0?f=json", token, null);

        Assert.Equal(HttpStatusCode.OK, read);

        return
        [
            .. JsonDocument.Parse(document).RootElement
                .GetProperty("fields")
                .EnumerateArray()
                .Select(f => f.GetProperty("name").GetString() ?? string.Empty),
        ];
    }
}
