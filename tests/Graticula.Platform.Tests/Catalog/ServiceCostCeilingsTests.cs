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
        Assert.Equal(1_000, ServiceCostCeilings.Unset.PageSize(1_000, 50_000));
        Assert.Equal(64L * 1024 * 1024, ServiceCostCeilings.Unset.ResponseBytes(64L * 1024 * 1024));
        Assert.True(ServiceCostCeilings.Unset.IsUnset);
    }

    /// <summary>
    /// The page size is one number — V-70, ADR-084: the service's own when it set one, the server's otherwise.
    /// </summary>
    /// <remarks>
    /// <b>The server's page size is a default and not a ceiling</b>, so a service may set a larger one; the
    /// deployment's ceiling is what nothing exceeds. Until 2026-09-23 there were two numbers here, a default
    /// page and a maximum, and a document giving the one over a query answering the other.
    /// </remarks>
    [Fact]
    public void A_service_page_size_replaces_the_servers_either_way()
    {
        Assert.Equal(50, new ServiceCostCeilings(maximumRecordCount: 50, null, null, null).PageSize(1_000, 50_000));
        Assert.Equal(5_000, new ServiceCostCeilings(maximumRecordCount: 5_000, null, null, null).PageSize(1_000, 50_000));
    }

    [Fact]
    public void A_service_may_not_ask_for_more_rows_than_the_server_permits()
    {
        // The direction that matters. A service asking for a million rows gets the
        // deployment's ceiling, not its own.
        ServiceCostCeilings cost = new(maximumRecordCount: 1_000_000, null, null, null);

        Assert.Equal(50_000, cost.PageSize(1_000, 50_000));
    }

    [Fact]
    public void The_servers_page_size_is_clamped_by_the_ceiling_too()
    {
        Assert.Equal(5, ServiceCostCeilings.Unset.PageSize(1_000, 5));
    }

    [Fact]
    public void A_response_ceiling_applies_when_the_server_has_none()
    {
        // Zero means no ceiling, so a naive Math.Min would return 0 here and disable
        // the service's ceiling — the bug this method exists to avoid.
        ServiceCostCeilings cost = new(null, maximumResponseBytes: 4096, null, null);

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
            () => new ServiceCostCeilings(value, null, null, null));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceCostCeilings(null, null, null, maximumEditsPerTransaction: value));
    }

    [Fact]
    public void Cost_and_capability_are_separate_axes()
    {
        // A service may bound what a request costs without configuring any
        // capability. Reading one and not the other is how the first version of the
        // catalogue read silently discarded every cost ceiling on such a service.
        ServiceCapabilityLimits limits = ServiceCapabilityLimits.Unset
            .With(new ServiceCostCeilings(50, null, null, null));

        Assert.False(limits.IsUnset);
        Assert.Null(limits.ServesFeatures);
        Assert.Equal(50, limits.Cost.MaximumRecordCount);
    }
}
