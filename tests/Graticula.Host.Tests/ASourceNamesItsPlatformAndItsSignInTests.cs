using System.Text.Json;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-055 condition 1: <i>Database Platform</i> and <i>Authentication Type</i>, left out of the connection dialog while
/// each had one value, are named for every listed source now that neither has one.
/// </summary>
public sealed class ASourceNamesItsPlatformAndItsSignInTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Remote(string location, string? keyId) =>
        GeoParquetLocator.ForRemote(JsonSerializer.Serialize(
            new { location, region = (string?)null, endpoint = (string?)null, accessKeyId = keyId,
                  secretAccessKey = keyId is null ? null : "s3cr3t", urlStyle = (string?)null, useSsl = true },
            Web));

    [Theory]
    [InlineData(DataSourceKinds.PostGis, "PostgreSQL with PostGIS", "Database authentication")]
    [InlineData(DataSourceKinds.Oracle, "Oracle", "Database authentication")]
    [InlineData(DataSourceKinds.MySql, "MySQL or MariaDB", "Database authentication")]
    [InlineData(DataSourceKinds.SqlServer, "SQL Server", "Database authentication")]
    [InlineData(DataSourceKinds.GeoParquet, "GeoParquet folder", "None (files on this server)")]
    [InlineData(DataSourceKinds.DuckDb, "DuckDB file", "None (files on this server)")]
    [InlineData(DataSourceKinds.MotherDuck, "MotherDuck", "Access token")]
    public void Every_kind_names_its_platform_and_how_it_signs_in(string kind, string platform, string authentication)
    {
        Assert.Equal((platform, authentication), AdminEndpoints.Describe(kind, "anything"));
    }

    /// <summary>
    /// One platform, two ways in: a bucket is read with an access key or anonymously, and which is read from what is
    /// stored — never the key itself. A source that could not be opened says nothing it does not know.
    /// </summary>
    [Fact]
    public void A_remote_location_says_whether_it_holds_a_key_and_never_which()
    {
        Assert.Equal("Access key", AdminEndpoints.Describe(DataSourceKinds.GeoParquetRemote,
            Remote("s3://bucket/prefix/", "AKIAEXAMPLEKEY123")).Authentication);
        Assert.Equal("Anonymous", AdminEndpoints.Describe(DataSourceKinds.GeoParquetRemote,
            Remote("s3://open-data/roads/", null)).Authentication);
        Assert.Equal("Anonymous", AdminEndpoints.Describe(DataSourceKinds.GeoParquetRemote,
            Remote("https://example.org/roads.parquet", null)).Authentication);
        Assert.Null(AdminEndpoints.Describe(DataSourceKinds.GeoParquetRemote, null).Authentication);
        Assert.Equal("GeoParquet on the web or in S3",
            AdminEndpoints.Describe(DataSourceKinds.GeoParquetRemote, null).Platform);
        Assert.DoesNotContain("AKIA", AdminEndpoints.Describe(DataSourceKinds.GeoParquetRemote,
            Remote("s3://bucket/prefix/", "AKIAEXAMPLEKEY123")).Authentication!, System.StringComparison.Ordinal);
    }
}
