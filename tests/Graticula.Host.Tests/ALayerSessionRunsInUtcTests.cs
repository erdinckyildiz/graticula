using System;
using Graticula.Features;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A moment is UTC on the way in and on the way out, whatever zone the server or the database runs in.
/// </summary>
/// <remarks>
/// <b>Found 2026-10-16 by ArcGIS Pro's typed edit against a server on a desktop at +03:00</b>: an instant sent for a
/// <c>timestamp</c> column was stored three hours later, and a <c>date</c> read back as the evening before. Containers run
/// in UTC and never showed it. Every layer document says <c>dateFieldsTimeReference: UTC</c>.
/// </remarks>
public sealed class ALayerSessionRunsInUtcTests
{
    [Theory]
    [InlineData("Host=db;Database=gis;Username=u")]
    [InlineData("Host=db;Database=gis;Username=u;Timezone=Europe/Istanbul")]
    public void The_layer_pool_session_is_in_utc_whatever_was_registered(string registered)
    {
        Assert.Equal("UTC", new NpgsqlConnectionStringBuilder(LayerConnections.InUtc(registered)).Timezone);
    }

    [Fact]
    public void A_value_with_no_zone_is_the_utc_wall_clock_and_not_this_machines()
    {
        DateTime read = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), StoredTime.Utc(read));
        Assert.Equal(DateTimeKind.Utc, StoredTime.Utc(read).Kind);
    }

    [Fact]
    public void A_utc_value_is_left_and_a_local_one_is_converted()
    {
        DateTime utc = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime local = utc.ToLocalTime();

        Assert.Equal(utc, StoredTime.Utc(utc));
        Assert.Equal(utc, StoredTime.Utc(local));
    }
}
