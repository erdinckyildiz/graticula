using System;
using System.Collections.Generic;
using GisServer.Platform.Catalog;
using Xunit;

namespace GisServer.Platform.Tests.Catalog;

/// <summary>
/// A configured capability is a ceiling, and the tests are written to fail on the
/// implementation that gets this wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-031 condition 1 asks for both directions in one place, and the reason is
/// specific.</b> A test that only checks *configuration narrows privilege* passes on
/// an implementation that unions instead of intersecting, as long as the privilege
/// side happens to be the larger set. Only the pair distinguishes the two.
/// </para>
/// <para>
/// This is also where <see href="Q-105">Q-105</see>'s invariant — no composition step
/// may widen what a narrower element allowed — stops being a sentence in a register.
/// </para>
/// </remarks>
public sealed class ServiceCapabilityLimitsTests
{
    private static readonly string[] ReadOnly = ["Query"];
    private static readonly string[] Editor = ["Query", "Create", "Update", "Delete"];
    private static readonly string[] OutOfOrder = ["Delete", "Query", "Update"];
    private static readonly string[] AlsoOutOfOrder = ["Delete", "Update", "Query"];
    private static readonly string[] InDocumentOrder = ["Query", "Update", "Delete"];
    private static readonly string[] QueryAndCreate = ["Query", "Create"];
    private static readonly string[] CreateAndUpdate = ["Create", "Update"];
    private static readonly string[] JustCreate = ["Create"];

    [Fact]
    public void Configuration_cannot_grant_what_a_privilege_withholds()
    {
        // The service says editing is offered. The caller may only read.
        ServiceCapabilityLimits limits = new(null, null, Editor, null);

        Assert.Equal(ReadOnly, limits.Restrict(ReadOnly));
    }

    [Fact]
    public void A_privilege_cannot_grant_what_the_configuration_withholds()
    {
        // The mirror image: the caller may edit, the service is configured not to.
        ServiceCapabilityLimits limits = new(null, null, ReadOnly, null);

        Assert.Equal(ReadOnly, limits.Restrict(Editor));
    }

    [Fact]
    public void An_unset_ceiling_changes_nothing()
    {
        // The compatibility guarantee in one line: this is every service that
        // existed before ADR-031, and it must behave exactly as it did.
        Assert.Equal(Editor, ServiceCapabilityLimits.Unset.Restrict(Editor));
        Assert.Equal(ReadOnly, ServiceCapabilityLimits.Unset.Restrict(ReadOnly));
    }

    [Fact]
    public void An_empty_ceiling_is_not_an_unset_one()
    {
        // ADR-031 §2a keeps Query revocable: a service that is running and refusing
        // is a state distinct from stopped. So an empty list has to survive as
        // empty rather than being read as "nothing configured".
        ServiceCapabilityLimits limits = new(null, null, [], null);

        Assert.False(limits.IsUnset);
        Assert.Empty(limits.Restrict(Editor));
    }

    [Fact]
    public void The_result_is_in_document_order_whatever_order_it_arrives_in()
    {
        // A client reads this as a string. Set iteration order would make the same
        // configuration produce different documents across requests, which is a
        // diff nobody can explain.
        ServiceCapabilityLimits limits = new(null, null, OutOfOrder, null);

        Assert.Equal(InDocumentOrder, limits.Restrict(AlsoOutOfOrder));
    }

    [Fact]
    public void An_unknown_capability_is_refused_rather_than_ignored()
    {
        ArgumentException refusal = Assert.Throws<ArgumentException>(
            () => new ServiceCapabilityLimits(null, null, ["Query", "Truncate"], null));

        // The message has to say why silence would be worse, because the failure it
        // prevents is a service that looks configured and is not.
        Assert.Contains("looks configured and is not", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_timeout_that_is_not_positive_is_refused(int milliseconds)
    {
        // Zero is how PostgreSQL spells "no limit", so accepting it would make this
        // knob the hole D-42 closed — and D-42 was wrong in exactly this direction.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceCapabilityLimits(
                null, null, null, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void A_face_the_data_cannot_serve_stays_off_however_it_is_configured()
    {
        // Tiles come only from hosted data (Q-67). A ceiling cannot lift a floor:
        // configuring tiles on for a registered layer must not produce a tile
        // service, or the setting would promise what the runtime refuses.
        ServiceCapabilityLimits on = new(null, servesTiles: true, null, null);

        Assert.False(on.AllowsTiles(dataSupportsIt: false));
        Assert.True(on.AllowsTiles(dataSupportsIt: true));
    }

    [Fact]
    public void An_unset_face_is_on_and_an_off_face_is_off()
    {
        Assert.True(ServiceCapabilityLimits.Unset.AllowsFeatures(dataSupportsIt: true));
        Assert.True(ServiceCapabilityLimits.Unset.AllowsTiles(dataSupportsIt: true));

        ServiceCapabilityLimits off = new(servesFeatures: false, servesTiles: false, null, null);

        Assert.False(off.AllowsFeatures(dataSupportsIt: true));
        Assert.False(off.AllowsTiles(dataSupportsIt: true));
    }

    [Fact]
    public void Restricting_is_commutative_in_the_only_sense_that_matters()
    {
        // Configuration ∩ privilege = privilege ∩ configuration. Asserted rather
        // than argued, because "composition only ever restricts" is exactly the
        // claim that intersection makes and union does not.
        IReadOnlyList<string> oneWay =
            new ServiceCapabilityLimits(null, null, QueryAndCreate, null).Restrict(CreateAndUpdate);
        IReadOnlyList<string> theOther =
            new ServiceCapabilityLimits(null, null, CreateAndUpdate, null).Restrict(QueryAndCreate);

        Assert.Equal(oneWay, theOther);
        Assert.Equal(JustCreate, oneWay);
    }
}
