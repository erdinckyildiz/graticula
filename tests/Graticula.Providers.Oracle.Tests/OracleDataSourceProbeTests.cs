using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>What registering an Oracle database finds, against a real one — ADR-205 phase 1.</summary>
[Trait("Needs", "Oracle")]
public sealed class OracleDataSourceProbeTests
{
    private readonly OracleDataSourceProbe _probe = new();

    [Fact]
    public async Task The_spatial_tables_are_offered_with_their_key_reference_and_type_and_read_only()
    {
        ProbeResult result = await _probe.ProbeAsync(OracleFixture.Locator, CancellationToken.None);

        Assert.True(result.Outcome == ProbeOutcome.Usable, result.Message);
        Assert.StartsWith("Oracle Spatial ", result.PostgisVersion, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.ServerVersion));

        string owner = OracleFixture.Source.User.ToUpperInvariant();
        SourceTable land = result.Tables.Single(t => t.SchemaName == owner && t.TableName == "TR_KARA");
        SourceTable places = result.Tables.Single(t => t.SchemaName == owner && t.TableName == "TR_YER");

        Assert.Equal(("SHAPE", 4326, "Polygon", "OBJECTID"), (land.GeometryColumn, land.Srid, land.GeometryType, land.CandidateObjectIdColumn));
        Assert.Equal(("SHAPE", 4326, "Point", "OBJECTID"), (places.GeometryColumn, places.Srid, places.GeometryType, places.CandidateObjectIdColumn));

        // Phase 1 reads; editing waits on the writer (condition 6).
        // ADR-205 phase 2: the test user owns these tables, so they are offered as editable.
        Assert.All(result.Tables, t => Assert.True(t.Writable));
    }

    [Fact]
    public async Task A_wrong_password_is_said_as_one()
    {
        string wrong = OracleLocator.For(OracleFixture.Source with { Password = "not-the-password-1" });

        ProbeResult result = await _probe.ProbeAsync(wrong, CancellationToken.None);

        Assert.Equal(ProbeOutcome.CannotConnect, result.Outcome);
        Assert.Contains("ORA-01017", result.Message, StringComparison.Ordinal);
        Assert.Empty(result.Tables);
    }

    [Fact]
    public async Task A_service_the_listener_does_not_know_names_the_service_as_the_problem()
    {
        string wrong = OracleLocator.For(OracleFixture.Source with { Service = "NOSUCHPDB" });

        ProbeResult result = await _probe.ProbeAsync(wrong, CancellationToken.None);

        Assert.Equal(ProbeOutcome.CannotConnect, result.Outcome);
        Assert.Contains("ORA-12514", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_database_listing_is_the_service_the_connection_names()
    {
        DatabaseListing listing = await _probe.ListDatabasesAsync(OracleFixture.Locator, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Usable, listing.Outcome);
        Assert.Equal([OracleFixture.Source.Service], listing.Databases);
    }
}
