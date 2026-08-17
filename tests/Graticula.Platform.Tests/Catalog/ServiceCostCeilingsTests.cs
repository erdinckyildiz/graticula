using System;
using Graticula.Platform.Catalog;
using Xunit;

namespace Graticula.Platform.Tests.Catalog;

/// <summary>
/// A cost ceiling narrows and never widens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Q-113.</b> The rule is the same one ADR-031 applied to capabilities, on a
/// different axis: a service may ask for less than the server permits and never for
/// more. Without it a per-service setting makes the server-wide figure advisory, and
/// an operator who lowered it globally would not have lowered it.
/// </para>
/// <para>
/// <b>Zero is the case worth having tests for.</b> A response ceiling of zero means
/// *no ceiling*, so "the smaller of the two" is the wrong rule there and a naive
/// minimum would let a disabled server ceiling disable the service's as well.
/// </para>
/// </remarks>
public sealed class ServiceCostCeilingsTests
{
    [Fact]
    public void An_unset_ceiling_defers_to_the_server()
    {
        Assert.Equal(50_000, ServiceCostCeilings.Unset.RecordCount(50_000));
        Assert.Equal(1_000, ServiceCostCeilings.Unset.PageSize(1_000, 50_000));
        Assert.Equal(64L * 1024 * 1024, ServiceCostCeilings.Unset.ResponseBytes(64L * 1024 * 1024));
        Assert.True(ServiceCostCeilings.Unset.IsUnset);
    }

    [Fact]
    public void A_service_may_ask_for_fewer_rows_than_the_server_permits()
    {
        ServiceCostCeilings cost = new(maximumRecordCount: 50, null, null, null, null);

        Assert.Equal(50, cost.RecordCount(50_000));
    }

    [Fact]
    public void A_service_may_not_ask_for_more_rows_than_the_server_permits()
    {
        // The direction that matters. A service asking for a million rows gets the
        // server's figure, not its own.
        ServiceCostCeilings cost = new(maximumRecordCount: 1_000_000, null, null, null, null);

        Assert.Equal(50_000, cost.RecordCount(50_000));
    }

    [Fact]
    public void A_service_default_replaces_the_servers_and_is_still_clamped()
    {
        ServiceCostCeilings cost = new(maximumRecordCount: 25, defaultRecordCount: 25, null, null, null);

        // The service said 25, so 25 it is — the server's default is what applies when
        // nobody else has an opinion, not a competing figure.
        Assert.Equal(25, cost.PageSize(1_000, 50_000));
        Assert.Equal(25, cost.PageSize(10, 50_000));

        // <b>But it is still clamped by the ceiling actually in force.</b> A server
        // whose maximum is five will not hand out a page of twenty-five because a
        // service asked for one — which is the same narrowing rule as everywhere else
        // here, applied to the default rather than to the maximum.
        Assert.Equal(5, cost.PageSize(1_000, 5));
    }

    [Fact]
    public void With_no_service_default_the_servers_applies()
    {
        ServiceCostCeilings cost = new(maximumRecordCount: 25, null, null, null, null);

        Assert.Equal(10, cost.PageSize(10, 50_000));

        // And the server's default is clamped by the service's maximum, so a server
        // default of a thousand cannot produce a page of a thousand on a service that
        // permits twenty-five.
        Assert.Equal(25, cost.PageSize(1_000, 50_000));
    }

    [Fact]
    public void A_default_larger_than_the_services_own_maximum_is_refused()
    {
        // Refused rather than clamped: an operator who wrote both meant one of them,
        // and quietly picking one hides which. The database refuses it too
        // (migration 17), because this constructor is not the only way to write a row.
        ArgumentOutOfRangeException refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceCostCeilings(
                maximumRecordCount: 10, defaultRecordCount: 100, null, null, null));

        Assert.Contains("would never apply", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_ceiling_applies_when_the_server_has_none()
    {
        // Zero means no ceiling, so a naive Math.Min would return 0 here and disable
        // the service's ceiling — the bug this method exists to avoid.
        ServiceCostCeilings cost = new(null, null, maximumResponseBytes: 4096, null, null);

        Assert.Equal(4096, cost.ResponseBytes(0));
        Assert.Equal(4096, cost.ResponseBytes(8192));
        Assert.Equal(2048, cost.ResponseBytes(2048));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_ceiling_that_is_not_positive_is_refused(int value)
    {
        // Zero would describe a service that answers nothing, which an empty
        // capability set already says (ADR-031 §2a) and says more clearly.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceCostCeilings(value, null, null, null, null));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceCostCeilings(null, null, null, null, value));
    }

    [Fact]
    public void Cost_and_capability_are_separate_axes()
    {
        // A service may bound what a request costs without configuring any
        // capability. Reading one and not the other is how the first version of the
        // catalogue read silently discarded every cost ceiling on such a service.
        ServiceCapabilityLimits limits = ServiceCapabilityLimits.Unset
            .With(new ServiceCostCeilings(50, null, null, null, null));

        Assert.False(limits.IsUnset);
        Assert.Null(limits.ServesFeatures);
        Assert.Equal(50, limits.Cost.MaximumRecordCount);
    }
}
