using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-288's replica operations, the parts that decide without a server: what <c>createReplica</c> and
/// <c>synchronizeReplica</c> accept and refuse, the sync-enabled documents, the difference between two moments of a
/// layer, an uploaded delta turned into <c>applyEdits</c>, the answers' shapes, and the walk back through a history.
/// </summary>
/// <remarks>
/// Written 2026-11-10 with the server side of ADR-288. The file itself is the writer's (another project), so a fake
/// writer stands in for it here; whether Field Maps takes what this answers is ADR-288's condition 1, on a device.
/// </remarks>
public sealed class OfflineReplicaTests
{
    private static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid B = Guid.Parse("66666666-7777-8888-9999-000000000000");
    private static readonly Guid C = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private const string LayerDocument = """
        {
          "id": 0, "name": "Hydrants", "type": "Feature Layer", "geometryType": "esriGeometryPoint",
          "hasZ": true, "hasM": false, "objectIdField": "objectid", "globalIdField": "globalid", "hasAttachments": true,
          "extent": { "xmin": 1, "ymin": 2, "xmax": 3, "ymax": 4, "spatialReference": { "wkid": 102100, "latestWkid": 3857 } },
          "fields": [
            { "name": "objectid", "type": "esriFieldTypeOID", "alias": "OBJECTID", "nullable": false, "editable": false },
            { "name": "globalid", "type": "esriFieldTypeGlobalID", "nullable": false, "editable": false, "length": 38 },
            { "name": "status", "type": "esriFieldTypeSmallInteger", "nullable": true, "editable": true,
              "domain": { "type": "codedValue", "name": "Status", "codedValues": [ { "name": "Open", "code": 1 }, { "name": "Shut", "code": 2 } ] } },
            { "name": "label", "type": "esriFieldTypeString", "nullable": true, "editable": true, "length": 50 },
            { "name": "created_user", "type": "esriFieldTypeString", "nullable": true, "editable": false },
            { "name": "shape", "type": "esriFieldTypeGeometry" }
          ]
        }
        """;

    private static PublishedService Service(bool hosted = true, bool sync = true, bool view = false)
    {
        PublishedLayer Layer(int index, string name) => new(
            Guid.NewGuid(),
            new LayerDefinition(name, "public", name, "geom", 3857, "objectid", "objectid", isHosted: hosted),
            "postgis",
            "unused",
            GeometryKind.Point,
            owner: null,
            SharingScope.Private,
            ServiceStatus.Started,
            layerIndex: index,
            serviceName: "Hydrants")
        {
            ServiceSyncEnabled = sync,
        };

        return new PublishedService(
            Guid.NewGuid(), "Hydrants", "hosted", "FeatureServer", null, null, SharingScope.Private, ServiceStatus.Started,
            [Layer(0, "hydrants"), Layer(1, "valves")])
        {
            SyncEnabled = sync,
            ViewOf = view ? Guid.NewGuid() : null,
        };
    }

    private static Func<string, string?> Fields(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> values = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => values.TryGetValue(name, out string? value) ? value : null;
    }

    private static OfflineLayer Shape() => OfflineReplicaShapes.Layer(
        "Hydrants", 0, LayerDocument, "PROJCS[\"WGS 84 / Pseudo-Mercator\"]", token => Nothing<OfflineRow>(token), token => Nothing<OfflineAttachment>(token));

