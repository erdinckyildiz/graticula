using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// What a test of <c>synchronizeReplica</c> against a real server does over HTTP: make a hosted layer with sync on, edit its
/// attachments, create a replica, download a delta and read it back — the helpers <c>ASyncOfAnAttachmentTests</c> was
/// written with, kept for the tests that followed it.
/// </summary>
internal sealed class ReplicaClient(TwoNodeSite.Node node, string token)
{
    /// <summary>A small PNG.</summary>
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    /// <summary>A different one.</summary>
    public static readonly byte[] OtherPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7, 6, 5];

    /// <summary>A hosted layer: its layer address, its service address and its folder and name.</summary>
    internal sealed record Hosted(string Feature, string Service, string Folder, string Name);

    /// <summary>A request with this client's token; the answer must be a success.</summary>
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await node.Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A request with this client's token whose answer is returned whatever it is.</summary>
    public async Task<(int Status, string Body)> TryAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await node.Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A form of fields, as a client posts them.</summary>
    public static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs) =>
        new(pairs.Select(p => KeyValuePair.Create(p.Key, p.Value)).Append(KeyValuePair.Create("f", "json")));

    /// <summary>Makes a hosted point layer with sync on and two features; answers the first one's object id and GlobalID.</summary>
    public async Task<(Hosted Layer, long First, Guid FirstGlobalId)> NewLayerAsync(string name)
    {
        JsonElement defined = await SendAsync(HttpMethod.Post, "/admin/hosted/define", TwoNodeSite.Json(new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "text", nullable = true } },
        }));
        string feature = defined.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Hosted layer = new(feature, feature[..feature.LastIndexOf('/')], parts[2], parts[3]);

        await SendAsync(HttpMethod.Post, $"/admin/services/{layer.Name}/sync?folder={layer.Folder}", TwoNodeSite.Json(new { enabled = true }));

        JsonElement added = await SendAsync(HttpMethod.Post, $"{feature}/addFeatures", Form(
            ("features", "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"one\"}},"
                + "{\"geometry\":{\"x\":1500,\"y\":2500,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"two\"}}]")));
        long first = added.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();

        JsonElement queried = await SendAsync(HttpMethod.Get, $"{feature}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");
        Guid parent = Guid.Parse(queried.GetProperty("features").EnumerateArray()
            .Single(f => f.GetProperty("attributes").GetProperty("objectid").GetInt64() == first)
            .GetProperty("attributes").GetProperty("globalid").GetString()!.Trim('{', '}'));

        return (layer, first, parent);
    }

    /// <summary>Drops a layer made by <see cref="NewLayerAsync"/>.</summary>
    public async Task DropAsync(Hosted layer)
    {
        using HttpRequestMessage drop = new(HttpMethod.Delete, $"/admin/featureservices/{layer.Name}?folder={layer.Folder}&drop=true");
        drop.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage dropped = await node.Http.SendAsync(drop);
    }

    /// <summary>Adds an attachment; answers its id.</summary>
    public async Task<int> AddAttachmentAsync(Hosted layer, long objectId, string name, byte[] bytes)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "attachment", name);
        form.Add(new StringContent("json"), "f");

        JsonElement added = await SendAsync(HttpMethod.Post, $"{layer.Feature}/{objectId}/addAttachment", form);
        return added.GetProperty("addAttachmentResult").GetProperty("objectId").GetInt32();
    }

    /// <summary>Creates a <c>perLayer</c> mobile geodatabase replica of layer 0; answers its id and generation.</summary>
    public async Task<(string ReplicaId, long Generation)> CreateReplicaAsync(Hosted layer, string name)
    {
        JsonElement created = await SendAsync(HttpMethod.Post, $"{layer.Service}/createReplica", Form(
            ("dataFormat", "sqlite"),
            ("replicaName", name),
            ("layers", "[0]"),
            ("returnAttachments", "true"),
            ("attachmentsSyncDirection", "bidirectional"),
            ("syncModel", "perLayer"),
            ("async", "false"),
            ("transportType", "esriTransportTypeUrl")));

        return (created.GetProperty("replicaID").GetString()!, created.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64());
    }

    /// <summary>A download-only <c>synchronizeReplica</c> of layer 0 from a generation; answers the finished job's status.</summary>
    public Task<JsonElement> DownloadSyncAsync(Hosted layer, string replicaId, long generation, string format = "sqlite") =>
        SendAsync(HttpMethod.Post, $"{layer.Service}/synchronizeReplica", Form(
            ("replicaID", replicaId),
            ("syncDirection", "download"),
            ("dataFormat", format),
            ("syncLayers", $"[{{\"id\":0,\"serverGen\":{generation},\"syncDirection\":\"download\"}}]"),
            ("returnAttachments", "true"),
            ("async", "false")));

    /// <summary>Downloads the file a finished job points at.</summary>
    public async Task<string> FetchAsync(JsonElement status, string directory, string name)
    {
        Assert.Equal("Completed", status.GetProperty("status").GetString());
        string url = status.GetProperty("resultUrl").GetString()!;

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url).PathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await node.Http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}");

        string path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
        return path;
    }

    /// <summary>The layer's shape as the server reads it, for reading a delta back.</summary>
    public async Task<OfflineLayer> ShapeAsync(Hosted layer)
    {
        JsonElement document = await SendAsync(HttpMethod.Get, $"{layer.Feature}?f=json");
        return OfflineReplicaShapes.Layer(
            layer.Name, 0, document.GetRawText(), "PROJCS[\"WGS 84 / Pseudo-Mercator\"]", _ => Nothing<OfflineRow>(), _ => Nothing<OfflineAttachment>());
    }

    /// <summary>One statement against the layer's database.</summary>
    public static async Task ExecuteAsync(string connection, string sql)
    {
        await using Npgsql.NpgsqlConnection open = new(connection);
        await open.OpenAsync();
        await using Npgsql.NpgsqlCommand command = new(sql, open);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A one-number read of the layer's database.</summary>
    public static async Task<long> CountAsync(string connection, string sql)
    {
        await using Npgsql.NpgsqlConnection open = new(connection);
        await open.OpenAsync();
        await using Npgsql.NpgsqlCommand command = new(sql, open);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The name of the attachment table of the layer made with this unique name, in the hosted schema.</summary>
    public static async Task<string> AttachTableAsync(string connection, string layerName)
    {
        await using Npgsql.NpgsqlConnection open = new(connection);
        await open.OpenAsync();
        await using Npgsql.NpgsqlCommand find = new(
            "select table_name from information_schema.tables where table_schema = 'hosted' and table_name like @like and table_name like '%\\_\\_attach'",
            open);
        find.Parameters.AddWithValue("like", "%" + layerName[^8..] + "%");
        return (string)(await find.ExecuteScalarAsync())!;
    }

    private static async IAsyncEnumerable<T> Nothing<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
