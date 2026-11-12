using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// ADR-288 §11a: <c>createReplica</c> with <c>dataFormat=json</c> answers ArcGIS's JSON replica in full. The shape is the one
/// measured on ArcGIS Server 12.1 (research note §5c); the rows come through the same reader a mobile geodatabase's do.
/// </summary>
public sealed class JsonReplicaTests
{
    private static readonly Guid A = Guid.Parse("774563db-ef14-47c7-bc0f-fc5d44d9beaf");
    private static readonly Guid B = Guid.Parse("fc59a070-0874-48f0-821f-1268acd139bf");

    private const string LayerDocument = """
        {
          "id": 0, "name": "points", "type": "Feature Layer", "geometryType": "esriGeometryPoint",
          "hasZ": true, "hasM": false, "objectIdField": "objectid", "globalIdField": "globalid", "hasAttachments": true,
          "extent": { "xmin": 1, "ymin": 2, "xmax": 3, "ymax": 4, "spatialReference": { "wkid": 3857 } },
          "fields": [
            { "name": "objectid", "type": "esriFieldTypeOID", "nullable": false, "editable": false },
            { "name": "globalid", "type": "esriFieldTypeGlobalID", "nullable": false, "editable": false, "length": 38 },
            { "name": "name", "type": "esriFieldTypeString", "nullable": true, "editable": true, "length": 50 },
            { "name": "seen", "type": "esriFieldTypeDate", "nullable": true, "editable": true },
            { "name": "size_mm", "type": "esriFieldTypeDouble", "nullable": true, "editable": true },
            { "name": "shape", "type": "esriFieldTypeGeometry" }
          ]
        }
        """;

    private static readonly DateTimeOffset Moment = DateTimeOffset.FromUnixTimeMilliseconds(1794465265186);

    private static PublishedService Service()
    {
        PublishedLayer Layer(int index) => new(
            Guid.NewGuid(),
            new LayerDefinition("points", "public", "points", "geom", 3857, "objectid", "objectid", isHosted: true),
            "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Private, ServiceStatus.Started,
            layerIndex: index, serviceName: "Hydrants")
        {
            ServiceSyncEnabled = true,
        };

        return new PublishedService(
            Guid.NewGuid(), "Hydrants", "hosted", "FeatureServer", null, null, SharingScope.Private, ServiceStatus.Started,
            [Layer(0), Layer(1)])
        {
            SyncEnabled = true,
        };
    }

    private static Func<string, string?> Fields(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> values = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => values.TryGetValue(name, out string? value) ? value : null;
    }

    private static CreateReplicaRequest Read(params (string Key, string Value)[] pairs)
    {
        Assert.True(OfflineReplicaRequests.TryReadCreate(Fields(pairs), Service(), out CreateReplicaRequest? request, out string? error), error);
        return request!;
    }

    private static string Refusal(params (string Key, string Value)[] pairs)
    {
        Assert.False(OfflineReplicaRequests.TryReadCreate(Fields(pairs), Service(), out _, out string? error));
        return error!;
    }

    // ------------------------------------------------------------------------------------------------ reading the request

    [Fact]
    public void A_json_request_is_read_with_its_registration_and_its_attachments()
    {
        CreateReplicaRequest request = Read(
            ("dataFormat", "json"), ("syncModel", "perLayer"), ("layers", "0"), ("returnAttachments", "true"), ("async", "true"));

        Assert.True(request.IsJson);
        Assert.True(request.Registered);
        Assert.True(request.ReturnAttachments);
        Assert.False(request.Embedded);
    }

    [Fact]
    public void A_missing_format_is_ArcGISs_default_json_and_is_read_as_json()
    {
        Assert.True(Read(("layers", "0")).IsJson);
    }

    [Fact]
    public void A_registration_that_names_its_layers_only_in_layerQueries_is_read_and_asks_for_no_rows()
    {
        // A registration that names its layers only in layerQueries; Field Maps's own registration is replicaOptions.registerExistingData (RegisterExistingDataTests).
        CreateReplicaRequest request = Read(
            ("dataFormat", "json"), ("syncModel", "perLayer"), ("returnAttachments", "false"),
            ("layerQueries", """{"0":{"queryOption":"none"},"1":{"queryOption":"none"}}"""));

        Assert.Equal([0, 1], request.Layers);
        Assert.All(request.Queries.Values, q => Assert.Equal("none", q.QueryOption));
        Assert.False(request.ReturnAttachments);
    }

