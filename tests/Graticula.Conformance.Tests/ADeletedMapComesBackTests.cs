using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>A deleted web map waits in the recycle bin and comes back with its id and its tags — ADR-203.</summary>
public sealed class ADeletedMapComesBackTests : ArcGisClient
{
    private static readonly string[] Tags = ["zz_recycled"];

    [Fact]
    public async Task A_deleted_map_is_listed_and_restored_with_its_id()
    {
        await RequireServerAsync();
        string? map = null;

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/content/webmaps", JsonSerializer.Serialize(new
            {
                title = "zz recycled map", sharing = "private",
                document = new { operationalLayers = Array.Empty<object>(), baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" }, version = "2.31" },
            }));
            Assert.True(made == 201, $"making the map: {made} {madeBody}");
            map = JsonDocument.Parse(madeBody).RootElement.GetProperty("id").GetString()!;
            await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}/tags", JsonSerializer.Serialize(new { tags = Tags }));

            (int deleted, string deletedBody) = await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}");
            Assert.True(deleted == 200, $"deleting: {deleted} {deletedBody}");
            string entry = JsonDocument.Parse(deletedBody).RootElement.GetProperty("recycled").GetString()!;

            (int gone, _) = await AdminAsync(HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.Equal(404, gone);

            (_, string listed) = await AdminAsync(HttpMethod.Get, "/admin/recycle-bin");
            Assert.Contains(JsonDocument.Parse(listed).RootElement.GetProperty("entries").EnumerateArray(),
                e => e.GetProperty("id").GetString() == entry && e.GetProperty("kind").GetString() == "web map");

            (int restored, string restoredBody) = await AdminAsync(HttpMethod.Post, $"/admin/recycle-bin/{entry}/restore");
            Assert.True(restored == 200, $"restoring: {restored} {restoredBody}");

            (int back, string backBody) = await AdminAsync(HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.Equal(200, back);
            Assert.Contains("zz_recycled", backBody, StringComparison.Ordinal);
        }
        finally
        {
            if (map is not null)
            {
                (_, string again) = await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}");

                if (JsonDocument.Parse(again).RootElement.TryGetProperty("recycled", out JsonElement second))
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{second.GetString()}");
                }
            }
        }
    }
}
