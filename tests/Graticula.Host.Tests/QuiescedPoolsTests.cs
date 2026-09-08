using System;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A quiesced source hands out nothing, whichever path asks for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md), and this is the hole that was
/// in it for an hour.</b> The gate went into <c>BudgetedFeatureSource</c>, which is the *read*
/// path. Writes, tiles and attachments each took the pool directly from <c>LayerConnections</c>,
/// so a quiesced source still accepted <c>applyEdits</c> — and an edit runs in a transaction,
/// which is precisely the thing that blocks a DBA's <c>ALTER TABLE</c>. The feature would have
/// failed at its own job while reporting success.
/// </para>
/// <para>
/// <b>And <c>GetOrAdd</c> would have rebuilt the pool the quiesce had just closed</b>, so the
/// next tile request undid the operator's instruction with the traffic it was meant to stop.
/// </para>
/// <para>
/// <b>Tested here rather than over HTTP, and the first attempt is why.</b> A conformance test
/// asked for tile 0/0/0 of a fixture layer and got <c>204</c> — the tile is empty and the
/// pipeline answers without touching the database, so the test proved nothing about the gate. The
/// hand-out is what was wrong; the hand-out is what this asks.
/// </para>
/// </remarks>
public sealed class QuiescedPoolsTests
{
    private const string Connection = "Host=localhost;Port=1;Database=nothing";

    /// <summary>
    /// Every way of reaching a source refuses while it is out of service.
    /// </summary>
    /// <remarks>
    /// <b>Enumerated rather than sampled.</b> The defect was that three of four paths had no gate,
    /// so a test covering one of them would have passed on the broken version.
    /// </remarks>
    [Fact]
    public void No_path_hands_out_a_quiesced_source()
    {
        SourceQuiesce quiesce = new();
        using LayerConnections connections = new(new ConnectionBudget(0, 0), new SourceBreaker(), quiesce);

        PublishedLayer layer = Layer();

        quiesce.Hold(Connection, "erdinc", TimeSpan.FromMinutes(5), "a schema change");

        // The read path, which had the gate from the start.
        Assert.Throws<SourceQuiescedException>(() => connections.SourceFor(layer));

        // The three that did not.
        Assert.Throws<SourceQuiescedException>(() => connections.WriterFor(layer, []));
        Assert.Throws<SourceQuiescedException>(() => connections.TileSourceFor(layer, []));
        Assert.Throws<SourceQuiescedException>(() => connections.AttachmentsFor(layer));
    }

    /// <summary>
    /// And every one of them works again once the source is resumed.
    /// </summary>
    /// <remarks>
    /// <b>A refusal that did not end would be an outage rather than a window.</b> Nothing here
    /// connects — building a pool is lazy — so this asserts the gate opens rather than that the
    /// database is reachable, which is the part that belongs to a test with a database.
    /// </remarks>
    [Fact]
    public void Resuming_lets_every_path_through_again()
    {
        SourceQuiesce quiesce = new();
        using LayerConnections connections = new(new ConnectionBudget(0, 0), new SourceBreaker(), quiesce);

        PublishedLayer layer = Layer();

        quiesce.Hold(Connection, "erdinc", TimeSpan.FromMinutes(5), null);
        Assert.True(quiesce.Resume(Connection));

        Assert.NotNull(connections.SourceFor(layer));
        Assert.NotNull(connections.WriterFor(layer, []));
        Assert.NotNull(connections.TileSourceFor(layer, []));
        Assert.NotNull(connections.AttachmentsFor(layer));
    }

    /// <summary>A layer over the connection this test quiesces.</summary>
    private static PublishedLayer Layer() =>
        new(
            Guid.NewGuid(),
            new LayerDefinition("roads", "public", "roads", "geom", 3857, "id", "objectid", false),
            "source",
            Connection,
            GeometryKind.Polygon,
            null,
            SharingScope.Organization,
            ServiceStatus.Started);
}
