using System;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Providers.MySql;
using Graticula.Providers.Oracle;
using Graticula.Providers.PostGis;
using Graticula.Providers.SqlServer;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-420, ADR-290 §12.4: offline sync is hosted-PostGIS only (ADR-288 §5.8), so no MySQL, Oracle or SQL Server attachment store
/// can reach <see cref="IAttachmentSync"/> — the request is refused before it touches a layer's attachments.
/// </summary>
/// <remarks>
/// <b>Two halves, each of which fails when the other is relaxed.</b> The refusal is <c>SyncFlags</c>: a service with any
/// layer that is not hosted cannot be sync-enabled, and <c>OfflineReplicaEndpoints.AdmitAsync</c> turns every replica request
/// away when <c>SyncFlags.IsOn</c> is false, before a replica, a layer, a history or an attachment store is resolved. The
/// other half is the types: none of the three engines' stores implements <see cref="IAttachmentSync"/>, so that the day the
/// refusal is lifted for one of them the build is not silently sending its layers' attachments nowhere.
/// </remarks>
public sealed class SyncReachesOnlyHostedPostGisAttachmentsTests
{
    private static PublishedLayer Layer(string name, bool hosted, string? attachmentsIn = null) => new(
        Guid.NewGuid(),
        new LayerDefinition(name, "public", name, "geom", 3857, "objectid", "objectid", isHosted: hosted),
        "postgis",
        "unused",
        GeometryKind.Point,
        owner: null,
        SharingScope.Private,
        ServiceStatus.Started,
        layerIndex: 0,
        serviceName: "S")
    {
        ServiceSyncEnabled = true,
        AttachmentsIn = attachmentsIn,
    };

    private static PublishedService Service(params PublishedLayer[] layers) => new(
        Guid.NewGuid(), "S", "folder", "FeatureServer", null, null, SharingScope.Private, ServiceStatus.Started, layers)
    {
        SyncEnabled = true,
    };

    [Theory]
    [InlineData(AttachmentPlaces.Source)]
    [InlineData(AttachmentPlaces.Datastore)]
    public void A_service_with_a_registered_layer_that_keeps_attachments_cannot_be_synchronised(string where)
    {
        PublishedService service = Service(Layer("hydrants", hosted: true), Layer("registered", hosted: false, where));

        string? refusal = SyncFlags.Refusal(service);

        Assert.NotNull(refusal);
        Assert.Contains("registered", refusal, StringComparison.Ordinal);
        Assert.Contains("not hosted", refusal, StringComparison.Ordinal);

        // The owner switched sync on, and it is still off: every replica request is turned away on this.
        Assert.False(SyncFlags.IsOn(service));
    }

    [Fact]
    public void A_service_of_hosted_layers_alone_is_offered_sync()
    {
        PublishedService service = Service(Layer("hydrants", hosted: true));

        Assert.Null(SyncFlags.Refusal(service));
        Assert.True(SyncFlags.IsOn(service));
    }

    [Fact]
    public void No_attachment_store_of_another_engine_implements_the_sync_interface()
    {
        foreach (Type store in (Type[])[typeof(MySqlAttachmentStore), typeof(OracleAttachmentStore), typeof(SqlServerAttachmentStore)])
        {
            Assert.True(typeof(IAttachmentStore).IsAssignableFrom(store), $"{store.Name} is not an attachment store any more.");
            Assert.False(typeof(IAttachmentSync).IsAssignableFrom(store), $"{store.Name} implements IAttachmentSync: D-420's reasoning no longer holds.");
        }

        Assert.True(typeof(IAttachmentSync).IsAssignableFrom(typeof(PostGisAttachmentStore)));
    }
}
