using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-426, against a real server over PostgreSQL: <c>synchronizeReplica</c> with <c>dataFormat=json</c> carries the server's attachment
/// changes down, and its <c>edits</c> parameter — ArcGIS's JSON upload — is applied through the same path a geodatabase upload is.
/// </summary>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class AJsonReplicaSyncsAttachmentsBothWaysTests(OneStartedNode started)
{
    private static string Braced(Guid id) => "{" + id.ToString("D").ToUpperInvariant() + "}";

    [Fact]
    public async Task A_json_download_carries_attachment_changes_and_a_json_upload_adds_updates_and_deletes_by_GlobalID()
    {
        TwoNodeSite.Node node = started.Node;
        ReplicaClient client = new(node, await node.SignInAsync());
        string name = "zz_sync_json_" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetTempPath(), started.Site.Schema, "files-" + name);
        Directory.CreateDirectory(directory);

        (ReplicaClient.Hosted layer, long first, Guid parent) = await client.NewLayerAsync(name);

        try
        {
            // ---- the way down ----
            int replaced = await client.AddAttachmentAsync(layer, first, "replace-me.png", ReplicaClient.Png);
            int removed = await client.AddAttachmentAsync(layer, first, "remove-me.png", ReplicaClient.Png);
            (string replicaId, long generation) = await client.CreateReplicaAsync(layer, name + "_r");
            await Task.Delay(100);

            int added = await client.AddAttachmentAsync(layer, first, "server-added.png", ReplicaClient.Png);

            using (MultipartFormDataContent form = new())
            {
                form.Add(new StringContent(replaced.ToString(System.Globalization.CultureInfo.InvariantCulture)), "attachmentId");
                ByteArrayContent file = new(ReplicaClient.OtherPng);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(file, "attachment", "replaced.png");
                form.Add(new StringContent("json"), "f");
                await client.SendAsync(HttpMethod.Post, $"{layer.Feature}/{first}/updateAttachment", form);
            }

            await client.SendAsync(HttpMethod.Post, $"{layer.Feature}/{first}/deleteAttachments", ReplicaClient.Form(
                ("attachmentIds", removed.ToString(System.Globalization.CultureInfo.InvariantCulture))));

            JsonElement down = await client.DownloadSyncAsync(layer, replicaId, generation, format: "json");
            string path = await client.FetchAsync(down, directory, "changes.json");
            JsonElement attachments = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("edits")[0].GetProperty("attachments");

            JsonElement insert = Assert.Single(attachments.GetProperty("adds").EnumerateArray());
            Assert.Equal("server-added.png", insert.GetProperty("name").GetString());
            Assert.Equal(Braced(parent), insert.GetProperty("parentGlobalid").GetString());
            Assert.EndsWith($"/0/{first}/attachments/{added}", insert.GetProperty("url").GetString(), StringComparison.Ordinal);

            JsonElement update = Assert.Single(attachments.GetProperty("updates").EnumerateArray());
            Assert.Equal("replaced.png", update.GetProperty("name").GetString());
            Assert.NotEqual(insert.GetProperty("globalid").GetString(), update.GetProperty("globalid").GetString());

            Assert.Single(attachments.GetProperty("deleteIds").EnumerateArray());

            // ---- the way up: a feature and its attachment, added by GlobalID in one upload ----
            Guid featureId = Guid.NewGuid();
            Guid attachmentId = Guid.NewGuid();
            long next = down.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64();
            string connection = started.Site.Database;
            string table = await ReplicaClient.AttachTableAsync(connection, name);
            string featureTable = await TableOf(connection, name);
            string data = Convert.ToBase64String(ReplicaClient.Png);

            string Edits(string features, string attachmentPart) =>
                $$"""[{ "id": 0, "features": { {{features}} }, "attachments": { {{attachmentPart}} } }]""";

            async Task<JsonElement> UploadAsync(string edits) => await client.SendAsync(
                HttpMethod.Post, $"{layer.Service}/synchronizeReplica", ReplicaClient.Form(
                    ("replicaID", replicaId),
                    ("syncDirection", "upload"),
                    ("dataFormat", "json"),
                    ("edits", edits),
                    ("syncLayers", $"[{{\"id\":0,\"serverGen\":{next},\"syncDirection\":\"upload\"}}]"),
                    ("returnAttachments", "true"),
                    ("async", "false")));

            string add = Edits(
                $$"""
                "adds": [ { "attributes": { "globalid": "{{Braced(featureId)}}", "label": "from json" }, "geometry": { "x": 3000, "y": 4000, "spatialReference": { "wkid": 3857 } } } ]
                """,
                $$"""
                "adds": [ { "globalid": "{{Braced(attachmentId)}}", "parentGlobalid": "{{Braced(featureId)}}", "contentType": "image/png", "name": "from-json.png", "data": "{{data}}" } ]
                """);

            Assert.Equal("Completed", (await UploadAsync(add)).GetProperty("status").GetString());
            Assert.Equal(1L, await ReplicaClient.CountAsync(connection, $"select count(*) from hosted.\"{featureTable}\" where label = 'from json'"));
            Assert.Equal(1L, await ReplicaClient.CountAsync(connection, $"select count(*) from hosted.\"{table}\" where globalid = '{attachmentId}' and att_name = 'from-json.png'"));

            // The same upload again, as after an answer that never arrived, adds nothing twice (the add of a GlobalID the server holds is an update).
            Assert.Equal("Completed", (await UploadAsync(add)).GetProperty("status").GetString());
            Assert.Equal(1L, await ReplicaClient.CountAsync(connection, $"select count(*) from hosted.\"{featureTable}\" where label = 'from json'"));
            Assert.Equal(1L, await ReplicaClient.CountAsync(connection, $"select count(*) from hosted.\"{table}\" where globalid = '{attachmentId}'"));

            // An update by GlobalID, and the attachment deleted by the GlobalID it was given.
            string change = Edits(
                $$"""
                "updates": [ { "attributes": { "globalid": "{{Braced(featureId)}}", "label": "changed by json" } } ]
                """,
                $$"""
                "deleteIds": [ "{{Braced(attachmentId)}}" ]
                """);

            Assert.Equal("Completed", (await UploadAsync(change)).GetProperty("status").GetString());
            Assert.Equal(1L, await ReplicaClient.CountAsync(connection, $"select count(*) from hosted.\"{featureTable}\" where label = 'changed by json'"));
            Assert.Equal(0L, await ReplicaClient.CountAsync(connection, $"select count(*) from hosted.\"{table}\" where globalid = '{attachmentId}'"));

            // What cannot be read is refused by name, and nothing is applied.
            (int status, string body) = await client.TryAsync(
                HttpMethod.Post, $"{layer.Service}/synchronizeReplica", ReplicaClient.Form(
                    ("replicaID", replicaId),
                    ("syncDirection", "upload"),
                    ("dataFormat", "json"),
                    ("edits", """[{ "id": 0, "features": { "deleteIds": [ 7 ] } }]"""),
                    ("syncLayers", $"[{{\"id\":0,\"serverGen\":{next},\"syncDirection\":\"upload\"}}]"),
                    ("async", "false")));
            Assert.Equal(400, status);
            Assert.Contains("not a GlobalID", body, StringComparison.Ordinal);
        }
        finally
        {
            await client.DropAsync(layer);
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The layer's own table in the hosted schema, found by the unique part of its name.</summary>
    private static async Task<string> TableOf(string connection, string layerName)
    {
        await using Npgsql.NpgsqlConnection open = new(connection);
        await open.OpenAsync();
        await using Npgsql.NpgsqlCommand find = new(
            "select table_name from information_schema.tables where table_schema = 'hosted' and table_name like @like "
            + "and table_name not like '%\\_\\_%' and table_name not like '%\\_chunk'",
            open);
        find.Parameters.AddWithValue("like", "%" + layerName[^8..] + "%");
        return (string)(await find.ExecuteScalarAsync())!;
    }
}
