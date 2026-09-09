using System;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The capability string is what the caller may do intersected with what the store will take,
/// and it used to be the first of those alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-231](../../docs/architecture-debt.md)'s writing half, repaired 2026-09-10.</b>
/// <c>PrivilegedCapabilities</c> reads the caller's privileges and asked the database nothing,
/// so a layer over a materialized view or a join view — relations PostgreSQL refuses every
/// write to — advertised <c>Query,Create,Update,Delete,Editing</c>, and every edit then failed
/// with <c>42809</c> or <c>55000</c>. Measured over HTTP on a fixture the same day: four
/// layers, four kinds, all four advertising editing and two of them accepting none of it.
/// </para>
/// <para>
/// <b>Advertising an operation the store will refuse is
/// [ADR-008](../../docs/adr/ADR-008-query-engine.md) §2 broken outright</b> — <em>never degrade
/// silently</em> — because the capability report exists precisely so a client does not have to
/// discover a limit by hitting it. That is why this half is a defect and not the product
/// decision beside it, which is whether a non-updatable relation should be refused at publish,
/// published read-only, or published with a warning.
/// </para>
/// <para>
/// <b>Here rather than in the conformance suite, and that is a trade recorded rather than
/// hidden.</b> Driving this end to end needs a running server, a registered data source and a
/// materialized view built by hand — a test that would be run once. What the conformance suite
/// would add over this is the plumbing between the describe and the document, and
/// <c>TheDescribedShapeSaysWhetherTheDatabaseWillTakeAWriteTests</c> holds the other end of
/// that plumbing against a real PostgreSQL.
/// </para>
/// </remarks>
public sealed class TheLayerDocumentDoesNotOfferAnEditTheDatabaseRefusesTests
{
    /// <summary>A relation the database will not write to is advertised read-only.</summary>
    [Fact]
    public void A_relation_the_database_refuses_is_advertised_query_only()
    {
        string capabilities = Program.CapabilitiesFor(
            Editing(), Layer(), ServiceCapabilityLimits.Unset, writable: false);

        Assert.Equal("Query", capabilities);
    }

    /// <summary>And a relation it will write to keeps everything the caller holds.</summary>
    /// <remarks>
    /// <b>The half that makes the other half worth having.</b> A narrowing that also fired on
    /// ordinary tables would take editing away from every layer on the server, which is a
    /// worse failure than the one being repaired and would look, from the document, exactly
    /// like an operator having configured the service read-only.
    /// </remarks>
    [Fact]
    public void A_relation_the_database_accepts_still_advertises_editing()
    {
        string capabilities = Program.CapabilitiesFor(
            Editing(), Layer(), ServiceCapabilityLimits.Unset, writable: true);

        Assert.Equal("Query,Create,Update,Delete,Editing", capabilities);
    }

    /// <summary>
    /// An answer nobody could get does not take a capability away.
    /// </summary>
    /// <remarks>
    /// <b><c>is false</c>, not <c>is not true</c>.</b> Null means nothing asked — a surface
    /// holding no shape, or a store that could not be reached — and narrowing on an absence
    /// would make an outage indistinguishable, in the document, from a deliberate setting.
    /// [ADR-008](../../docs/adr/ADR-008-query-engine.md) §2 is a rule against over-claiming;
    /// answering an under-claim to a question nobody asked is a different mistake, not a
    /// cautious version of the same one.
    /// </remarks>
    [Fact]
    public void An_unasked_question_does_not_narrow_anything()
    {
        string capabilities = Program.CapabilitiesFor(
            Editing(), Layer(), ServiceCapabilityLimits.Unset, writable: null);

        Assert.Equal("Query,Create,Update,Delete,Editing", capabilities);
    }

    /// <summary>
    /// A writable relation does not undo the service's configured ceiling.
    /// </summary>
    /// <remarks>
    /// <b>ADR-031: configuration only ever restricts.</b> The three inputs each remove and
    /// none adds, so a relation that takes writes still cannot put back what an operator turned
    /// off — [D-179](../../docs/architecture-debt.md) is what happens when one of the three is
    /// skipped on one document.
    /// </remarks>
    [Fact]
    public void A_writable_relation_does_not_lift_the_configured_ceiling()
    {
        ServiceCapabilityLimits queryOnly = new(null, null, ["Query"], null);

        string capabilities =
            Program.CapabilitiesFor(Editing(), Layer(), queryOnly, writable: true);

        Assert.Equal("Query", capabilities);
    }

    /// <summary>
    /// A layer with no integer identity is read-only whatever the relation says.
    /// </summary>
    /// <remarks>
    /// <b>The guard this one was modelled on, kept honest.</b> ADR-013 §2a: with no way to name
    /// a row there is nothing to update or delete, and a writable relation does not supply one.
    /// Asserted so that adding the writability branch cannot have shadowed the identity branch.
    /// </remarks>
    [Fact]
    public void A_layer_with_no_integer_identity_stays_read_only()
    {
        string capabilities = Program.CapabilitiesFor(
            Editing(), Layer(integerIdentity: null), ServiceCapabilityLimits.Unset,
            writable: true);

        Assert.Equal("Query", capabilities);
    }

    /// <summary>A request from somebody who may edit anything.</summary>
    /// <returns>The context, with the principal feature the capability code reads.</returns>
    private static DefaultHttpContext Editing()
    {
        DefaultHttpContext context = new();

        context.Features.Set(new RequestPrincipal(
            new Principal(Guid.NewGuid(), PrincipalKind.User, "editor", "Editor", isDisabled: false),
            Guid.NewGuid(),
            Authorization.Resolve(UserTypes.Unrestricted, [Roles.Administrator])));

        return context;
    }

    /// <summary>A published layer over a point table.</summary>
    /// <param name="integerIdentity">
    /// The object-id column, or null for a layer ArcGIS cannot address rows in.
    /// </param>
    /// <returns>The layer.</returns>
    private static PublishedLayer Layer(string? integerIdentity = "id") =>
        new(
            Guid.NewGuid(),
            new LayerDefinition("parcels", "public", "parcels", "geom", 4326, "id", integerIdentity, isHosted: false),
            "source",
            "Host=localhost;Database=gis",
            GeometryKind.Point,
            owner: null,
            SharingScope.Private,
            ServiceStatus.Started);
}
