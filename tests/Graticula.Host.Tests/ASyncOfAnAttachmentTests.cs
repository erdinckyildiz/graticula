using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Offline.Sqlite;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>One real server over a fresh platform store, started once for the class — <see cref="TwoNodeSite"/>.</summary>
public sealed class OneStartedNode : IAsyncLifetime
{
    /// <summary>The site.</summary>
    public TwoNodeSite Site { get; } = new();

    /// <summary>The node.</summary>
    public TwoNodeSite.Node Node { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        await Site.InitializeAsync();
        Node = await Site.StartAsync("node-a");
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Site.DisposeAsync();
}

/// <summary>
/// <c>synchronizeReplica</c> with an attachment, end to end against PostGIS — ADR-290 §12.3: the glue in
/// <c>OfflineReplicaEndpoints</c> between the change record, the attachment store and the delta geodatabase, which the pieces'
/// own tests (<c>PostGisAttachmentSyncTests</c>, <c>OfflineReplicaTests</c>) do not reach.
/// </summary>
/// <remarks>
/// <b>A real server process</b> (<see cref="TwoNodeSite"/>), a hosted layer made through the API, the delta written by the
/// server's own writer and read back by the Offline.Sqlite reader the server's upload path uses; an upload is a delta this
/// suite writes with the same writer. Nothing is mocked between the HTTP request and the PostGIS table.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ASyncOfAnAttachmentTests(OneStartedNode started) : IClassFixture<OneStartedNode>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static readonly byte[] OtherPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7, 6, 5];

