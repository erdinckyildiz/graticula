using System;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Platform.Tests.Admin;

/// <summary>
/// ADR-095: which kinds of source serve vector tiles, how long their tiles are kept when nobody chose,
/// and what the admin API says about how closely they follow the data.
/// </summary>
/// <remarks>
/// <b>Per kind, because the rule is a whitelist of kinds.</b> A test that only checked *registered PostGIS is
/// tiled now* would pass on a rule that tiled everything, and the one kind that must stay refused — an engine
/// nothing here can encode — is the half Q-67 still decides.
/// </remarks>
public sealed class TileSourcesTests
{
    [Fact]
    public void Every_kind_a_registration_can_name_is_tiled()
    {
        // The datastore.
        Assert.True(TileSources.Tiled(hosted: true, DataSourceKinds.PostGis));

        // ADR-095: a registered PostGIS database, which Q-67 refused until 2026-09-29.
        Assert.True(TileSources.Tiled(hosted: false, DataSourceKinds.PostGis));

        // ADR-066 §9 and ADR-067: the DuckDB-read kinds.
        Assert.True(TileSources.Tiled(hosted: false, DataSourceKinds.GeoParquet));
        Assert.True(TileSources.Tiled(hosted: false, DataSourceKinds.GeoParquetRemote));
        Assert.True(TileSources.Tiled(hosted: false, DataSourceKinds.DuckDb));
        Assert.True(TileSources.Tiled(hosted: false, DataSourceKinds.MotherDuck));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("oracle")]
    [InlineData("mysql")]
    [InlineData("")]
    [InlineData(null)]
    public void A_kind_nothing_here_encodes_is_still_refused(string? kind)
    {
        // Q-67's reason still holds for these engines: `ST_AsMVT` is a PostGIS function. None can be
        // registered today; the provider v1-scope §3a defers would be refused until somebody decides how
        // its rows reach a tile.
        Assert.False(TileSources.Tiled(hosted: false, kind));
    }

    [Fact]
    public void Only_a_registered_PostGIS_layer_gets_the_shorter_default()
    {
        TimeSpan server = TimeSpan.FromMinutes(60);

        Assert.Equal(TileSources.RegisteredLifetime, TileSources.DefaultLifetimeOf(false, DataSourceKinds.PostGis, server));
        Assert.Equal(TimeSpan.FromMinutes(5), TileSources.RegisteredLifetime);

        // Hosted and the file kinds keep the server's default: nothing about them changed.
        Assert.Equal(server, TileSources.DefaultLifetimeOf(true, DataSourceKinds.PostGis, server));
        Assert.Equal(server, TileSources.DefaultLifetimeOf(false, DataSourceKinds.GeoParquet, server));
        Assert.Equal(server, TileSources.DefaultLifetimeOf(false, DataSourceKinds.MotherDuck, server));
    }

    [Fact]
    public void The_registered_default_never_lengthens_a_shorter_server_default()
    {
        // An operator who set the whole server to two minutes did not ask for registered layers to be kept
        // longer than everything else.
        TimeSpan two = TimeSpan.FromMinutes(2);

        Assert.Equal(two, TileSources.DefaultLifetimeOf(false, DataSourceKinds.PostGis, two));
        Assert.Equal(TimeSpan.Zero, TileSources.DefaultLifetimeOf(false, DataSourceKinds.PostGis, TimeSpan.Zero));
    }

    [Fact]
    public void Coherence_says_best_effort_where_other_tools_write()
    {
        Assert.Equal("exact", TileSources.CoherenceOf(true, DataSourceKinds.PostGis));
        Assert.Equal("best-effort", TileSources.CoherenceOf(false, DataSourceKinds.PostGis));
        Assert.Equal("best-effort", TileSources.CoherenceOf(false, DataSourceKinds.MotherDuck));
        Assert.Equal("file-version", TileSources.CoherenceOf(false, DataSourceKinds.GeoParquet));
        Assert.Equal("file-version", TileSources.CoherenceOf(false, DataSourceKinds.DuckDb));
    }
}
