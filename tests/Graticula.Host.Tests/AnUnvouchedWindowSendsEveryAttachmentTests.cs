using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Offline.Sqlite;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-423, ADR-290 §11.4: when the change record cannot vouch for a window (it begins after the window did — a table made before
/// the record, a pruned record), the download sends every attachment of the replica's features as an update and names the layer
/// so the device's generation moves; the next window, which the record does vouch for, sends nothing.
/// </summary>
/// <remarks>
/// <b>What this does not show</b>: how Field Maps or Pro takes an update of a GlobalID it holds. The server's half is built and
/// tested; the device's is awaiting a measurement.
/// </remarks>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class AnUnvouchedWindowSendsEveryAttachmentTests(OneStartedNode started)
{
    [Fact]
    public async Task A_window_the_record_cannot_vouch_for_sends_every_attachment_as_an_update_and_the_next_one_sends_nothing()
    {
        TwoNodeSite.Node node = started.Node;
        ReplicaClient client = new(node, await node.SignInAsync());
        string name = "zz_sync_unv_" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetTempPath(), started.Site.Schema, "files-" + name);
        Directory.CreateDirectory(directory);

        (ReplicaClient.Hosted layer, long first, _) = await client.NewLayerAsync(name);

        try
        {
            JsonElement features = await client.SendAsync(
                HttpMethod.Get, $"{layer.Feature}/query?where=1%3D1&outFields=objectid&returnGeometry=false&f=json");
            long second = features.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("attributes").GetProperty("objectid").GetInt64()).Single(id => id != first);

            await client.AddAttachmentAsync(layer, first, "a.png", ReplicaClient.Png);
            await client.AddAttachmentAsync(layer, first, "b.png", ReplicaClient.OtherPng);
            await client.AddAttachmentAsync(layer, second, "c.png", ReplicaClient.Png);

            (string replicaId, long generation) = await client.CreateReplicaAsync(layer, name + "_r");
            await Task.Delay(100);

            // The record now says it begins after the replica did — as one would for a table made before the record, or one pruned.
            string table = await ReplicaClient.AttachTableAsync(started.Site.Database, name);
            await ReplicaClient.ExecuteAsync(
                started.Site.Database,
                $"insert into hosted.graticula_attachment_log (at, attach_table, kind, attachmentid, rel_objectid) "
                + $"values (clock_timestamp(), '{table}', 'begin', 0, 0)");
            await Task.Delay(100);

            OfflineLayer shape = await client.ShapeAsync(layer);
            SqliteOfflineDeltaReader reader = new();

            JsonElement down = await client.DownloadSyncAsync(layer, replicaId, generation);
            string file = await client.FetchAsync(down, directory, "delta-1.geodatabase");
            OfflineLayerChanges changes = Assert.Single((await reader.ReadDeltaAsync(file, [shape], CancellationToken.None)).Layers);

            // Every attachment the features have, as updates; nothing as an insert or a delete (what was deleted before the record cannot be known).
            Assert.Equal(["a.png", "b.png", "c.png"], changes.AttachmentUpdates.Select(a => a.Name).Order());
            Assert.Empty(changes.AttachmentInserts);
            Assert.Empty(changes.AttachmentDeletes);
            Assert.All(changes.AttachmentUpdates, a => Assert.NotEqual(Guid.Empty, a.GlobalId));
            Assert.Equal(changes.AttachmentUpdates.Count, changes.AttachmentUpdates.Select(a => a.GlobalId).Distinct().Count());

            // It said so in its log, which is how the measurement on a device will find it.
            Assert.Contains("FULL RESEND of attachments", node.Log, StringComparison.Ordinal);

            // The layer moved on: the next window starts after the marker, which the record vouches for, and holds nothing.
            long next = down.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64();
            Assert.True(next > generation);

            JsonElement again = await client.DownloadSyncAsync(layer, replicaId, next);
            string quiet = await client.FetchAsync(again, directory, "delta-2.geodatabase");
            Assert.Empty((await reader.ReadDeltaAsync(quiet, [shape], CancellationToken.None)).Layers);
        }
        finally
        {
            await client.DropAsync(layer);
            Directory.Delete(directory, recursive: true);
        }
    }
}