    private TwoNodeSite.Node Node => started.Node;

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Node.Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs) =>
        new(pairs.Select(p => KeyValuePair.Create(p.Key, p.Value)).Append(KeyValuePair.Create("f", "json")));

    private async Task<int> AddAttachmentAsync(string feature, long objectId, string token, string name, byte[] bytes)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "attachment", name);
        form.Add(new StringContent("json"), "f");

        JsonElement added = await SendAsync(HttpMethod.Post, $"{feature}/{objectId}/addAttachment", token, form);
        return added.GetProperty("addAttachmentResult").GetProperty("objectId").GetInt32();
    }

    private async Task ReplaceAttachmentAsync(string feature, long objectId, string token, int attachmentId, string name, byte[] bytes)
    {
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(attachmentId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "attachmentId");
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "attachment", name);
        form.Add(new StringContent("json"), "f");

        JsonElement replaced = await SendAsync(HttpMethod.Post, $"{feature}/{objectId}/updateAttachment", token, form);
        Assert.True(replaced.GetProperty("updateAttachmentResult").GetProperty("success").GetBoolean(), replaced.ToString());
    }

    /// <summary>Downloads the file a finished replica job points at.</summary>
    private async Task<string> DownloadAsync(JsonElement status, string token, string directory, string name)
    {
        Assert.Equal("Completed", status.GetProperty("status").GetString());
        string url = status.GetProperty("resultUrl").GetString()!;

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url).PathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Node.Http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}");

        string path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
        return path;
    }

    private static async Task<long> CountAsync(string connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection open = new(connection);
        await open.OpenAsync();
        await using NpgsqlCommand command = new(sql, open);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_replica_is_sent_the_attachments_the_server_added_replaced_and_removed_and_stores_the_one_a_device_adds_under_its_own_GlobalID()
    {
        string token = await Node.SignInAsync();
        string name = "zz_sync_att_" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetTempPath(), started.Site.Schema, "files-" + name);
        Directory.CreateDirectory(directory);

        // ---- a hosted layer with sync on ----
        JsonElement defined = await SendAsync(HttpMethod.Post, "/admin/hosted/define", token, TwoNodeSite.Json(new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "text", nullable = true } },
        }));
        string feature = defined.GetProperty("services").GetProperty("feature").GetString()!;
        string service = feature[..feature.LastIndexOf('/')];
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            await SendAsync(HttpMethod.Post, $"/admin/services/{parts[3]}/sync?folder={parts[2]}", token, TwoNodeSite.Json(new { enabled = true }));

            JsonElement addedFeatures = await SendAsync(HttpMethod.Post, $"{feature}/addFeatures", token, Form(
                ("features", "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"one\"}},"
                    + "{\"geometry\":{\"x\":1500,\"y\":2500,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"two\"}}]")));
            long first = addedFeatures.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();

            JsonElement queried = await SendAsync(HttpMethod.Get, $"{feature}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json", token);
            Guid parentGlobalId = Guid.Parse(queried.GetProperty("features").EnumerateArray()
                .Single(f => f.GetProperty("attributes").GetProperty("objectid").GetInt64() == first)
                .GetProperty("attributes").GetProperty("globalid").GetString()!.Trim('{', '}'));

            // Two attachments the replica will already hold: one the server then replaces, one it then deletes.
            int replaced = await AddAttachmentAsync(feature, first, token, "replace-me.png", Png);
            int removed = await AddAttachmentAsync(feature, first, token, "remove-me.png", Png);

            // ---- the replica ----
            JsonElement created = await SendAsync(HttpMethod.Post, $"{service}/createReplica", token, Form(
                ("dataFormat", "sqlite"),
                ("replicaName", name + "_r"),
                ("layers", "[0]"),
                ("returnAttachments", "true"),
                ("attachmentsSyncDirection", "bidirectional"),
                ("syncModel", "perLayer"),
                ("async", "false"),
                ("transportType", "esriTransportTypeUrl")));
            string replicaId = created.GetProperty("replicaID").GetString()!;
            long generation = created.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64();

            // The record stamps a change when it commits; a change must commit after the generation, by the database's clock.
            await Task.Delay(100);

            // ---- the server changes its attachments: one added, one replaced, one deleted ----
            int added = await AddAttachmentAsync(feature, first, token, "server-added.png", Png);
            await ReplaceAttachmentAsync(feature, first, token, replaced, "replaced.png", OtherPng);
            await SendAsync(HttpMethod.Post, $"{feature}/{first}/deleteAttachments", token, Form(("attachmentIds", removed.ToString(System.Globalization.CultureInfo.InvariantCulture))));

            // ---- the delta, read back ----
            JsonElement layerDocument = await SendAsync(HttpMethod.Get, $"{feature}?f=json", token);
            OfflineLayer shape = OfflineReplicaShapes.Layer(
                parts[3], 0, layerDocument.GetRawText(), "PROJCS[\"WGS 84 / Pseudo-Mercator\"]",
                _ => Nothing<OfflineRow>(), _ => Nothing<OfflineAttachment>());
            SqliteOfflineDeltaReader reader = new();

            JsonElement downloaded = await SendAsync(HttpMethod.Post, $"{service}/synchronizeReplica", token, Form(
                ("replicaID", replicaId),
                ("syncDirection", "download"),
                ("syncLayers", $"[{{\"id\":0,\"serverGen\":{generation},\"syncDirection\":\"download\"}}]"),
                ("returnAttachments", "true"),
                ("async", "false")));
            string file = await DownloadAsync(downloaded, token, directory, "delta-1.geodatabase");
            OfflineDelta delta = await reader.ReadDeltaAsync(file, [shape], CancellationToken.None);
            OfflineLayerChanges changes = Assert.Single(delta.Layers);

            OfflineAttachment inserted = Assert.Single(changes.AttachmentInserts);
            Assert.Equal("server-added.png", inserted.Name);
            Assert.Equal(parentGlobalId, inserted.ParentGlobalId);
            Assert.Equal(added, (int)inserted.AttachmentId);
            Assert.NotEqual(Guid.Empty, inserted.GlobalId);

            OfflineAttachment updated = Assert.Single(changes.AttachmentUpdates);
            Assert.Equal("replaced.png", updated.Name);
            Assert.Equal(replaced, (int)updated.AttachmentId);
            Assert.Equal(OtherPng, updated.Data.ToArray());
            Assert.NotEqual(inserted.GlobalId, updated.GlobalId);

            OfflineKey deleted = Assert.Single(changes.AttachmentDeletes);
            Assert.Equal(removed, (int)deleted.ObjectId);
            Assert.NotEqual(Guid.Empty, deleted.GlobalId);

            // Nothing about the feature's own row: only its attachments changed.
            Assert.Empty(changes.Inserts);
            Assert.Empty(changes.Updates);
            Assert.Empty(changes.Deletes);

            // ---- the other way: the device adds an attachment under a GlobalID of its own ----
            long nextGeneration = downloaded.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64();
            Assert.True(nextGeneration > generation, "The generation did not advance.");

            Guid clientGlobalId = Guid.NewGuid();
            string upload = Path.Combine(directory, "upload.geodatabase");
            await new SqliteMobileGeodatabaseWriter().WriteDeltaAsync(
                new OfflineDelta(
                    Guid.Parse(replicaId),
                    nextGeneration,
                    [new OfflineLayerChanges(shape, [], [], [], [new OfflineAttachment(900, parentGlobalId, clientGlobalId, "from-device.png", "image/png", Png)], [], [])],
                    parts[3]),
                upload,
                CancellationToken.None);

            using MultipartFormDataContent body = new();
            ByteArrayContent bytes = new(await File.ReadAllBytesAsync(upload));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/x-sqlite3");
            body.Add(bytes, "file", "upload.geodatabase");
            body.Add(new StringContent("json"), "f");
            JsonElement sent = await SendAsync(HttpMethod.Post, $"{service}/uploads/upload", token, body);
            string uploadId = sent.GetProperty("item").GetProperty("itemID").GetString()!;

            JsonElement bidirectional = await SendAsync(HttpMethod.Post, $"{service}/synchronizeReplica", token, Form(
                ("replicaID", replicaId),
                ("syncDirection", "bidirectional"),
                ("editsUploadID", uploadId),
                ("syncLayers", $"[{{\"id\":0,\"serverGen\":{nextGeneration},\"syncDirection\":\"bidirectional\"}}]"),
                ("returnAttachments", "true"),
                ("async", "false")));
            Assert.Equal("Completed", bidirectional.GetProperty("status").GetString());

            // Stored on the server, under the GlobalID the device gave it, against the feature it named.
            // The table is named for the layer's own, which the server chose; find it by the unique part of the name.
            await using NpgsqlConnection lookup = new(started.Site.Database);
            await lookup.OpenAsync();
            await using NpgsqlCommand find = new(
                "select table_name from information_schema.tables where table_schema = 'hosted' and table_name like @like and table_name like '%\\_\\_attach'",
                lookup);
            find.Parameters.AddWithValue("like", "%" + name[^8..] + "%");
            string table = $"hosted.\"{(string)(await find.ExecuteScalarAsync())!}\"";
            Assert.Equal(1L, await CountAsync(
                started.Site.Database,
                $"select count(*) from {table} where globalid = @g and rel_objectid = @f and att_name = 'from-device.png'",
                ("g", clientGlobalId), ("f", first)));

            // And not sent back to the device that made it.
            string again = await DownloadAsync(bidirectional, token, directory, "delta-2.geodatabase");
            OfflineDelta second = await reader.ReadDeltaAsync(again, [shape], CancellationToken.None);
            Assert.All(second.Layers, l => Assert.DoesNotContain(l.AttachmentInserts.Concat(l.AttachmentUpdates), a => a.GlobalId == clientGlobalId));
        }
        finally
        {
            using HttpRequestMessage drop = new(HttpMethod.Delete, $"/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true");
            drop.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage dropped = await Node.Http.SendAsync(drop);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async IAsyncEnumerable<T> Nothing<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
