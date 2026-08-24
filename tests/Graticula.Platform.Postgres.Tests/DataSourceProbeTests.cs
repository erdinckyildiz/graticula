using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Postgres;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// What the probe can tell an operator about a table it has never seen.
/// </summary>
/// <remarks>
/// <para>
/// <b><see href="../../docs/architecture-debt.md">D-50</see>: the probe could not inform
/// the one publish field the operator must decide.</b> <c>POST /admin/layers</c> requires
/// <c>identityColumn</c> and Q-57 makes that a nomination rather than an inference —
/// <em>declared, not inferred</em> — and the probe reported a single suggestion, and only
/// when a table already had an ArcGIS-shaped object id. Everything else was typed from
/// memory, where a typo is a 400 and a wrong-but-existing column decides which row an edit
/// lands on.
/// </para>
/// <para>
/// <b>Four table shapes, and the third is the one that matters.</b> A table whose primary
/// key is text and which carries two unique integer columns is exactly the case the old
/// probe handled worst: it offered one of the two integers with no sign that the other
/// existed and no mention of the key the table was actually designed around. The other
/// three are the boundaries — an ArcGIS-shaped table, a composite key, and no key at all —
/// because a check that only tries the case it was written for proves the case it was
/// written for.
/// </para>
/// </remarks>
public sealed class DataSourceProbeTests : PostgresFixture
{
    private async Task ShapesAsync()
    {
        await using NpgsqlConnection connection =
            await DataSource.OpenConnectionAsync(CancellationToken.None);

        foreach (string statement in new[]
        {
            // An ArcGIS-shaped table: the integer key is the obvious nomination, and a
            // second unique integer beside it that the old probe never mentioned.
            $"""
             create table "{SchemaName}".arcgis_shaped (
               objectid serial primary key,
               code     integer unique,
               geom     geometry(Point, 4326))
             """,

            // The interesting one. The key is text, so it can never be an object id; two
            // unique integers are candidates and the old probe named exactly one.
            $"""
             create table "{SchemaName}".text_key (
               ref      text primary key,
               legacy   integer unique,
               parcel   integer unique,
               geom     geometry(Polygon, 4326))
             """,

            // A composite key names nothing: half a key is worse than no key.
            $"""
             create table "{SchemaName}".composite_key (
               a integer, b integer, geom geometry(Point, 4326), primary key (a, b))
             """,

            // No key at all.
            $"""
             create table "{SchemaName}".no_key (
               label text, geom geometry(Point, 4326))
             """,
        })
        {
            await using NpgsqlCommand command = new(statement, connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<Dictionary<string, SourceTable>> ProbeAsync()
    {
        PostgresDataSourceProbe probe = new();

        // <b>The environment's string, not the data source's.</b> Npgsql redacts the
        // password out of `NpgsqlDataSource.ConnectionString`, so handing that to the probe
        // is a connection attempt with no credential — which it reports, correctly, as
        // `CannotConnect`. Found by watching all four of these fail identically.
        string connectionString =
            Environment.GetEnvironmentVariable("GRATICULA_TEST_PG")
            ?? throw new InvalidOperationException(
                "GRATICULA_TEST_PG is not set, so nothing can be probed.");

        ProbeResult result = await probe
            .ProbeAsync(connectionString, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Usable, result.Outcome);

        return result.Tables
            .Where(t => t.SchemaName == SchemaName)
            .ToDictionary(t => t.TableName, StringComparer.Ordinal);
    }

    /// <summary>Every unique integer column is offered, not only the first.</summary>
    [Fact]
    public async Task The_probe_offers_every_identity_candidate_rather_than_one()
    {
        await ShapesAsync();

        Dictionary<string, SourceTable> tables = await ProbeAsync();

        Assert.Equal(
            ["objectid", "code"],
            tables["arcgis_shaped"].IdentityCandidates);

        // The primary key first, because it is the one an operator should reach for.
        Assert.Equal("objectid", tables["arcgis_shaped"].IdentityCandidates[0]);

        Assert.Equal(
            ["legacy", "parcel"],
            tables["text_key"].IdentityCandidates.OrderBy(c => c, StringComparer.Ordinal));
    }

    /// <summary>The table's own key is named even when it cannot be an object id.</summary>
    /// <remarks>
    /// <b>This is the half a single suggestion could never carry.</b> `text_key`'s key is
    /// `ref`, and an operator who cannot see that is choosing an identity column without
    /// knowing what the table was designed around — which is how a plausible wrong column
    /// gets nominated and starts deciding which row an edit lands on.
    /// </remarks>
    [Fact]
    public async Task The_probe_names_a_primary_key_it_cannot_recommend()
    {
        await ShapesAsync();

        Dictionary<string, SourceTable> tables = await ProbeAsync();

        Assert.Equal("ref", tables["text_key"].PrimaryKeyColumn);

        // And it is still honest about what may be an object id, which is neither of them.
        Assert.NotEqual("ref", tables["text_key"].CandidateObjectIdColumn);
    }

    /// <summary>Half a composite key is worse than no key, so nothing is named.</summary>
    [Fact]
    public async Task A_composite_key_and_a_keyless_table_offer_nothing()
    {
        await ShapesAsync();

        Dictionary<string, SourceTable> tables = await ProbeAsync();

        foreach (string name in new[] { "composite_key", "no_key" })
        {
            Assert.Null(tables[name].PrimaryKeyColumn);
            Assert.Null(tables[name].CandidateObjectIdColumn);
            Assert.Empty(tables[name].IdentityCandidates);
        }
    }

    /// <summary>
    /// The first candidate is the object id column, so the console's one-click nomination
    /// and the list it comes from cannot disagree.
    /// </summary>
    /// <remarks>
    /// <b>Two views of one fact, which is the shape
    /// <see href="../../docs/architecture-debt.md">D-46</see> records.</b> The console
    /// offers <c>CandidateObjectIdColumn</c> as a one-click nomination and would draw the
    /// rest of the list beside it; if the two were computed by different rules the button
    /// would eventually name something the list did not contain.
    /// </remarks>
    [Fact]
    public async Task The_one_click_nomination_is_the_first_of_the_candidates()
    {
        await ShapesAsync();

        Dictionary<string, SourceTable> tables = await ProbeAsync();

        foreach (SourceTable table in tables.Values)
        {
            if (table.CandidateObjectIdColumn is null)
            {
                Assert.Empty(table.IdentityCandidates);
                continue;
            }

            Assert.Equal(table.CandidateObjectIdColumn, table.IdentityCandidates[0]);
        }
    }
}
