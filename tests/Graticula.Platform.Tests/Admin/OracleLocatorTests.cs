using System;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Platform.Tests.Admin;

/// <summary>How a registered Oracle database is stored where a connection string would be — ADR-205.</summary>
public sealed class OracleLocatorTests
{
    private static readonly OracleSource Source =
        new("db.example.org", 1521, "FREEPDB1", "trdata", "p;a=s\"s'w o\\rd");

    [Fact]
    public void A_password_with_every_awkward_character_survives_the_round_trip()
    {
        string stored = OracleLocator.For(Source);

        Assert.StartsWith(OracleLocator.Scheme, stored, StringComparison.Ordinal);
        Assert.Equal(Source, OracleLocator.Of(stored));
    }

    [Fact]
    public void What_is_shown_has_no_password_in_it()
    {
        string shown = OracleLocator.WithoutSecrets(OracleLocator.For(Source));

        Assert.DoesNotContain("p;a=s", shown, StringComparison.Ordinal);
        Assert.Null(OracleLocator.Of(shown).Password);
        Assert.Equal("db.example.org:1521/FREEPDB1", OracleLocator.Of(shown).Address);
    }

    [Fact]
    public void An_oracle_locator_is_oracle_and_never_falls_through_to_postgis()
    {
        // Until 2026-10-19 everything KindOf did not recognise was PostGIS; an Oracle locator would have been handed
        // to Npgsql by every caller that asks.
        string stored = OracleLocator.For(Source);

        Assert.Equal(DataSourceKinds.Oracle, GeoParquetLocator.KindOf(stored));
        Assert.False(GeoParquetLocator.Is(stored));
        Assert.Equal(DataSourceKinds.PostGis, GeoParquetLocator.KindOf("Host=db;Database=gis;Username=u"));
    }

    [Fact]
    public void Two_users_of_one_database_share_its_key_and_two_databases_do_not()
    {
        string a = OracleLocator.For(Source);
        string sameDatabase = OracleLocator.For(Source with { User = "OTHER", Password = "x", Host = "DB.example.org" });
        string other = OracleLocator.For(Source with { Service = "OTHERPDB" });

        Assert.Equal(OracleLocator.DatabaseKey(a), OracleLocator.DatabaseKey(sameDatabase));
        Assert.NotEqual(OracleLocator.DatabaseKey(a), OracleLocator.DatabaseKey(other));
    }

    [Theory]
    [InlineData("oracle:not json")]
    [InlineData("oracle:{\"host\":\"h\",\"port\":1521,\"service\":\"\",\"user\":\"u\",\"password\":null}")]
    [InlineData("oracle:{\"host\":\"h\",\"port\":0,\"service\":\"s\",\"user\":\"u\",\"password\":null}")]
    [InlineData("Host=db;Database=gis")]
    public void A_locator_that_names_no_database_is_refused(string stored) =>
        Assert.Throws<ArgumentException>(() => OracleLocator.Of(stored));
}
