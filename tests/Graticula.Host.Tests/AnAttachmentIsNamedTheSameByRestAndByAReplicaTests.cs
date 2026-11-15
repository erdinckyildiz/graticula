using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Offline.Sqlite;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-422: <c>queryAttachments</c> and <c>…/{objectId}/attachments</c> answer the GlobalID a replica names each attachment by, and
/// the GlobalID of its feature — the kept one, else the one derived from the layer and the id.
/// </summary>
/// <remarks>Against a real server over PostgreSQL, like <see cref="ASyncOfAnAttachmentTests"/>.</remarks>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class AnAttachmentIsNamedTheSameByRestAndByAReplicaTests(OneStartedNode started)
{
    private static string Braced(Guid id) => "{" + id.ToString("D").ToUpperInvariant() + "}";

    [Fact]
    public async Task REST_and_a_replica_call_an_attachment_by_the_same_GlobalID_and_its_feature_by_the_same_parent()
    {
        TwoNodeSite.Node node = started.Node;
        ReplicaClient client = new(node, await node.SignInAsync());
        string name = "zz_sync_rest_" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetTempPath(), started.Site.Schema, "files-" + name);
        Directory.CreateDirectory(directory);

        (ReplicaClient.Hosted layer, long first, Guid parent) = await client.NewLayerAsync(name);

        try
        {
            int replaced = await client.AddAttachmentAsync(layer, first, "one.png", ReplicaClient.Png);
            (string replicaId, long generation) = await client.CreateReplicaAsync(layer, name + "_r");
            await Task.Delay(100);

            int added = await client.AddAttachmentAsync(layer, first, "two.png", ReplicaClient.Png);

            using (MultipartFormDataContent form = new())
            {
                form.Add(new StringContent(replaced.ToString(System.Globalization.CultureInfo.InvariantCulture)), "attachmentId");
                ByteArrayContent file = new(ReplicaClient.OtherPng);
                file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(file, "attachment", "one-again.png");
                form.Add(new StringContent("json"), "f");
                await client.SendAsync(HttpMethod.Post, $"{layer.Feature}/{first}/updateAttachment", form);
            }

            // What the replica is told.
            OfflineLayer shape = await client.ShapeAsync(layer);
            string file1 = await client.FetchAsync(await client.DownloadSyncAsync(layer, replicaId, generation), directory, "delta.geodatabase");
            OfflineLayerChanges changes = Assert.Single((await new SqliteOfflineDeltaReader().ReadDeltaAsync(file1, [shape], CancellationToken.None)).Layers);
            OfflineAttachment inserted = Assert.Single(changes.AttachmentInserts);
            OfflineAttachment updated = Assert.Single(changes.AttachmentUpdates);

            // What REST says, by the list …
            JsonElement listed = await client.SendAsync(HttpMethod.Get, $"{layer.Feature}/{first}/attachments?f=json");
            JsonElement[] infos = [.. listed.GetProperty("attachmentInfos").EnumerateArray()];

            Assert.Equal(2, infos.Length);
            Assert.Equal(Braced(inserted.GlobalId), infos.Single(i => i.GetProperty("id").GetInt32() == added).GetProperty("globalId").GetString());
            Assert.Equal(Braced(updated.GlobalId), infos.Single(i => i.GetProperty("id").GetInt32() == replaced).GetProperty("globalId").GetString());
            Assert.All(infos, i => Assert.Equal(Braced(parent), i.GetProperty("parentGlobalId").GetString()));

            // … and by queryAttachments, where the group names its feature's GlobalID.
            JsonElement queried = await client.SendAsync(HttpMethod.Get, $"{layer.Feature}/queryAttachments?objectIds={first}&f=json");
            JsonElement group = queried.GetProperty("attachmentGroups").EnumerateArray().Single();

            Assert.Equal(Braced(parent), group.GetProperty("parentGlobalId").GetString());
            Assert.Equal(
                Braced(inserted.GlobalId),
                group.GetProperty("attachmentInfos").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == added).GetProperty("globalId").GetString());

            // globalIds names features, and an unknown one names none.
            JsonElement byGlobal = await client.SendAsync(
                HttpMethod.Get, $"{layer.Feature}/queryAttachments?globalIds={Uri.EscapeDataString(Braced(parent))}&f=json");
            Assert.Equal(first, byGlobal.GetProperty("attachmentGroups").EnumerateArray().Single().GetProperty("parentObjectId").GetInt64());

            JsonElement unknown = await client.SendAsync(
                HttpMethod.Get, $"{layer.Feature}/queryAttachments?globalIds={Guid.NewGuid()}&f=json");
            Assert.Empty(unknown.GetProperty("attachmentGroups").EnumerateArray());

            // An attachment a device made keeps the GlobalID it gave it, and REST says that one.
            Guid clientGlobalId = Guid.NewGuid();
            string upload = Path.Combine(directory, "upload.geodatabase");
            await new SqliteMobileGeodatabaseWriter().WriteDeltaAsync(
                new OfflineDelta(
                    Guid.Parse(replicaId),
                    generation,
                    [new OfflineLayerChanges(shape, [], [], [], [new OfflineAttachment(900, parent, clientGlobalId, "device.png", "image/png", ReplicaClient.Png)], [], [])],
                    layer.Name),
                upload,
                CancellationToken.None);

            using MultipartFormDataContent body = new();
            ByteArrayContent bytes = new(await File.ReadAllBytesAsync(upload));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/x-sqlite3");
            body.Add(bytes, "file", "upload.geodatabase");
            body.Add(new StringContent("json"), "f");
            string uploadId = (await client.SendAsync(HttpMethod.Post, $"{layer.Service}/uploads/upload", body))
                .GetProperty("item").GetProperty("itemID").GetString()!;

            await client.SendAsync(HttpMethod.Post, $"{layer.Service}/synchronizeReplica", ReplicaClient.Form(
                ("replicaID", replicaId),
                ("syncDirection", "upload"),
                ("editsUploadID", uploadId),
                ("syncLayers", $"[{{\"id\":0,\"serverGen\":{generation},\"syncDirection\":\"upload\"}}]"),
                ("returnAttachments", "true"),
                ("async", "false")));

            JsonElement after = await client.SendAsync(HttpMethod.Get, $"{layer.Feature}/{first}/attachments?f=json");
            Assert.Contains(
                after.GetProperty("attachmentInfos").EnumerateArray(),
                i => i.GetProperty("name").GetString() == "device.png" && i.GetProperty("globalId").GetString() == Braced(clientGlobalId));
        }
        finally
        {
            await client.DropAsync(layer);
            Directory.Delete(directory, recursive: true);
        }
    }
}
