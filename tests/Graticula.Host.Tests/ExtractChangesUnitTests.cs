using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Webhooks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-311's units: the <c>extractChanges</c> parameter reader, change tracking's switch rules and document flags, and a
/// webhook's <c>changesUrl</c>. The operation itself is tested against a running server in the conformance suite
/// (<c>ExtractChangesTests</c>, <c>WebhooksTests</c>).
/// </summary>
public sealed class ExtractChangesUnitTests
{
    private static PublishedService Service(bool hosted = true, bool tracking = true, Guid? viewOf = null)
    {
        PublishedLayer Layer(int index) => new(
            Guid.NewGuid(),
            new LayerDefinition("points" + index, "public", "points" + index, "geom", 3857, "objectid", "objectid", isHosted: hosted),
            "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Private, ServiceStatus.Started,
            layerIndex: index, serviceName: "Hydrants")
        {
            ServiceChangeTracking = tracking,
        };

        return new PublishedService(
            Guid.NewGuid(), "Hydrants", "hosted", "FeatureServer", null, null, SharingScope.Private, ServiceStatus.Started,
            [Layer(0), Layer(2)])
        {
            ChangeTracking = tracking,
            ViewOf = viewOf,
        };
    }

    private static Func<string, string?> Fields(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> values = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => values.TryGetValue(name, out string? value) ? value : null;
    }

    private static ExtractChangesRequest Read(params (string Key, string Value)[] pairs)
    {
        Assert.True(ExtractChangesRequest.TryRead(Fields(pairs), Service(), 3857, out ExtractChangesRequest? request, out string? error), error);
        return request!;
    }

    private static string Refusal(params (string Key, string Value)[] pairs)
    {
        Assert.False(ExtractChangesRequest.TryRead(Fields(pairs), Service(), 3857, out _, out string? error));
        return error!;
    }

    [Fact]
    public void Defaults_are_the_references_and_omitted_layers_are_every_layer()
    {
        ExtractChangesRequest request = Read(("serverGens", "1000"));

        Assert.Equal([0, 2], request.Layers);
        Assert.All(request.Layers, id => Assert.Equal(new ExtractWindow(1000, null), request.Windows[id]));
        Assert.True(request.ReturnInserts && request.ReturnUpdates && request.ReturnDeletes);
        Assert.True(request.Attachments && request.AttachmentsByUrl);
        Assert.False(request.IdsOnly || request.ExtentOnly || request.HasGeometryUpdates || request.DeletedFeatures || request.Async);
        Assert.Null(request.FieldsToCompare);
        Assert.Null(request.Area);
        Assert.Equal(ReplicaLayerQuery.Default, request.Queries[0]);
    }

