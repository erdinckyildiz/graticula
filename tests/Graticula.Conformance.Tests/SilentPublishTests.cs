using System;
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
/// A publish that writes nothing does not answer as though it wrote something.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-147](../../docs/architecture-debt.md), found while repairing D-104.</b> The statement
/// that publishes a layer begins by reading the data source, and every insert in it selects from
/// that read. A source id matching no row made the whole statement a no-op — no folder, no
/// service, no layer — and the method returned the address it had computed in memory before
/// asking. `POST /admin/layers` answered **201 Created** with an id, a service name and a URL,
/// and the store had exactly as many layers afterwards as before.
/// </para>
/// <para>
/// <b>The count is asserted, not only the status.</b> A 404 that still wrote a row and a 201 that
/// wrote none are the same defect wearing different numbers, and only counting tells them apart.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class SilentPublishTests : ArcGisClient
{
    /// <summary>Publishing from a source that does not exist is refused, and writes nothing.</summary>
    [Fact]
    public async Task A_publish_from_a_source_that_does_not_exist_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs an administrator's token");

        int before = await LayerCountAsync(root, token!);

        (HttpStatusCode status, string body) = await AdminAsync(
            root, token!, HttpMethod.Post, "/admin/layers",
            JsonSerializer.Serialize(new
            {
                name = "zz_d147_probe",
                dataSourceId = Guid.Empty,
                schemaName = "public",
                tableName = "zz_d147_no_such_table",
                geometryColumn = "geom",
                identityColumn = "id",
                srid = 4326,
                geometryType = "Point",
                sharing = "private",
                serviceName = "zz_d147_probe",
            }));

        Assert.True(
            status == HttpStatusCode.NotFound,
            $"publishing from a data source that does not exist answered {(int)status}: {body}. "
            + "It writes nothing, so it has to say so.");

        Assert.Contains("no data source", body, StringComparison.OrdinalIgnoreCase);

        // The half a status code cannot carry: nothing arrived.
        Assert.Equal(before, await LayerCountAsync(root, token!));

        // And no service was left behind either. The statement creates the service and the layer
        // together or not at all, and *not at all* is what this asserts.
        Assert.DoesNotContain(
            "zz_d147_probe",
            string.Join(' ', await EveryServiceNameAsync()),
            StringComparison.Ordinal);
    }

    private async Task<int> LayerCountAsync(string root, string token) =>
        JsonDocument.Parse((await AdminAsync(root, token, HttpMethod.Get, "/admin/layers", null)).Body)
            .RootElement.GetProperty("layers").GetArrayLength();

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
