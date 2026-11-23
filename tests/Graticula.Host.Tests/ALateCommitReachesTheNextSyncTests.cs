using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-401, against a real server over PostgreSQL: a transaction that began writing before a sync and committed after it is
/// sent by the next sync. A history row is stamped with its transaction's start; before the generation was closed at the
/// oldest open writer, that row fell before the generation the first sync handed out and after the moment it read, and no
/// window ever held it.
/// </summary>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class ALateCommitReachesTheNextSyncTests(OneStartedNode started)
{
    [Fact]
    public async Task An_edit_open_across_a_sync_arrives_in_the_one_after_it()
    {
        TwoNodeSite.Node node = started.Node;
        ReplicaClient client = new(node, await node.SignInAsync());
        string name = "zz_sync_late_" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetTempPath(), started.Site.Schema, "files-" + name);
        Directory.CreateDirectory(directory);

        (ReplicaClient.Hosted layer, long first, Guid _) = await client.NewLayerAsync(name);

        try
        {
            (string replicaId, long generation) = await client.CreateReplicaAsync(layer, name + "_r");
            string table = await TableAsync(started.Site.Database, name);
            await Task.Delay(50);

            // ---- a writer opens a transaction and edits a feature, and does not commit yet ----
            await using NpgsqlConnection writer = new(started.Site.Database);
            await writer.OpenAsync();
            await using NpgsqlTransaction open = await writer.BeginTransactionAsync();
            await using (NpgsqlCommand edit = new($"update hosted.\"{table}\" set label = 'late' where objectid = {first}", writer, open))
            {
                Assert.Equal(1, await edit.ExecuteNonQueryAsync());
            }

            DateTimeOffset began;
            await using (NpgsqlCommand start = new("select now()", writer, open))
            {
                began = new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await start.ExecuteScalarAsync())!, DateTimeKind.Utc));
            }

            await Task.Delay(50);

            // ---- a sync runs while it is open: the edit is not committed, so it is not in this one ----
            JsonElement during = await client.DownloadSyncAsync(layer, replicaId, generation, format: "json");
            long handedOut = during.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64();
            Assert.DoesNotContain("late", await LabelsAsync(client, during, directory, "during.json"));

            // The generation handed out is before the open writer began, so its rows belong to the next window.
            Assert.True(
                handedOut < began.ToUnixTimeMilliseconds(),
                $"The generation {handedOut} is not before the open transaction's start {began.ToUnixTimeMilliseconds()}.");

            // ---- it commits, and the next sync carries it ----
            await open.CommitAsync();
            await Task.Delay(50);

            JsonElement after = await client.DownloadSyncAsync(layer, replicaId, handedOut, format: "json");
            Assert.Contains("late", await LabelsAsync(client, after, directory, "after.json"));
        }
        finally
        {
            await client.DropAsync(layer);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string[]> LabelsAsync(ReplicaClient client, JsonElement status, string directory, string file)
    {
        string path = await client.FetchAsync(status, directory, file);
        JsonElement edits = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("edits");

        // A sync with nothing to send answers no layer at all.
        if (edits.GetArrayLength() == 0)
        {
            return [];
        }

        JsonElement features = edits[0].GetProperty("features");

        return [.. features.GetProperty("adds").EnumerateArray().Concat(features.GetProperty("updates").EnumerateArray())
            .Select(f => f.GetProperty("attributes").GetProperty("label").GetString() ?? string.Empty)];
    }

    private static async Task<string> TableAsync(string connection, string layerName)
    {
        await using NpgsqlConnection open = new(connection);
        await open.OpenAsync();
        await using NpgsqlCommand find = new(
            "select table_name from information_schema.tables where table_schema = 'hosted' and table_name like @like "
            + "and position('__' in table_name) = 0",
            open);
        find.Parameters.AddWithValue("like", "%" + layerName[^8..] + "%");
        return (string)(await find.ExecuteScalarAsync())!;
    }
}
