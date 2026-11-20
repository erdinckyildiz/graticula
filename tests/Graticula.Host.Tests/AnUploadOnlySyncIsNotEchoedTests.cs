using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-401, against a real server over PostgreSQL: a layer synchronised with <c>syncDirection=upload</c> keeps its generation, so
/// the next download's window holds what the replica uploaded. That download leaves out what the server has not changed since
/// the upload — rows and attachments — and still sends what it has.
/// </summary>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class AnUploadOnlySyncIsNotEchoedTests(OneStartedNode started)
{
    private static string Braced(Guid id) => "{" + id.ToString("D").ToUpperInvariant() + "}";

    [Fact]
    public async Task The_next_download_leaves_out_the_replicas_own_uploads_and_sends_what_the_server_changed_after_them()
    {
        TwoNodeSite.Node node = started.Node;
        ReplicaClient client = new(node, await node.SignInAsync());
        string name = "zz_sync_echo_" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetTempPath(), started.Site.Schema, "files-" + name);
        Directory.CreateDirectory(directory);

        (ReplicaClient.Hosted layer, long first, Guid _) = await client.NewLayerAsync(name);

        try
        {
            (string replicaId, long generation) = await client.CreateReplicaAsync(layer, name + "_r");
            await Task.Delay(100);

            Guid kept = Guid.NewGuid();
            Guid changed = Guid.NewGuid();
            Guid photo = Guid.NewGuid();
            string data = Convert.ToBase64String(ReplicaClient.Png);

            // ---- upload only: two features and a photograph, made on the device ----
            string edits = $$"""
                [{ "id": 0,
                   "features": { "adds": [
                     { "attributes": { "globalid": "{{Braced(kept)}}", "label": "device kept" }, "geometry": { "x": 3000, "y": 4000, "spatialReference": { "wkid": 3857 } } },
                     { "attributes": { "globalid": "{{Braced(changed)}}", "label": "device changed" }, "geometry": { "x": 3100, "y": 4100, "spatialReference": { "wkid": 3857 } } } ] },
                   "attachments": { "adds": [
                     { "globalid": "{{Braced(photo)}}", "parentGlobalid": "{{Braced(kept)}}", "contentType": "image/png", "name": "device.png", "data": "{{data}}" } ] } }]
                """;
            JsonElement uploaded = await client.SendAsync(HttpMethod.Post, $"{layer.Service}/synchronizeReplica", ReplicaClient.Form(
                ("replicaID", replicaId),
                ("syncDirection", "upload"),
                ("dataFormat", "json"),
                ("edits", edits),
                ("syncLayers", $"[{{\"id\":0,\"serverGen\":{generation},\"syncDirection\":\"upload\"}}]"),
                ("returnAttachments", "true"),
                ("async", "false")));
            Assert.Equal("Completed", uploaded.GetProperty("status").GetString());
            Assert.Equal(generation, uploaded.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64());

            // ---- the server then edits one of them, and one of its own ----
            await Task.Delay(100);
            JsonElement rows = await client.SendAsync(
                HttpMethod.Get, $"{layer.Feature}/query?where=label%3D%27device%20changed%27&outFields=objectid&returnGeometry=false&f=json");
            long changedId = rows.GetProperty("features")[0].GetProperty("attributes").GetProperty("objectid").GetInt64();
            await client.SendAsync(HttpMethod.Post, $"{layer.Feature}/updateFeatures", ReplicaClient.Form(
                ("features", $"[{{\"attributes\":{{\"objectid\":{changedId},\"label\":\"server changed it\"}}}}]")));
            await client.SendAsync(HttpMethod.Post, $"{layer.Feature}/updateFeatures", ReplicaClient.Form(
                ("features", $"[{{\"attributes\":{{\"objectid\":{first},\"label\":\"server's own\"}}}}]")));

            // ---- the download ----
            JsonElement down = await client.DownloadSyncAsync(layer, replicaId, generation, format: "json");
            string path = await client.FetchAsync(down, directory, "changes.json");
            JsonElement layerEdits = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("edits")[0];
            JsonElement features = layerEdits.GetProperty("features");
            string[] sent = [.. features.GetProperty("adds").EnumerateArray().Concat(features.GetProperty("updates").EnumerateArray())
                .Select(f => f.GetProperty("attributes").GetProperty("globalid").GetString()!.ToUpperInvariant())];

            Assert.DoesNotContain(Braced(kept), sent);
            Assert.Contains(Braced(changed), sent);
            Assert.Equal(2, sent.Length);
            Assert.Contains(
                features.GetProperty("adds").EnumerateArray().Concat(features.GetProperty("updates").EnumerateArray()),
                f => f.GetProperty("attributes").GetProperty("label").GetString() == "server's own");

            if (layerEdits.TryGetProperty("attachments", out JsonElement attachments))
            {
                string[] photos = [.. attachments.GetProperty("adds").EnumerateArray().Concat(attachments.GetProperty("updates").EnumerateArray())
                    .Select(a => a.GetProperty("globalid").GetString()!.ToUpperInvariant())];
                Assert.DoesNotContain(Braced(photo), photos);
            }

            // ---- the generation moved, and what was remembered went with it ----
            long next = down.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64();
            Assert.True(next > generation, "The generation did not advance.");
            Assert.Equal(0L, await ReplicaClient.CountAsync(
                started.Site.Database,
                $"select count(*) from \"{started.Site.Schema}\".replica where id = '{replicaId.Trim('{', '}')}' and layers::text like '%\"own\"%'"));
        }
        finally
        {
            await client.DropAsync(layer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Remembering_keeps_the_newest_moment_for_a_GlobalID_and_forgets_the_oldest_past_the_limit()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        DateTimeOffset first = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        DateTimeOffset second = DateTimeOffset.FromUnixTimeMilliseconds(2000);

        var once = OfflineReplicaEndpoints.Remember(null, new System.Collections.Generic.HashSet<Guid> { a }, first);
        var twice = OfflineReplicaEndpoints.Remember(once, new System.Collections.Generic.HashSet<Guid> { a, b }, second);

        Assert.Equal(2, twice.Count);
        Assert.All(twice, o => Assert.Equal(2000, o.At));

        var many = OfflineReplicaEndpoints.Remember(
            null, Enumerable.Range(0, OfflineReplicaEndpoints.OwnLimit + 5).Select(_ => Guid.NewGuid()).ToHashSet(), first);
        Assert.Equal(OfflineReplicaEndpoints.OwnLimit, many.Count);
    }
}
