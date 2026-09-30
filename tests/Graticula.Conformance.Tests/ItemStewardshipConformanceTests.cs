using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An item's owner chooses the edits it offers inside the administrator's ceiling, and may protect it from
/// deletion; the API enforces both — ADR-102 conditions 1 and 2, owner decision 2026-10-01.
/// </summary>
/// <remarks>
/// <b>Asserted on what a client sees</b>, not on the stored columns: the layer document's capabilities and the
/// answer to an edit, and the answer to a delete.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ItemStewardshipConformanceTests : ArcGisClient
{
    private static readonly string[] UpdateOnly = ["Update"];
    private static readonly string[] Unknown = ["Query", "Teleport"];

    private static string Service() => Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? "hosted/ci_editable";

    [Fact]
    public async Task What_the_owner_withholds_is_neither_advertised_nor_accepted()
    {
        string service = Service();
        string bare = service[(service.LastIndexOf('/') + 1)..];
        string editing = $"/admin/services/{Uri.EscapeDataString(bare)}/editing{FolderQuery(service)}";

        try
        {
            (int status, string body) = await AdminAsync(HttpMethod.Put, editing,
                JsonSerializer.Serialize(new { operations = UpdateOnly }));

            Assert.True(status == 200, $"Offering Update alone answered {status}: {body}");

            JsonElement layer = await GetJsonAsync($"/rest/services/{service}/FeatureServer/0?f=json");
            string caps = layer.GetProperty("capabilities").GetString() ?? "";

            Assert.Equal("Query,Update", caps);

            (status, body) = await AdminAsync(HttpMethod.Post,
                $"/rest/services/{service}/FeatureServer/0/addFeatures?f=json&features="
                + Uri.EscapeDataString("[{\"attributes\":{},\"geometry\":{\"x\":29,\"y\":41}}]"));

            Assert.Contains("403", body, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, editing, JsonSerializer.Serialize(new { operations = (string[]?)null }));
        }

        JsonElement back = await GetJsonAsync($"/rest/services/{service}/FeatureServer/0?f=json");
        Assert.Contains("Create", back.GetProperty("capabilities").GetString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_cannot_be_withheld_and_an_unknown_operation_is_refused()
    {
        string service = Service();
        string bare = service[(service.LastIndexOf('/') + 1)..];

        (int status, string body) = await AdminAsync(HttpMethod.Put,
            $"/admin/services/{Uri.EscapeDataString(bare)}/editing{FolderQuery(service)}",
            JsonSerializer.Serialize(new { operations = Unknown }));

        Assert.True(status == 400, $"An owner's operations of Query and Teleport answered {status}: {body}");
    }

    [Fact]
    public async Task A_protected_item_is_not_deleted_until_the_protection_is_turned_off()
    {
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE") ?? "hosted/ci_parcels";
        string bare = service[(service.LastIndexOf('/') + 1)..];
        string protection = $"/admin/services/{Uri.EscapeDataString(bare)}/protection{FolderQuery(service)}";

        try
        {
            (int status, string body) = await AdminAsync(HttpMethod.Put, protection,
                JsonSerializer.Serialize(new { @protected = true }));

            Assert.True(status == 200, $"Protecting the service answered {status}: {body}");

            (status, body) = await AdminAsync(HttpMethod.Delete,
                $"/admin/featureservices/{Uri.EscapeDataString(bare)}{FolderQuery(service)}");

            Assert.True(status == 409, $"Deleting a protected service answered {status}: {body}");
            Assert.Contains("protected", body, StringComparison.OrdinalIgnoreCase);

            (status, body) = await AdminAsync(HttpMethod.Get,
                $"/admin/services/{Uri.EscapeDataString(bare)}/stewardship{FolderQuery(service)}");

            Assert.Equal(200, status);
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("deleteProtected").GetBoolean());
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, protection, JsonSerializer.Serialize(new { @protected = false }));
        }
    }
}
