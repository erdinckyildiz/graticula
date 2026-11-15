using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Platform.Catalog;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-409: ArcGIS's JSON upload (<c>synchronizeReplica</c>'s <c>edits</c>) is read into what a geodatabase upload is, under the same
/// conflict rule (ADR-288 §5.5), and a JSON download carries the server's attachment changes.
/// </summary>
public sealed class JsonEditsTests
{
    private static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid B = Guid.Parse("66666666-7777-8888-9999-000000000000");
    private static readonly Guid C = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static List<OfflineJsonEdits.Entry> Read(string json)
    {
        Assert.True(OfflineJsonEdits.TryRead(json, out List<OfflineJsonEdits.Entry> entries, out string? error), error);
        return entries;
    }

    private static string Failure(string json)
    {
        Assert.False(OfflineJsonEdits.TryRead(json, out _, out string? error));
        return error!;
    }

    private static JsonObject Parsed(string? text) => Assert.IsType<JsonArray>(JsonNode.Parse(text!)).Select(n => n!.AsObject()).First();

    [Fact]
    public void Features_are_read_under_the_conflict_rule_the_geodatabase_upload_follows()
    {
        OfflineLayer shape = OfflineReplicaTests.Shape();
        HashSet<string> editable = OfflineReplicaShapes.EditableFields(shape.LayerDocument);

        OfflineJsonEdits.Entry entry = Assert.Single(Read($$"""
            [{ "id": 0, "features": {
                 "adds": [
                   { "attributes": { "globalid": "{{{B}}}", "label": "new", "created_user": "hacker", "objectid": 99 }, "geometry": { "x": 1, "y": 2 } },
                   { "attributes": { "GlobalID": "{{A}}", "label": "sent twice" } } ],
                 "updates": [ { "attributes": { "globalid": "{{C}}", "label": "the server deleted it" } } ],
                 "deleteIds": [ "{{A}}", "{{C}}" ] } }]
            """));

        Assert.Equal([A, B, C], OfflineJsonEdits.FeatureGlobalIds(entry, "globalid").Order());

        ReplicaLayerEdits? edits = OfflineJsonEdits.Edits(entry, shape, "globalid", editable, new HashSet<Guid> { A }, out string? error);

        Assert.True(edits is not null, error);

        // A new row is added with the fields a client may write and its own GlobalID; the object id and `created_user` are the server's.
        JsonObject added = Parsed(edits!.Adds);
        Assert.Equal("new", added["attributes"]!["label"]!.GetValue<string>());
        Assert.Null(added["attributes"]!["created_user"]);
        Assert.Null(added["attributes"]!["objectid"]);
        Assert.Equal(1, added["geometry"]!["x"]!.GetValue<int>());

        // An add of a GlobalID the server holds is an update (the same upload sent twice adds nothing twice); names are read in any case.
        JsonObject updated = Parsed(edits.Updates);
        Assert.Equal("sent twice", updated["attributes"]!["label"]!.GetValue<string>());

        // A delete of one it holds is applied; an update and a delete of one it no longer holds are dropped and counted.
        Assert.Equal($$"""["{{{A.ToString().ToUpperInvariant()}}}"]""", edits.Deletes);
        Assert.Equal(2, edits.Dropped);
    }

    [Fact]
    public void The_flat_shape_is_read_as_well_as_the_references()
    {
        OfflineJsonEdits.Entry entry = Assert.Single(Read($$"""
            [{ "id": 1, "adds": [ { "attributes": { "globalid": "{{A}}" } } ], "updates": [], "deletes": [ "{{B}}" ] }]
            """));

        Assert.Equal(1, entry.Id);
        Assert.Single(entry.Adds);
        Assert.Equal([B], entry.Deletes);
    }

    [Fact]
    public void An_attachment_is_read_with_its_bytes_and_a_delete_by_its_GlobalID()
    {
        OfflineLayer shape = OfflineReplicaTests.Shape();
        byte[] bytes = Encoding.UTF8.GetBytes("hello");

        OfflineJsonEdits.Entry entry = Assert.Single(Read($$"""
            [{ "id": 0, "attachments": {
                 "adds": [ { "globalid": "{{A}}", "parentGlobalid": "{{B}}", "contentType": "text/plain", "name": "a.txt", "data": "{{Convert.ToBase64String(bytes)}}" } ],
                 "deleteIds": [ "{{C}}" ] } }]
            """));

        OfflineLayerChanges? changes = OfflineJsonEdits.Attachments(entry, shape, out string? error);

        Assert.True(changes is not null, error);
        OfflineAttachment added = Assert.Single(changes!.AttachmentInserts);
        Assert.Equal((A, B, "a.txt", "text/plain"), (added.GlobalId, added.ParentGlobalId, added.Name, added.ContentType));
        Assert.Equal(bytes, added.Data.ToArray());
        Assert.Equal(C, Assert.Single(changes.AttachmentDeletes).GlobalId);
        Assert.Empty(changes.AttachmentUpdates);
    }

