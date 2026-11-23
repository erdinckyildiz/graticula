using System;
using Graticula.Platform.Postgres;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-319 §5.2: what connection-string handling changes for a store that names a primary and a standby — and that a
/// string naming one host passes through unchanged, so the baseline deployment builds what it built before.
/// </summary>
public sealed class StoreHostsTests
{
    private const string One = "Host=db;Port=5432;Database=gis;Username=gis";
    private const string Two = "Host=db-a:5432,db-b:5433;Database=gis;Username=gis";

    [Fact]
    public void One_host_passes_through_every_function_unchanged()
    {
        Assert.False(StoreHosts.IsMultiHost(One));
        Assert.Equal(One, StoreHosts.ForConnection(One));

        (string hosts, string ports, string? target) = StoreHosts.ForLibpq(new NpgsqlConnectionStringBuilder(One));
        Assert.Equal("db", hosts);
        Assert.Equal("5432", ports);
        Assert.Null(target);

        using NpgsqlDataSource built = StoreHosts.Build(new NpgsqlDataSourceBuilder(One));
        Assert.IsNotType<NpgsqlMultiHostDataSource>(built);
    }

    [Fact]
    public void Two_hosts_are_read_with_their_ports_in_the_order_named()
    {
        Assert.True(StoreHosts.IsMultiHost(Two));
        Assert.Equal(new[] { ("db-a", 5432), ("db-b", 5433) }, StoreHosts.Of(Two));
        Assert.Equal(new[] { ("a", 6000), ("b", 6000) }, StoreHosts.Of("Host=a,b;Port=6000"));
        Assert.Equal(new[] { ("::1", 5432), ("b", 5433) }, StoreHosts.Of("Host=[::1]:5432,b:5433"));
    }

    [Fact]
    public void A_connection_to_two_hosts_is_sent_to_the_read_write_one_and_rechecks_quickly()
    {
        NpgsqlConnectionStringBuilder given = new(StoreHosts.ForConnection(Two));
        Assert.Equal("read-write", given.TargetSessionAttributes);
        Assert.Equal(1, given.HostRecheckSeconds);

        // An operator's own choice of either is kept.
        NpgsqlConnectionStringBuilder theirs = new(StoreHosts.ForConnection(Two + ";Target Session Attributes=primary;Host Recheck Seconds=5"));
        Assert.Equal("primary", theirs.TargetSessionAttributes);
        Assert.Equal(5, theirs.HostRecheckSeconds);
    }

    [Fact]
    public void Pg_dump_is_given_libpq_lists_and_the_read_write_target()
    {
        (string hosts, string ports, string? target) = StoreHosts.ForLibpq(new NpgsqlConnectionStringBuilder(Two));
        Assert.Equal("db-a,db-b", hosts);
        Assert.Equal("5432,5433", ports);
        Assert.Equal("read-write", target);
    }

    /// <summary>
    /// The measured reason this class exists: Npgsql 9 refuses the target in a multi-host string given to a builder,
    /// and <see cref="StoreHosts.Build"/> takes it out and applies it — so a string an operator wrote with it still builds.
    /// </summary>
    [Fact]
    public void A_pool_over_two_hosts_builds_with_or_without_the_target_in_the_string()
    {
        string with = Two + ";Target Session Attributes=read-write";
        Assert.Throws<InvalidOperationException>(() => new NpgsqlDataSourceBuilder(with).Build());

        NpgsqlDataSource built = StoreHosts.Build(new NpgsqlDataSourceBuilder(with));
        Assert.NotNull(built);
        Assert.NotNull(StoreHosts.Build(new NpgsqlDataSourceBuilder(Two)));
    }

    [Fact]
    public void A_host_voted_down_is_held_out_until_it_is_cleared()
    {
        string address = StoreHosts.Address("Held-Out.example", 5432);
        Assert.Equal("held-out.example:5432", address);
        Assert.False(StoreEpoch.IsSuspect(address));

        StoreEpoch.Suspect(address, TimeSpan.FromMinutes(1));
        Assert.True(StoreEpoch.IsSuspect(address));

        StoreEpoch.Clear(address);
        Assert.False(StoreEpoch.IsSuspect(address));
    }

    [Fact]
    public void The_newest_timeline_only_rises_and_is_remembered_across_a_restart()
    {
        long system = Random.Shared.NextInt64(1, long.MaxValue);
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "store-epoch-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            StoreEpoch.Load(file);
            Assert.True(StoreEpoch.Observe(system, 2));
            Assert.False(StoreEpoch.Observe(system, 1));
            Assert.Equal(2, StoreEpoch.Newest(system));
            Assert.Contains(system.ToString(System.Globalization.CultureInfo.InvariantCulture), System.IO.File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }
}