    private static async IAsyncEnumerable<T> Nothing<T>([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await Task.CompletedTask;
        cancellation.ThrowIfCancellationRequested();
        yield break;
    }

    private static OfflineRow Row(long oid, Guid gid, string label) =>
        new([oid, gid, (short)1, label, "someone"], Point.Create(10, 20, 5, null));

    // ------------------------------------------------------------------------------------------------ createReplica

    [Fact]
    public void A_per_replica_sync_is_refused_with_ArcGISs_own_sentence()
    {
        Assert.False(OfflineReplicaRequests.TryReadCreate(
            Fields(("dataFormat", "sqlite"), ("syncModel", "perReplica"), ("layers", "0")), Service(), out _, out string? error));

        Assert.Equal("Enterprise hosted feature services do not support replicas of type 'perReplica'.", error);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("filegdb")]
    public void A_format_other_than_sqlite_is_refused_and_names_the_decision(string format)
    {
        Assert.False(OfflineReplicaRequests.TryReadCreate(
            Fields(("dataFormat", format), ("syncModel", "perLayer"), ("layers", "0")), Service(), out _, out string? error));

        Assert.Contains("ADR-288 §5.4", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_format_is_ArcGISs_default_json_and_is_refused_too()
    {
        Assert.False(OfflineReplicaRequests.TryReadCreate(Fields(("layers", "0")), Service(), out _, out string? error));
        Assert.Contains("sqlite", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("my replica")]
    [InlineData("field-maps")]
    [InlineData("çay")]
    public void A_replica_name_outside_letters_digits_and_underscores_is_refused(string name)
    {
        Assert.False(OfflineReplicaRequests.TryReadCreate(
            Fields(("dataFormat", "sqlite"), ("replicaName", name), ("layers", "0")), Service(), out _, out string? error));

        Assert.Contains("letters, digits and underscores", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_maps_request_is_read_whole()
    {
        Assert.True(OfflineReplicaRequests.TryReadCreate(
            Fields(
                ("dataFormat", "sqlite"),
                ("replicaName", "Hydrants_offline_1"),
                ("layers", "[1,0]"),
                ("layerQueries", """{"0":{"queryOption":"useFilter","where":"status = 1","useGeometry":true},"1":{"queryOption":"none","useGeometry":false}}"""),
                ("geometry", """{"xmin":3000000,"ymin":4500000,"xmax":3300000,"ymax":5000000,"spatialReference":{"wkid":102100}}"""),
                ("geometryType", "esriGeometryEnvelope"),
                ("returnAttachments", "true"),
                ("attachmentsSyncDirection", "bidirectional"),
                ("syncModel", "perLayer"),
                ("async", "true"),
                ("transportType", "esriTransportTypeUrl")),
            Service(), out CreateReplicaRequest? request, out string? error), error);

        Assert.Equal("Hydrants_offline_1", request!.Name);
        Assert.Equal([0, 1], request.Layers);
        Assert.True(request.Registered);
        Assert.True(request.Async);
        Assert.True(request.ReturnAttachments);
        Assert.Equal(3857, request.Srid);
        Assert.Equal(new Envelope(3000000, 4500000, 3300000, 5000000), request.Extent);
        Assert.Equal("status = 1", request.Queries[0].Where);
        Assert.Equal("none", request.Queries[1].QueryOption);
        Assert.False(request.Queries[1].UseGeometry);
    }

    [Fact]
    public void An_envelope_may_be_four_numbers_and_inSR_names_its_reference()
    {
        Assert.True(OfflineReplicaRequests.TryReadCreate(
            Fields(("dataFormat", "sqlite"), ("layers", "0"), ("geometry", "26,36,45,42"), ("inSR", "4326")),
            Service(), out CreateReplicaRequest? request, out string? error), error);

        Assert.Equal(4326, request!.Srid);
        Assert.Equal("none", request.SyncModel);
        Assert.False(request.Registered);
        Assert.IsType<Polygon>(request.Filter);
    }

    [Fact]
    public void A_layer_the_service_does_not_have_is_refused()
    {
        Assert.False(OfflineReplicaRequests.TryReadCreate(
            Fields(("dataFormat", "sqlite"), ("layers", "0,7")), Service(), out _, out string? error));

        Assert.Contains("7 is not a layer", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ synchronizeReplica

    [Fact]
    public void A_sync_reads_each_layers_generation_and_direction()
    {
        Assert.True(OfflineReplicaRequests.TryReadSynchronize(
            Fields(
                ("replicaID", "{D2B16AE0-C3B5-48E7-9300-0445085CF669}"),
                ("syncDirection", "bidirectional"),
                ("syncLayers", """[{"id":0,"serverGen":1794286130989,"syncDirection":"bidirectional"},{"id":1,"serverGen":1794286130989,"syncDirection":"download"}]"""),
                ("editsUploadID", "iabc"),
                ("editsUploadFormat", "sqlite"),
                ("async", "true")),
            out SynchronizeReplicaRequest? request, out string? error), error);

        Assert.Equal(Guid.Parse("D2B16AE0-C3B5-48E7-9300-0445085CF669"), request!.ReplicaId);
        Assert.Equal(2, request.Layers.Count);
        Assert.Equal(1794286130989, request.Layers[0].ServerGeneration);
        Assert.Equal("download", request.Layers[1].Direction);
        Assert.Equal("iabc", request.UploadId);
    }

    [Theory]
    [InlineData("syncDirection", "snapshot", "snapshot")]
    [InlineData("editsUploadFormat", "json", "editsUploadFormat")]
    [InlineData("replicaID", "nope", "not a replica id")]
    public void What_a_sync_cannot_do_is_refused_by_name(string key, string value, string said)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["replicaID"] = "D2B16AE0-C3B5-48E7-9300-0445085CF669",
            ["editsUploadID"] = "iabc",
            [key] = value,
        };

        Assert.False(OfflineReplicaRequests.TryReadSynchronize(
            name => values.TryGetValue(name, out string? v) ? v : null, out _, out string? error));
        Assert.Contains(said, error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ documents

    [Fact]
    public void Sync_is_offered_to_a_caller_who_may_edit_and_not_to_a_reader()
    {
        Assert.Equal("Query,Create,Update,Delete,Editing,Sync", SyncDocument.WithSync("Query,Create,Update,Delete,Editing"));
        Assert.Equal("Query", SyncDocument.WithSync("Query"));
        Assert.Equal("Query,Update,Sync", SyncDocument.WithSync("Query,Update,Sync"));
    }

    [Fact]
    public void A_sync_enabled_service_document_says_what_ArcGIS_says_and_what_this_server_does_not_do()
    {
        JsonObject document = new() { ["capabilities"] = "Query,Create,Update,Delete,Editing", ["syncEnabled"] = false };

        SyncDocument.ApplyToService(document);

        Assert.True(document["syncEnabled"]!.GetValue<bool>());
        Assert.True(document["supportsDisconnectedEditing"]!.GetValue<bool>());
        Assert.EndsWith(",Sync", document["capabilities"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(document["syncCapabilities"]!["supportsPerReplicaSync"]!.GetValue<bool>());
        Assert.False(document["syncCapabilities"]!["supportsRollbackOnFailure"]!.GetValue<bool>());
        Assert.True(document["syncCapabilities"]!["supportsPerLayerSync"]!.GetValue<bool>());
        Assert.True(document["syncCapabilities"]!["supportsSyncModelNone"]!.GetValue<bool>());
    }

    [Fact]
    public void Only_a_hosted_service_that_is_not_a_view_may_be_sync_enabled()
    {
        Assert.Null(SyncFlags.Refusal(Service()));
        Assert.True(SyncFlags.IsOn(Service()));
        Assert.Contains("not hosted", SyncFlags.Refusal(Service(hosted: false)), StringComparison.Ordinal);
        Assert.False(SyncFlags.IsOn(Service(hosted: false)));
        Assert.Contains("view", SyncFlags.Refusal(Service(view: true)), StringComparison.Ordinal);
        Assert.False(SyncFlags.IsOn(Service(sync: false)));
    }

    [Fact]
    public void A_layer_document_is_read_into_the_ports_layer()
    {
        OfflineLayer layer = Shape();

        Assert.Equal("Hydrants", layer.Name);
        Assert.Equal("esriGeometryPoint", layer.GeometryType);
        Assert.Equal(GeometryOrdinates.Z, layer.Ordinates);
        Assert.Equal(3857, layer.Wkid);
        Assert.False(layer.Geographic);
        Assert.Equal("globalid", layer.GlobalIdField);
        Assert.True(layer.HasAttachments);
        Assert.Equal(["objectid", "globalid", "status", "label", "created_user"], layer.Fields.Select(f => f.Name));

        // The writer writes a domain's name as given, so it arrives prefixed as ArcGIS's files name them.
        OfflineCodedDomain domain = Assert.IsType<OfflineCodedDomain>(layer.Fields[2].Domain);
        Assert.Equal("Hydrants_Status", domain.Name);
        Assert.Equal(1L, domain.Codes[0].Key);
    }

    [Fact]
    public void Values_arrive_as_the_port_carries_them()
    {
        Assert.Equal(7L, OfflineReplicaShapes.Normalise(7, "esriFieldTypeOID"));
        Assert.Equal(7, OfflineReplicaShapes.Normalise(7L, "esriFieldTypeInteger"));
        Assert.Equal((short)1, OfflineReplicaShapes.Normalise(true, "esriFieldTypeSmallInteger"));
        Assert.Equal(1.5, OfflineReplicaShapes.Normalise(1.5m, "esriFieldTypeDouble"));
        Assert.Equal(A, OfflineReplicaShapes.Normalise("{" + A + "}", "esriFieldTypeGlobalID"));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero),
            OfflineReplicaShapes.Normalise(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), "esriFieldTypeDate"));
        Assert.Null(OfflineReplicaShapes.Normalise(DBNull.Value, "esriFieldTypeString"));
    }

    [Fact]
    public void A_status_and_a_replica_are_in_ArcGISs_shapes()
    {
        ReplicaJob job = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "esriReplicaResponseTypeData",
            DateTimeOffset.FromUnixTimeMilliseconds(1794286119000))
        {
            Status = "Completed",
            ReplicaName = "graticula_test_cap1",
            ReplicaId = Guid.Parse("d2b16ae0-c3b5-48e7-9300-0445085cf669"),
            Generations = [new ReplicaLayer(0, 1794286130989, "useFilter", "", true)],
        };

        JsonObject status = OfflineReplicaShapes.Status(job, "https://example/replicafiles/x");

        Assert.Equal("D2B16AE0-C3B5-48E7-9300-0445085CF669", status["replicaID"]!.GetValue<string>());
        Assert.Equal("esriTransportTypeUrl", status["transportType"]!.GetValue<string>());
        Assert.Equal(1794286130989, status["layerServerGens"]![0]!["serverGen"]!.GetValue<long>());
        Assert.Equal("https://example/replicafiles/x", status["resultUrl"]!.GetValue<string>());

        JsonObject replica = OfflineReplicaShapes.Replica(new ReplicaRecord(
            job.ReplicaId!.Value, "graticula_test_cap1", Guid.NewGuid(), Guid.NewGuid(), "worker", "perLayer",
            job.Generations, true, "bidirectional", """{"xmin":1,"ymin":2,"xmax":3,"ymax":4}""", 3857,
            DateTimeOffset.FromUnixTimeMilliseconds(1794286130989), DateTimeOffset.FromUnixTimeMilliseconds(1794286130989)));

        Assert.Equal("worker", replica["replicaOwner"]!.GetValue<string>());
        Assert.Equal("perLayer", replica["syncModel"]!.GetValue<string>());
        Assert.Equal(1794286130989, replica["lastSyncDate"]!.GetValue<long>());
        Assert.Equal(1.0, replica["geometry"]!["xmin"]!.GetValue<double>());
        Assert.Equal(3857, replica["spatialReference"]!["wkid"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------------------------------------ download

    [Fact]
    public void Two_moments_of_a_layer_become_inserts_updates_and_deletes()
    {
        Dictionary<long, OfflineRow> then = new() { [1] = Row(1, A, "kept"), [2] = Row(2, B, "gone") };
        Dictionary<long, OfflineRow> now = new() { [1] = Row(1, A, "changed"), [3] = Row(3, C, "new") };

        ReplicaChanges changes = OfflineReplicaShapes.Diff(then, now, globalIdIndex: 1);

        Assert.Equal("new", Assert.Single(changes.Inserts).Values[3]);
        Assert.Equal("changed", Assert.Single(changes.Updates).Values[3]);

        // A delete is named by the GlobalID the row had then, since it has none now.
        Assert.Equal(new OfflineKey(2, B), Assert.Single(changes.Deletes));
    }

    [Fact]
    public void A_download_leaves_out_the_rows_its_own_upload_named()
    {
        ReplicaChanges changes = new([Row(5, C, "added offline")], [Row(1, A, "edited offline"), Row(2, B, "edited on the server")],
            [new OfflineKey(4, Guid.Parse("98d673ab-14ff-4696-a620-2a5239786556"))]);

        ReplicaChanges sent = OfflineReplicaShapes.WithoutOwn(
            changes, globalIdIndex: 1, new HashSet<Guid> { C, A, Guid.Parse("98d673ab-14ff-4696-a620-2a5239786556") });

        Assert.Empty(sent.Inserts);
        Assert.Equal(B, Assert.Single(sent.Updates).Values[1]);
        Assert.Empty(sent.Deletes);
    }

    [Fact]
    public void An_area_sent_as_four_numbers_is_kept_as_JSON_with_its_reference()
    {
        Assert.Equal(
            """{"xmin":1,"ymin":2,"xmax":3.5,"ymax":4,"spatialReference":{"wkid":3857}}""",
            OfflineReplicaRequests.AreaJson("1,2,3.5,4", new Envelope(1, 2, 3.5, 4), 3857));
        Assert.Equal("""{"xmin":1}""", OfflineReplicaRequests.AreaJson("""{"xmin":1}""", new Envelope(1, 2, 3, 4), 3857));
        Assert.Null(OfflineReplicaRequests.AreaJson(null, null, 3857));
    }

    [Fact]
    public async Task The_history_is_walked_back_to_the_generation_through_pages_that_end_inside_one_moment()
    {
        DateTimeOffset since = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        // Six thousand changes in one transaction after the generation, then one before it.
        List<FeatureChange> changes = [.. Enumerable.Range(1, 6000)
            .Select(i => new FeatureChange(i, i, since.AddSeconds(5), "updated", "worker", false))];
        changes.Add(new FeatureChange(9999, 9999, since.AddSeconds(-1), "added", "worker", false));

        (HashSet<long> changed, bool emptied) = await OfflineReplicaEndpoints
            .ChangedSinceAsync(new History(changes), since, CancellationToken.None);

        Assert.Equal(6000, changed.Count);
        Assert.DoesNotContain(9999L, changed);
        Assert.False(emptied);
    }

    [Fact]
    public async Task A_truncate_since_the_generation_is_reported()
    {
        DateTimeOffset since = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        (_, bool emptied) = await OfflineReplicaEndpoints.ChangedSinceAsync(
            new History([new FeatureChange(1, 0, since.AddMinutes(1), "emptied", "worker", false)]), since, CancellationToken.None);

        Assert.True(emptied);
    }

    // ------------------------------------------------------------------------------------------------ upload

    [Fact]
    public async Task An_upload_read_by_the_writer_becomes_the_edits_applyEdits_takes_and_the_last_edit_wins()
    {
        OfflineLayer layer = Shape();
        Guid deletedOnServer = Guid.NewGuid();
        FakeWriter writer = new(new OfflineDelta(Guid.NewGuid(), 0,
        [
            new OfflineLayerChanges(
                layer,
                Inserts: [Row(-1, C, "added offline"), Row(-2, A, "sent twice")],
                Updates: [Row(1, B, "edited offline"), Row(4, deletedOnServer, "edited after the server deleted it")],
                Deletes: [new OfflineKey(2, A), new OfflineKey(5, deletedOnServer)],
                [], [], []),
        ], string.Empty));

        OfflineDelta delta = await writer.ReadDeltaAsync("upload.geodatabase", [layer], CancellationToken.None);
        OfflineLayerChanges changes = Assert.Single(delta.Layers);

        Assert.Equal(new HashSet<Guid> { C, A, B, deletedOnServer }, OfflineReplicaShapes.GlobalIdsOf(changes));

        ReplicaLayerEdits edits = OfflineReplicaShapes.Edits(
            changes, OfflineReplicaShapes.EditableFields(LayerDocument), new HashSet<Guid> { A, B });

        JsonElement adds = JsonDocument.Parse(edits.Adds!).RootElement;
        JsonElement updates = JsonDocument.Parse(edits.Updates!).RootElement;

        // The insert the server already holds — the same upload sent twice — is an update, not a second row.
        Assert.Equal(1, adds.GetArrayLength());
        Assert.Equal(2, updates.GetArrayLength());
        Assert.Equal("{" + C.ToString().ToUpperInvariant() + "}", adds[0].GetProperty("attributes").GetProperty("globalid").GetString());

        // What the server keeps for itself is not sent: the object id and a column the document says is not editable.
        Assert.False(adds[0].GetProperty("attributes").TryGetProperty("objectid", out _));
        Assert.False(adds[0].GetProperty("attributes").TryGetProperty("created_user", out _));
        Assert.Equal(5.0, adds[0].GetProperty("geometry").GetProperty("z").GetDouble());

        // The row the server deleted stays deleted: its offline update and delete are dropped.
        Assert.Equal(2, edits.Dropped);
        Assert.Equal(["{" + A.ToString().ToUpperInvariant() + "}"], JsonSerializer.Deserialize<string[]>(edits.Deletes!)!);
    }

    [Theory]
    [InlineData(200, """[{"id":0,"addResults":[{"success":true}],"updateResults":[],"deleteResults":[]}]""", null)]
    [InlineData(200, """[{"id":0,"addResults":[{"success":false,"error":{"description":"bad value"}}]}]""", "bad value")]
    [InlineData(403, """{"error":{"code":403,"message":"not yours"}}""", "not yours")]
    public void An_applyEdits_answer_is_read_for_its_first_failure(int status, string body, string? said)
    {
        string? failure = OfflineReplicaEndpoints.EditFailure(status, Encoding.UTF8.GetBytes(body));

        if (said is null)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains(said, failure, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_attachments_GlobalID_is_the_same_every_time_and_different_for_every_attachment()
    {
        Guid layer = Guid.NewGuid();

        Assert.Equal(OfflineReplicaShapes.AttachmentGlobalId(layer, 7), OfflineReplicaShapes.AttachmentGlobalId(layer, 7));
        Assert.NotEqual(OfflineReplicaShapes.AttachmentGlobalId(layer, 7), OfflineReplicaShapes.AttachmentGlobalId(layer, 8));
        Assert.NotEqual(OfflineReplicaShapes.AttachmentGlobalId(layer, 7), OfflineReplicaShapes.AttachmentGlobalId(Guid.NewGuid(), 7));
    }

    // ------------------------------------------------------------------------------------------------ fakes

    /// <summary>A writer that writes nothing and reads back the delta it was given.</summary>
    private sealed class FakeWriter(OfflineDelta uploaded) : IMobileGeodatabaseWriter
    {
        public Task WriteAsync(OfflineCopy copy, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WriteDeltaAsync(OfflineDelta delta, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OfflineDelta> ReadDeltaAsync(string path, IReadOnlyList<OfflineLayer> layers, CancellationToken cancellationToken) =>
            Task.FromResult(uploaded);
    }

    /// <summary>A history held in memory, answering pages as the PostgreSQL one does: newest first, strictly before.</summary>
    private sealed class History(List<FeatureChange> changes) : IFeatureHistory
    {
        public Task<IReadOnlyList<FeatureChange>> ChangesAsync(DateTimeOffset? before, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeatureChange>>(
                [.. changes.Where(c => before is null || c.At < before).OrderByDescending(c => c.At).Take(limit)]);

        public Task<bool> IsOnAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<HistoryKept?> KeptAsync(CancellationToken cancellationToken) => Task.FromResult<HistoryKept?>(null);

        public Task<long> EnableAsync(CancellationToken cancellationToken) => Task.FromResult(0L);

        public Task DisableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> SetKeepAsync(int? days, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<FeatureVersion>> VersionsAsync(long objectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeatureVersion>>([]);

        public Task<RestoreOutcome> RestoreAsync(
            long objectId, long historyId, string editor, EditorTracking? tracking, CancellationToken cancellationToken) =>
            Task.FromResult(new RestoreOutcome(RestoreResult.NoSuchVersion, false));
    }
}
