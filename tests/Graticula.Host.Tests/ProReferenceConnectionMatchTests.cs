using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A layer Pro shares by reference is served only from a registered database it reaches as the same database user —
/// ADR-299, owner decision 2026-11-12.
/// </summary>
public sealed class ProReferenceConnectionMatchTests
{
    private const string Pro =
        "SERVER=localhost,55499;INSTANCE=sde:postgresql:localhost,55499;DBCLIENT=postgresql;"
        + "DB_CONNECTION_PROPERTIES=localhost,55499;DATABASE=gis_dev;USER=gis;AUTHENTICATION_MODE=DBMS";

    private static ArcGisConnection Registered(string user) =>
        ArcGisConnection.FromNpgsql($"Host=localhost;Port=55499;Database=gis_dev;Username={user};Password=x")!;

    [Fact]
    public void TheSameDatabaseAndUserMatch() =>
        Assert.True(ArcGisConnection.Parse(Pro)!.Matches(Registered("gis")));

    [Fact]
    public void TheUserIsComparedWithoutCase() =>
        Assert.True(ArcGisConnection.Parse(Pro.Replace("USER=gis", "USER=GIS", System.StringComparison.Ordinal))!
            .Matches(Registered("gis")));

    [Fact]
    public void AnotherUserOnTheSameDatabaseDoesNotMatch() =>
        Assert.False(ArcGisConnection.Parse(Pro)!.Matches(Registered("graticula_serving")));

    [Fact]
    public void NoUserOnProsSideMatchesNothing() =>
        Assert.False(ArcGisConnection.Parse(Pro.Replace("USER=gis;", string.Empty, System.StringComparison.Ordinal))!
            .Matches(Registered("gis")));

    [Fact]
    public void AnotherDatabaseDoesNotMatch() =>
        Assert.False(ArcGisConnection.Parse(Pro.Replace("DATABASE=gis_dev", "DATABASE=gis", System.StringComparison.Ordinal))!
            .Matches(Registered("gis")));

    [Fact]
    public void ThePublishersAddressIsWhatProIsComparedWith() =>
        Assert.True(ArcGisConnection.Parse(Pro)!.Matches(
            ArcGisConnection.FromNpgsql("Host=datastore;Port=5432;Database=gis_dev;Username=gis;Password=x")!.At("localhost,55499")));
}