    [Fact]
    public void An_embedded_json_replica_is_read_when_it_is_answered_at_once()
    {
        CreateReplicaRequest request = Read(("dataFormat", "json"), ("layers", "0"), ("transportType", "esriTransportTypeEmbedded"));

        Assert.True(request.Embedded);
    }

    [Fact]
    public void An_embedded_replica_that_is_asked_for_by_status_url_is_refused_by_name()
    {
        string said = Refusal(("dataFormat", "json"), ("layers", "0"), ("async", "true"), ("transportType", "esriTransportTypeEmbedded"));

        Assert.Contains("transportType 'esriTransportTypeEmbedded'", said, StringComparison.Ordinal);
        Assert.Contains("async 'true'", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mobile_geodatabase_cannot_be_embedded()
    {
        string said = Refusal(("dataFormat", "sqlite"), ("layers", "0"), ("transportType", "esriTransportTypeEmbedded"));

        Assert.Contains("transportType 'esriTransportTypeEmbedded'", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dataFormat", "filegdb")]
    [InlineData("syncModel", "sometimes")]
    [InlineData("transportType", "esriTransportTypeSmoke")]
    [InlineData("targetType", "server")]
    [InlineData("replicaName", "my replica")]
    [InlineData("attachmentsSyncDirection", "sideways")]
    [InlineData("layers", "9")]
    [InlineData("inSR", "banana")]
    public void Every_refusal_names_the_parameter_and_the_value_it_refused(string key, string value)
    {
        List<(string, string)> request = [("dataFormat", "json"), ("layers", "0")];
        request.RemoveAll(p => p.Item1 == key);
        request.Add((key, value));

        string said = Refusal([.. request]);

        Assert.Contains(key, said, StringComparison.Ordinal);
        Assert.Contains(value, said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("layerQueries", "[1]")]
    [InlineData("layerQueries", "not json")]
    [InlineData("geometry", "1,2,three,4")]
    public void A_value_that_does_not_parse_is_quoted_back(string key, string value)
    {
        string said = Refusal(("dataFormat", "json"), ("layers", "0"), (key, value));

        Assert.Contains(key, said, StringComparison.Ordinal);
        Assert.Contains(value, said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_download_reads_and_an_inline_or_uploaded_json_edit_is_refused_by_name()
    {
        Assert.True(OfflineReplicaRequests.TryReadSynchronize(
            Fields(("replicaID", A.ToString()), ("dataFormat", "json"), ("syncDirection", "download")),
            out SynchronizeReplicaRequest? request, out string? error), error);
        Assert.Equal("json", request!.DataFormat);

        Assert.False(OfflineReplicaRequests.TryReadSynchronize(
            Fields(("replicaID", A.ToString()), ("dataFormat", "json"), ("edits", """[{"id":0}]""")), out _, out error));
        Assert.Contains("edits", error, StringComparison.Ordinal);
        Assert.Contains("""[{"id":0}]""", error, StringComparison.Ordinal);

        Assert.False(OfflineReplicaRequests.TryReadSynchronize(
            Fields(("replicaID", A.ToString()), ("dataFormat", "json"), ("editsUploadID", "abc")), out _, out error));
        Assert.Contains("editsUploadID 'abc'", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ the file

    [Fact]
    public async Task A_json_replica_has_the_shape_ArcGIS_answered_with()
    {
        string file = Path.Combine(Path.GetTempPath(), "json-replica-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            CreateReplicaRequest request = Read(
                ("dataFormat", "json"), ("syncModel", "perLayer"), ("layers", "0"), ("returnAttachments", "true"), ("replicaName", "graticula_test_json_data"));
            PackagePlan plan = PlanOf(request, new Source(
                Features(
                    (1, A, "p0", Moment, 0.0, Point.Create(3100000, 4700000, 10.5, null)),
                    (2, B, "server_upd", Moment.AddHours(1), 555.5, Point.Create(3101000, 4700500, 11.5, null)))));
            Guid replicaId = Guid.Parse("D75D8B19-382F-4388-A388-AE8F0BDACAE8");
            List<ReplicaLayer> generations = [new ReplicaLayer(0, Moment.ToUnixTimeMilliseconds(), "useFilter", string.Empty, true)];

            await OfflineJsonReplica.WriteAsync(
                _ => new Attachments(new Dictionary<long, AttachmentInfo[]>
                {
                    [1] = [new AttachmentInfo(1, 1, "note.txt", "text/plain", null, 13, Moment)],
                    [2] = [new AttachmentInfo(2, 2, "server.txt", "text/plain", null, 15, Moment)],
                }),
                plan, replicaId, Moment, generations, "https://gis.test/rest/services/Hydrants/FeatureServer", file, CancellationToken.None);

            JsonElement root = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement;

            // The top level, in the order the capture has it.
            Assert.Equal(
                ["replicaName", "replicaID", "targetType", "transportType", "responseType", "syncModel", "layerServerGens", "layers"],
                root.EnumerateObject().Select(p => p.Name));
            Assert.Equal("graticula_test_json_data", root.GetProperty("replicaName").GetString());
            Assert.Equal("D75D8B19-382F-4388-A388-AE8F0BDACAE8", root.GetProperty("replicaID").GetString());
            Assert.Equal("client", root.GetProperty("targetType").GetString());
            Assert.Equal("esriTransportTypeUrl", root.GetProperty("transportType").GetString());
            Assert.Equal("esriReplicaResponseTypeData", root.GetProperty("responseType").GetString());
            Assert.Equal("perLayer", root.GetProperty("syncModel").GetString());
            Assert.Equal(0, root.GetProperty("layerServerGens")[0].GetProperty("id").GetInt32());
            Assert.Equal(1794465265186, root.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64());

            JsonElement layer = root.GetProperty("layers")[0];
            Assert.Equal(["id", "features", "attachments"], layer.EnumerateObject().Select(p => p.Name));

            JsonElement first = layer.GetProperty("features")[0];
            Assert.Equal(["geometry", "attributes"], first.EnumerateObject().Select(p => p.Name));
            Assert.Equal(3100000, first.GetProperty("geometry").GetProperty("x").GetDouble());
            Assert.Equal(4700000, first.GetProperty("geometry").GetProperty("y").GetDouble());
            Assert.Equal(10.5, first.GetProperty("geometry").GetProperty("z").GetDouble());

            JsonElement attributes = first.GetProperty("attributes");
            Assert.Equal(1, attributes.GetProperty("objectid").GetInt64());
            Assert.Equal("{774563DB-EF14-47C7-BC0F-FC5D44D9BEAF}", attributes.GetProperty("globalid").GetString());
            Assert.Equal("p0", attributes.GetProperty("name").GetString());
            Assert.Equal(1794465265186, attributes.GetProperty("seen").GetInt64());
            Assert.Equal(0.0, attributes.GetProperty("size_mm").GetDouble());
            Assert.False(attributes.TryGetProperty("shape", out _), "The geometry field is not an attribute.");

            Assert.Equal(2, layer.GetProperty("features").GetArrayLength());

            JsonElement attachment = layer.GetProperty("attachments")[0];
            Assert.Equal(
                ["attachmentId", "globalId", "parentGlobalId", "contentType", "size", "name", "url"],
                attachment.EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, attachment.GetProperty("attachmentId").GetInt32());
            Assert.Equal("{774563DB-EF14-47C7-BC0F-FC5D44D9BEAF}", attachment.GetProperty("parentGlobalId").GetString());
            Assert.Equal(OfflineReplicaShapes.AttachmentGlobalId(plan.Layers[0].Layer.Id, 1), Guid.Parse(attachment.GetProperty("globalId").GetString()!));
            Assert.Equal("text/plain", attachment.GetProperty("contentType").GetString());
            Assert.Equal(13, attachment.GetProperty("size").GetInt64());
            Assert.Equal("note.txt", attachment.GetProperty("name").GetString());

            // The url is the layer's own attachment route, which the replica's owner fetches with the token they hold.
            Assert.Equal("https://gis.test/rest/services/Hydrants/FeatureServer/0/1/attachments/1", attachment.GetProperty("url").GetString());
            Assert.Equal("https://gis.test/rest/services/Hydrants/FeatureServer/0/2/attachments/2", layer.GetProperty("attachments")[1].GetProperty("url").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_snapshot_has_no_replica_id_and_attachments_are_left_out_unless_asked_for()
    {
        string file = Path.Combine(Path.GetTempPath(), "json-replica-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            CreateReplicaRequest request = Read(("dataFormat", "json"), ("layers", "0"), ("syncModel", "none"));
            PackagePlan plan = PlanOf(request, new Source(Features((1, A, "p0", Moment, 1.0, Point.Create(1, 2, 3, null)))));

            await OfflineJsonReplica.WriteAsync(
                _ => throw new InvalidOperationException("No attachment store should be asked."),
                plan, null, Moment, OfflinePackager.GenerationsOf(plan, Moment), "https://gis.test/fs", file, CancellationToken.None);

            JsonElement root = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement;

            Assert.False(root.TryGetProperty("replicaID", out _));
            Assert.Equal("none", root.GetProperty("syncModel").GetString());
            Assert.Equal(1, root.GetProperty("layers")[0].GetProperty("features").GetArrayLength());
            Assert.Equal(0, root.GetProperty("layers")[0].GetProperty("attachments").GetArrayLength());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_layer_asked_for_with_queryOption_none_is_registered_with_no_features_and_no_attachments()
    {
        string file = Path.Combine(Path.GetTempPath(), "json-replica-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            CreateReplicaRequest request = Read(
                ("dataFormat", "json"), ("syncModel", "perLayer"), ("returnAttachments", "true"),
                ("layerQueries", """{"0":{"queryOption":"none"}}"""));
            PackagePlan plan = PlanOf(request, new Source(Features((1, A, "p0", Moment, 1.0, Point.Create(1, 2, 3, null)))));

            await OfflineJsonReplica.WriteAsync(
                _ => throw new InvalidOperationException("No attachment store should be asked."),
                plan, Guid.NewGuid(), Moment, OfflinePackager.GenerationsOf(plan, Moment), "https://gis.test/fs", file, CancellationToken.None);

            JsonElement layer = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement.GetProperty("layers")[0];

            Assert.Equal(0, layer.GetProperty("features").GetArrayLength());
            Assert.Equal(0, layer.GetProperty("attachments").GetArrayLength());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_json_download_states_adds_updates_and_deletes_with_the_generations_it_leaves()
    {
        string file = Path.Combine(Path.GetTempPath(), "json-changes-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            OfflineLayer shape = OfflineReplicaShapes.Layer(
                "Hydrants", 0, LayerDocument, "PROJCS[\"x\"]", _ => OfflineReplicaEndpoints.Empty<OfflineRow>(), _ => OfflineReplicaEndpoints.Empty<OfflineAttachment>());
            ReplicaRecord replica = new(
                Guid.Parse("D75D8B19-382F-4388-A388-AE8F0BDACAE8"), "graticula_test", Guid.NewGuid(), Guid.NewGuid(), "ada", "perLayer",
                [], true, "bidirectional", null, 3857, Moment, Moment);
            OfflineRow added = new([3L, A, "added", Moment, 2.5], Point.Create(5, 6, 7, null));
            OfflineRow changed = new([2L, B, "changed", Moment, 1.5], Point.Create(8, 9, 10, null));

            await OfflineJsonReplica.WriteChangesAsync(
                file, replica, [new OfflineLayerChanges(shape, [added], [changed], [new OfflineKey(7, Guid.NewGuid())], [], [], [])],
                [new ReplicaLayer(0, 99, "useFilter", string.Empty, true)], CancellationToken.None);

            JsonElement root = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement;

            Assert.Equal("esriReplicaResponseTypeEdits", root.GetProperty("responseType").GetString());
            Assert.Equal(99, root.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64());

            JsonElement edit = root.GetProperty("edits")[0];
            Assert.Equal(0, edit.GetProperty("id").GetInt32());
            Assert.Equal("added", edit.GetProperty("features").GetProperty("adds")[0].GetProperty("attributes").GetProperty("name").GetString());
            Assert.Equal(7, edit.GetProperty("features").GetProperty("adds")[0].GetProperty("geometry").GetProperty("z").GetDouble());
            Assert.Equal("changed", edit.GetProperty("features").GetProperty("updates")[0].GetProperty("attributes").GetProperty("name").GetString());
            Assert.Equal(7, edit.GetProperty("features").GetProperty("deleteIds")[0].GetInt64());
            Assert.Equal(0, edit.GetProperty("attachments").GetProperty("adds").GetArrayLength());
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ------------------------------------------------------------------------------------------------ registration

    [Fact]
    public async Task A_json_replica_is_registered_by_the_same_call_a_mobile_geodatabase_is()
    {
        CreateReplicaRequest request = Read(
            ("dataFormat", "json"), ("syncModel", "perLayer"), ("layers", "0"), ("replicaName", "reg_1"), ("returnAttachments", "true"),
            ("geometry", "1,2,3,4"), ("inSR", "3857"));
        PackagePlan plan = PlanOf(request, new Source([]));
        Store store = new();
        Guid id = Guid.NewGuid();
        ReplicaOwner owner = new(Guid.NewGuid(), "ada");

        await OfflinePackager.RegisterAsync(store, plan, id, owner, OfflinePackager.GenerationsOf(plan, Moment), Moment, CancellationToken.None);

        ReplicaRecord stored = Assert.Single(store.Created);
        Assert.Equal(id, stored.Id);
        Assert.Equal("reg_1", stored.Name);
        Assert.Equal("perLayer", stored.SyncModel);
        Assert.Equal(owner.Id, stored.OwnerId);
        Assert.Equal(plan.Service.Id, stored.ServiceId);
        Assert.True(stored.ReturnAttachments);
        Assert.Equal(3857, stored.Wkid);
        Assert.Equal(Moment.ToUnixTimeMilliseconds(), Assert.Single(stored.Layers).ServerGeneration);
    }

    // ------------------------------------------------------------------------------------------------ fakes

    private static PackagePlan PlanOf(CreateReplicaRequest request, Source source)
    {
        PublishedService service = Service();
        List<OfflineReplicaEndpoints.Planned> layers = [];

        foreach (int id in request.Layers)
        {
            LayerDescription described = new(
                [
                    new FieldDescription("objectid", FieldType.Integer, false, null),
                    new FieldDescription("globalid", FieldType.Guid, false, null),
                    new FieldDescription("name", FieldType.Text, true, 50),
                    new FieldDescription("seen", FieldType.Date, true, null),
                    new FieldDescription("size_mm", FieldType.Double, true, null),
                ],
                null);

            OfflineReplicaEndpoints.Filter filter = OfflineReplicaEndpoints.Filter.Of(request.Queries[id], request, described, out _)!;
            layers.Add(new OfflineReplicaEndpoints.Planned(id, service.LayerOrTable(id)!, source, described, LayerDocument, filter));
        }

        return new PackagePlan(service, request, "{}", layers, new Dictionary<int, string> { [3857] = "PROJCS[\"x\"]" });
    }

    private static List<Feature> Features(params (long Oid, Guid Gid, string Name, DateTimeOffset Seen, double Size, Geometry Geometry)[] rows)
    {
        FeatureSchema schema = new(["objectid", "globalid", "name", "seen", "size_mm"]);
        return [.. rows.Select(r => new Feature(r.Oid.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Geometry, schema,
            [r.Oid, r.Gid, r.Name, r.Seen, r.Size]))];
    }

    private sealed class Source(List<Feature> features) : IFeatureSource
    {
        public FeatureSchema SchemaFor(FeatureQuery query) => FeatureSchema.Empty;

        public async IAsyncEnumerable<Feature> ReadAsync(FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            // One page: the reader stops when a page is short.
            foreach (Feature feature in features.Skip(query.Offset).Take(query.Limit))
            {
                yield return feature;
            }
        }

        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> CountUpToAsync(FeatureQuery query, long ceiling, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Attachments(Dictionary<long, AttachmentInfo[]> byFeature) : IAttachmentStore
    {
        public Task<IReadOnlyList<AttachmentInfo>> ListAsync(long featureId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AttachmentInfo>>(byFeature.TryGetValue(featureId, out AttachmentInfo[]? found) ? found : []);

        public Task<OpenAttachment?> OpenAsync(int attachmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> AddAsync(long featureId, string name, string contentType, string? declaredContentType, Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(long featureId, int attachmentId, string name, string contentType, string? declaredContentType, Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<int>> DeleteAsync(IReadOnlyList<int> attachmentIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AttachmentUsage> UsageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store : IOfflineSyncStore
    {
        public List<ReplicaRecord> Created { get; } = [];

        public Task<int> SetEnabledAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task CreateAsync(ReplicaRecord replica, CancellationToken cancellationToken)
        {
            Created.Add(replica);
            return Task.CompletedTask;
        }

        public Task<ReplicaRecord?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Created.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<ReplicaRecord>> ListAsync(Guid serviceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReplicaRecord>>(Created);

        public Task<bool> AdvanceAsync(Guid id, IReadOnlyList<ReplicaLayer> layers, DateTimeOffset synced, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Created.RemoveAll(r => r.Id == id) > 0);
    }
}
