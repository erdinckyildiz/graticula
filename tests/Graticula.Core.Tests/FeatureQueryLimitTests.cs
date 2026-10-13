using System;
using Graticula.Features;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// What one query may ask for, and the one kind of query that may ask for more — owner decision 2026-10-13.
/// </summary>
public sealed class FeatureQueryLimitTests
{
    [Fact]
    public void A_query_may_ask_for_the_ceiling_and_not_one_more()
    {
        Assert.Equal(FeatureQuery.MaximumLimit, new FeatureQuery(FeatureQuery.MaximumLimit).Limit);

        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureQuery(FeatureQuery.MaximumLimit + 1));
    }

    [Fact]
    public void Only_a_page_of_a_drawing_may_ask_for_more_and_it_has_a_ceiling_of_its_own()
    {
        Assert.Equal(
            FeatureQuery.MaximumDrawingLimit,
            new FeatureQuery(FeatureQuery.MaximumDrawingLimit, drawing: true).Limit);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FeatureQuery(FeatureQuery.MaximumDrawingLimit + 1, drawing: true));
    }
}