    [Fact]
    public void Generations_are_one_value_a_pair_or_per_layer()
    {
        Assert.Equal(new ExtractWindow(5, 9), Read(("serverGens", "[5,9]"), ("layers", "0")).Windows[0]);
        Assert.Equal(new ExtractWindow(5, 9), Read(("serverGens", "5,9"), ("layers", "[0]")).Windows[0]);

        // layerServerGens alone names the layers; minServerGen sent back is read and ignored.
        ExtractChangesRequest perLayer = Read(("layerServerGens", """[{"id":2,"minServerGen":1,"serverGen":7}]"""));
        Assert.Equal([2], perLayer.Layers);
        Assert.Equal(new ExtractWindow(7, null), perLayer.Windows[2]);

        // Both: a layer's own generation wins, serverGens covers the rest.
        ExtractChangesRequest both = Read(("layerServerGens", """[{"id":2,"serverGen":7}]"""), ("serverGens", "3"));
        Assert.Equal(new ExtractWindow(3, null), both.Windows[0]);
        Assert.Equal(new ExtractWindow(7, null), both.Windows[2]);

        Assert.Contains("serverGens or layerServerGens is required", Refusal(("layers", "0")), StringComparison.Ordinal);
        Assert.Contains("Send the smaller first", Refusal(("serverGens", "[9,5]")), StringComparison.Ordinal);
        Assert.Contains("not one generation", Refusal(("serverGens", "[1,2,3]")), StringComparison.Ordinal);
        Assert.Contains("not a layer or table", Refusal(("layerServerGens", """[{"id":1,"serverGen":7}]""")), StringComparison.Ordinal);
        Assert.Contains("not a layer or table", Refusal(("serverGens", "1"), ("layers", "1")), StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_not_served_is_refused_by_name()
    {
        Assert.Contains("dataFormat 'sqlite'", Refusal(("serverGens", "1"), ("dataFormat", "sqlite")), StringComparison.Ordinal);
        Assert.Contains("dataFormat 'sqllite'", Refusal(("serverGens", "1"), ("dataFormat", "sqllite")), StringComparison.Ordinal);
        Assert.Contains("changesExtentGridCell", Refusal(("serverGens", "1"), ("changesExtentGridCell", "large")), StringComparison.Ordinal);
        Assert.Contains("includeRelated", Refusal(("serverGens", "1"), ("layerQueries", """{"0":{"includeRelated":true}}""")), StringComparison.Ordinal);
        Assert.Contains("esriGeometryPolyline", Refusal(("serverGens", "1"), ("geometryType", "esriGeometryPolyline")), StringComparison.Ordinal);
        Assert.Contains("returnIdsOnly and returnExtentOnly", Refusal(("serverGens", "1"), ("returnIdsOnly", "true"), ("returnExtentOnly", "true")), StringComparison.Ordinal);
        Assert.Contains("returnHasGeometryUpdates", Refusal(("serverGens", "1"), ("returnHasGeometryUpdates", "true"), ("returnUpdates", "false")), StringComparison.Ordinal);
        Assert.Contains("fieldsToCompare needs returnUpdates", Refusal(("serverGens", "1"), ("fieldsToCompare", """{"fields":["a"]}"""), ("returnUpdates", "false")), StringComparison.Ordinal);
        Assert.Contains("returnDeletedFeatures", Refusal(("serverGens", "1"), ("returnDeletedFeatures", "true"), ("returnIdsOnly", "true")), StringComparison.Ordinal);

        // changesExtentGridCell=none and dataFormat=json are what is served.
        Assert.True(Read(("serverGens", "1"), ("changesExtentGridCell", "none"), ("dataFormat", "JSON")).ReturnInserts);
    }

    [Fact]
    public void Filters_and_answers_are_read_as_asked()
    {
        ExtractChangesRequest request = Read(
            ("serverGens", "1"),
            ("layers", "0,2"),
            ("layerQueries", """{"2":{"queryOption":"useFilter","where":"name = 'x'","useGeometry":false}}"""),
            ("geometry", "1,2,3,4"),
            ("inSR", "4326"),
            ("outSR", """{"wkid":102100}"""),
            ("returnInserts", "false"),
            ("returnIdsOnly", "true"),
            ("returnAttachments", "false"),
            ("fieldsToCompare", """{"fields":["name","size"]}"""),
            ("async", "true"));

        Assert.Equal(new ReplicaLayerQuery("useFilter", "name = 'x'", false), request.Queries[2]);
        Assert.Equal(ReplicaLayerQuery.Default, request.Queries[0]);
        Assert.Equal(4326, request.AreaSrid);
        Assert.Equal(new Envelope(1, 2, 3, 4), request.Area!.Envelope);
        Assert.Equal(3857, request.OutSrid);
        Assert.False(request.ReturnInserts);
        Assert.True(request.IdsOnly && request.Async);
        Assert.False(request.Attachments);
        Assert.Equal(["name", "size"], request.FieldsToCompare);
    }

    [Fact]
    public void Change_tracking_is_had_only_where_history_can_be_and_honoured_only_while_every_layer_keeps_it()
    {
        Assert.Null(ChangeTrackingFlags.Refusal(Service()));
        Assert.Contains("not hosted", ChangeTrackingFlags.Refusal(Service(hosted: false)), StringComparison.Ordinal);
        Assert.Contains("A view's rows", ChangeTrackingFlags.Refusal(Service(viewOf: Guid.NewGuid())), StringComparison.Ordinal);

        PublishedService service = Service();
        LayerDescription archived = Described(archived: true);
        LayerDescription stopped = Described(archived: false);

        Assert.True(ChangeTrackingFlags.IsOn(service, [archived, archived]));
        Assert.False(ChangeTrackingFlags.IsOn(service, [archived, stopped]));
        Assert.False(ChangeTrackingFlags.IsOn(Service(tracking: false), [archived, archived]));

        Assert.Contains("no longer keeps its history",
            ChangeTrackingFlags.NotHonoured(service, [(service.LayersAndTables[0], archived), (service.LayersAndTables[1], stopped)]),
            StringComparison.Ordinal);
        Assert.Contains("Change tracking is not enabled",
            ChangeTrackingFlags.NotHonoured(Service(tracking: false), []), StringComparison.Ordinal);
        Assert.Null(ChangeTrackingFlags.NotHonoured(service, [(service.LayersAndTables[0], archived), (service.LayersAndTables[1], archived)]));

        // Said to a caller who may query, once.
        Assert.Equal("Query,Create,ChangeTracking", ChangeTrackingFlags.WithChangeTracking("Query,Create"));
        Assert.Equal("Query,ChangeTracking", ChangeTrackingFlags.WithChangeTracking("Query,ChangeTracking"));
        Assert.Equal("Create", ChangeTrackingFlags.WithChangeTracking("Create"));
    }

    [Fact]
    public void A_webhooks_changes_url_is_arcgis_shape_with_the_flags_each_change_type_gives()
    {
        const string Service = "https://gis.example.com/rest/services/hosted/roads/FeatureServer";

        Assert.Equal(
            Service + "/extractChanges?layers=[2]&serverGens=[10,20]&async=true&returnUpdates=false&returnDeletes=false&returnAttachments=false&f=json",
            WebhookEvents.ChangesUrl(Service, 2, (10, 20), ["FeaturesCreated"]));
        Assert.Equal(
            Service + "/extractChanges?layers=[0]&serverGens=[10,20]&async=true&returnDeletes=false&returnAttachments=false&f=json",
            WebhookEvents.ChangesUrl(Service, 0, (10, 20), ["FeaturesUpdated"]));
        Assert.Equal(
            Service + "/extractChanges?layers=[0]&serverGens=[10,20]&async=true&returnInserts=false&returnUpdates=false&returnAttachments=false&f=json",
            WebhookEvents.ChangesUrl(Service, 0, (10, 20), ["FeaturesDeleted"]));
        Assert.Equal(
            Service + "/extractChanges?layers=[0]&serverGens=[10,20]&async=true&returnInserts=false&returnUpdates=false&returnDeletes=false&f=json",
            WebhookEvents.ChangesUrl(Service, 0, (10, 20), ["AttachmentsCreated"]));

        // An edit that is several keeps only the flags all of them give.
        Assert.Equal(
            Service + "/extractChanges?layers=[0]&serverGens=[10,20]&async=true&returnAttachments=false&f=json",
            WebhookEvents.ChangesUrl(Service, 0, (10, 20), ["FeaturesCreated", "FeaturesUpdated", "FeaturesDeleted"]));

        // A change that is not of features or attachments has none.
        Assert.Null(WebhookEvents.ChangesUrl(Service, 0, (10, 20), ["LayerSchemaChanged"]));
        Assert.Null(WebhookEvents.ChangesUrl(Service, 0, (10, 20), []));
    }

    [Fact]
    public void The_window_starts_before_the_change_and_ends_after_it_whatever_the_sub_millisecond()
    {
        DateTimeOffset began = DateTimeOffset.FromUnixTimeMilliseconds(1000).AddTicks(9_999);
        DateTimeOffset committed = DateTimeOffset.FromUnixTimeMilliseconds(1004).AddTicks(1);

        Assert.Equal((1000L, 1005L), WebhookEvents.Window(began, committed));
        Assert.Equal((1000L, 1004L), WebhookEvents.Window(began, DateTimeOffset.FromUnixTimeMilliseconds(1004)));
        Assert.Equal((1000L, 1000L), WebhookEvents.Window(DateTimeOffset.FromUnixTimeMilliseconds(1000), DateTimeOffset.FromUnixTimeMilliseconds(1000)));
    }

    [Fact]
    public void A_tracked_edit_payload_carries_changes_url_beside_its_ids_and_an_untracked_one_does_not()
    {
        EditOutcome outcome = new([EditResult.Ok(5)], [], [], false);
        WebhookEvents.ServiceEvent tracked = WebhookEvents.FromEdit("roads", "hosted", 2, outcome, DateTimeOffset.UnixEpoch, (100, 105))!;
        (byte[] bytes, _) = WebhookPayload.Compose(Hook(), Guid.NewGuid(), tracked.Body.ToJsonString(), DateTimeOffset.UnixEpoch);

        using JsonDocument payload = JsonDocument.Parse(bytes);
        JsonElement one = payload.RootElement[0];
        Assert.Equal(
            "https://gis.example.com/rest/services/hosted/roads/FeatureServer/extractChanges?layers=[2]&serverGens=[100,105]&async=true"
            + "&returnUpdates=false&returnDeletes=false&returnAttachments=false&f=json",
            one.GetProperty("changesUrl").GetString());
        Assert.Contains("objectIds=5", one.GetProperty("queryUrl").GetString(), StringComparison.Ordinal);
        Assert.False(one.TryGetProperty(WebhookEvents.ServerGensKey, out _));

        WebhookEvents.ServiceEvent untracked = WebhookEvents.FromEdit("roads", "hosted", 2, outcome, DateTimeOffset.UnixEpoch)!;
        (byte[] plain, _) = WebhookPayload.Compose(Hook(), Guid.NewGuid(), untracked.Body.ToJsonString(), DateTimeOffset.UnixEpoch);
        using JsonDocument without = JsonDocument.Parse(plain);
        Assert.False(without.RootElement[0].TryGetProperty("changesUrl", out _));
    }

    [Fact]
    public void Two_values_of_a_field_are_compared_by_content()
    {
        Assert.True(OfflineReplicaEndpoints.SameValue(new byte[] { 1, 2 }, new byte[] { 1, 2 }));
        Assert.False(OfflineReplicaEndpoints.SameValue(new byte[] { 1, 2 }, new byte[] { 2, 1 }));
        Assert.True(OfflineReplicaEndpoints.SameValue("a", "a"));
        Assert.False(OfflineReplicaEndpoints.SameValue(1L, null));
        Assert.Equal(string.Empty, OfflineReplicaEndpoints.GeometryText(null, 3857));
        Assert.NotEqual(
            OfflineReplicaEndpoints.GeometryText(new Point(1, 2), 3857),
            OfflineReplicaEndpoints.GeometryText(new Point(1, 3), 3857));
    }

    private static LayerDescription Described(bool archived) => new([], null)
    {
        Archived = archived,
        ArchivedSince = archived ? DateTimeOffset.UnixEpoch : null,
    };

    private static WebhookDefinition Hook() => new(
        Guid.NewGuid(), WebhookKind.FeatureService, Guid.NewGuid(), 1,
        "hook", "https://receiver.example.com/hook", ["*"], HasSecret: false, Active: true, Guid.NewGuid(), "ci", null,
        "https://gis.example.com", "application/json", "json", 3, 5, 5, "{}", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