    [Fact]
    public void What_cannot_be_read_is_refused_by_name_and_nothing_is_taken_for_something_else()
    {
        OfflineLayer shape = OfflineReplicaTests.Shape();

        // A number in deleteIds is not taken for an object id.
        Assert.Contains("not a GlobalID", Failure("""[{ "id": 0, "features": { "deleteIds": [ 7 ] } }]"""), StringComparison.Ordinal);
        Assert.Contains("numeric id", Failure("""[{ "features": {} }]"""), StringComparison.Ordinal);

        // An attachment with only an address is not fetched from here.
        OfflineJsonEdits.Entry byUrl = Assert.Single(Read($$"""
            [{ "id": 0, "attachments": { "adds": [ { "globalid": "{{A}}", "parentGlobalid": "{{B}}", "url": "http://elsewhere/x.png" } ] } }]
            """));
        Assert.Null(OfflineJsonEdits.Attachments(byUrl, shape, out string? error));
        Assert.Contains("does not read an address", error, StringComparison.Ordinal);

        // A feature without its GlobalID cannot be named.
        OfflineJsonEdits.Entry nameless = Assert.Single(Read("""[{ "id": 0, "features": { "adds": [ { "attributes": { "label": "x" } } ] } }]"""));
        Assert.Null(OfflineJsonEdits.Edits(nameless, shape, "globalid", OfflineReplicaShapes.EditableFields(shape.LayerDocument), new HashSet<Guid>(), out error));
        Assert.Contains("GlobalID", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_json_download_carries_the_attachment_changes_by_address_or_inline()
    {
        string file = Path.Combine(Path.GetTempPath(), "json-attach-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            OfflineLayer shape = OfflineReplicaTests.Shape();
            ReplicaRecord replica = new(
                Guid.Parse("D75D8B19-382F-4388-A388-AE8F0BDACAE8"), "graticula_test", Guid.NewGuid(), Guid.NewGuid(), "ada", "perLayer",
                [], true, "bidirectional", null, 3857, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            byte[] bytes = [1, 2, 3];
            OfflineLayerChanges changes = new(
                shape, [], [], [],
                [new OfflineAttachment(3, B, A, "new.png", "image/png", bytes, ParentObjectId: 5)],
                [new OfflineAttachment(4, B, C, "again.png", "image/png", bytes, ParentObjectId: 5)],
                [new OfflineKey(9, B)]);
            ReplicaLayer[] generations = [new ReplicaLayer(0, 99, "useFilter", string.Empty, true)];

            await OfflineJsonReplica.WriteChangesAsync(
                file, replica, [changes], generations, CancellationToken.None, "https://host/rest/services/hosted/x/FeatureServer");

            JsonElement attachments = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement.GetProperty("edits")[0].GetProperty("attachments");
            JsonElement inserted = attachments.GetProperty("adds")[0];

            Assert.Equal("{" + A.ToString().ToUpperInvariant() + "}", inserted.GetProperty("globalid").GetString());
            Assert.Equal("{" + B.ToString().ToUpperInvariant() + "}", inserted.GetProperty("parentGlobalid").GetString());
            Assert.Equal("https://host/rest/services/hosted/x/FeatureServer/0/5/attachments/3", inserted.GetProperty("url").GetString());
            Assert.Equal("again.png", attachments.GetProperty("updates")[0].GetProperty("name").GetString());
            Assert.Equal("{" + B.ToString().ToUpperInvariant() + "}", attachments.GetProperty("deleteIds")[0].GetString());

            await OfflineJsonReplica.WriteChangesAsync(
                file, replica, [changes], generations, CancellationToken.None, "https://host/rest/services/hosted/x/FeatureServer", dataByUrl: false);

            JsonElement inline = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement.GetProperty("edits")[0]
                .GetProperty("attachments").GetProperty("adds")[0];
            Assert.Equal(Convert.ToBase64String(bytes), inline.GetProperty("data").GetString());
            Assert.False(inline.TryGetProperty("url", out _));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
