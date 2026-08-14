using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Providers.PostGis;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Following a declared relationship, against real PostgreSQL.
/// </summary>
/// <remarks>
/// ADR-013 §3 requires one query rather than N+1, and the difference is
/// invisible in the result — a loop returns exactly the same records. So the
/// test that matters counts statements rather than rows.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostGisRelatedRecordsTests : PostgresFixture
{
    private static string Unique => "rel_" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(string Parcels, string Owners)> FixtureAsync()
    {
        string parcels = Unique;
        string owners = Unique;

        await using NpgsqlCommand create = DataSource.CreateCommand(
            $"""
             create table public.{parcels} (
               objectid  integer generated always as identity primary key,
               parcel_id integer not null,
               address   text);

             create table public.{owners} (
               objectid   integer generated always as identity primary key,
               parcel_id  integer not null,
               owner_name text);

             insert into public.{parcels} (parcel_id, address)
               values (100, 'one'), (200, 'two'), (300, 'lonely');

             insert into public.{owners} (parcel_id, owner_name)
               values (100, 'Ayse'), (100, 'Mehmet'), (200, 'Zeynep');
             """);

        await create.ExecuteNonQueryAsync();
        return (parcels, owners);
    }

    private async Task DropAsync(string a, string b)
    {
        await using NpgsqlCommand drop = DataSource.CreateCommand(
            $"drop table if exists public.{a}; drop table if exists public.{b};");

        await drop.ExecuteNonQueryAsync();
    }

    private static LayerDefinition Layer(string table) => new(
        name: table,
        schemaName: "public",
        tableName: table,
        geometryColumn: "geom",
        srid: 3857,
        identityColumn: "objectid",
        objectIdColumn: "objectid",
        isHosted: true);

    private static readonly FieldDescription[] OwnerFields =
    [
        new("objectid", FieldType.Integer, false, null),
        new("parcel_id", FieldType.Integer, false, null),
        new("owner_name", FieldType.Text, true, null),
    ];

    [Fact]
    public async Task Related_rows_come_back_grouped_by_the_feature_they_belong_to()
    {
        (string parcels, string owners) = await FixtureAsync();

        try
        {
            PostGisRelatedRecords related = new(
                DataSource, Layer(parcels), Layer(owners), sameDatabase: true);

            IReadOnlyList<RelatedGroup> groups = await related.QueryAsync(
                "parcel_id", "parcel_id", [1, 2], OwnerFields, 1000, CancellationToken.None);

            Assert.Equal(2, groups.Count);
            Assert.Equal(["Ayse", "Mehmet"],
                groups[0].Records.Select(r => (string?)r["owner_name"]));
            Assert.Equal(["Zeynep"], groups[1].Records.Select(r => (string?)r["owner_name"]));
        }
        finally
        {
            await DropAsync(parcels, owners);
        }
    }

    [Fact]
    public async Task A_feature_with_no_related_rows_is_absent_rather_than_empty()
    {
        // ArcGIS omits the group. An empty group would make a client render an
        // expandable list of nothing beside every unrelated feature.
        (string parcels, string owners) = await FixtureAsync();

        try
        {
            PostGisRelatedRecords related = new(
                DataSource, Layer(parcels), Layer(owners), sameDatabase: true);

            IReadOnlyList<RelatedGroup> groups = await related.QueryAsync(
                "parcel_id", "parcel_id", [3], OwnerFields, 1000, CancellationToken.None);

            Assert.Empty(groups);
        }
        finally
        {
            await DropAsync(parcels, owners);
        }
    }

    [Fact]
    public async Task Every_requested_feature_is_answered_by_one_statement()
    {
        // <b>The requirement ADR-013 §3 states and the result cannot show.</b> A
        // loop over the object ids returns identical records; the only
        // difference is how many times the database was asked.
        //
        // The first version of this counted pg_stat_database's transactions,
        // which count every connection to the database — so it measured whatever
        // else the suite happened to be doing and failed as soon as it ran
        // beside anything. The reader counts its own statements instead.
        (string parcels, string owners) = await FixtureAsync();

        try
        {
            PostGisRelatedRecords related = new(
                DataSource, Layer(parcels), Layer(owners), sameDatabase: true);

            await related.QueryAsync(
                "parcel_id", "parcel_id", [1, 2, 3], OwnerFields, 1000, CancellationToken.None);

            Assert.Equal(1, related.QueriesIssued);
        }
        finally
        {
            await DropAsync(parcels, owners);
        }
    }

    [Fact]
    public async Task Two_layers_in_different_databases_are_refused_with_the_reason()
    {
        // A join is one statement in one database. Refusing at query time rather
        // than at declaration time is deliberate: a data source can be
        // re-registered elsewhere after the relationship was made.
        (string parcels, string owners) = await FixtureAsync();

        try
        {
            PostGisRelatedRecords related = new(
                DataSource, Layer(parcels), Layer(owners), sameDatabase: false);

            InvalidOperationException refused =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => related.QueryAsync(
                        "parcel_id", "parcel_id", [1], OwnerFields, 1000, CancellationToken.None));

            Assert.Contains("different databases", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DropAsync(parcels, owners);
        }
    }

    [Fact]
    public async Task The_limit_bounds_the_answer()
    {
        (string parcels, string owners) = await FixtureAsync();

        try
        {
            PostGisRelatedRecords related = new(
                DataSource, Layer(parcels), Layer(owners), sameDatabase: true);

            IReadOnlyList<RelatedGroup> groups = await related.QueryAsync(
                "parcel_id", "parcel_id", [1, 2], OwnerFields, 2, CancellationToken.None);

            Assert.Equal(2, groups.Sum(g => g.Records.Count));
        }
        finally
        {
            await DropAsync(parcels, owners);
        }
    }
}
