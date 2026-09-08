using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A layer's fields are read from a catalogue that lists every relation this server publishes,
/// and it still shows only what the credential may read.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-231](../../docs/architecture-debt.md), found 2026-09-09 while assembling the evidence
/// for [Q-18](../../docs/open-questions.md).</b> Two catalogues disagreed and nothing noticed:
/// <c>PostgresDataSourceProbe</c> lists what may be published from <c>pg_class</c>, whose
/// <c>relkind</c> filter admits <c>'m'</c> — a materialized view — while
/// <c>PostGisFeatureSource</c> read its fields from <c>information_schema.columns</c>, which
/// <b>does not list a materialized view at all</b>, because they are not in the SQL standard.
/// </para>
/// <para>
/// <b>So a materialized view published successfully and served nothing.</b> Measured end to end
/// on a fixture: <c>POST /admin/layers</c> answered <b>201</b> with <c>arcGisServable: true</c>,
/// the layer document came back with <c>fields: []</c>, <c>query</c> returned features carrying
/// only the object id, and <c>applyEdits</c> answered <i>'owner' is not a column of this layer</i>
/// about a column that is one.
/// </para>
/// <para>
/// <b>The repair had one way to go wrong and this file is about that way.</b>
/// <c>information_schema</c> is <b>privilege-filtered</b>, and <c>ReadFieldsAsync</c>'s own
/// comment said so — <i>it shows what this credential may actually see, not what exists. That is
/// the honest answer for a capability report.</i> Reading <c>pg_attribute</c> instead is
/// unfiltered, so the naive repair would have reported columns the credential cannot select,
/// turning a missing field list into a disclosure. The query applies
/// <c>has_column_privilege</c>, which is what <c>information_schema</c> applies internally; the
/// second test is what keeps that true.
/// </para>
/// </remarks>
public sealed class FieldsComeFromTheCatalogueThatHasThemTests : PostgresFixture
{
    /// <summary>Where this test builds its relations, so it cleans up by dropping one schema.</summary>
    private const string Schema = "zzzfieldcat";

    /// <summary>
    /// A materialized view's columns are described, where before there were none.
    /// </summary>
    [Fact]
    public async Task A_materialized_view_reports_the_columns_it_has()
    {
        await MakeAsync();

        try
        {
            LayerDescription described = await new PostGisFeatureSource(DataSource, Over("mv"))
                .DescribeAsync(CancellationToken.None);

            Assert.True(
                described.Fields.Count > 0,
                "A materialized view described no fields at all. `information_schema.columns` "
                + "does not list materialized views — PostgreSQL leaves them out because they "
                + "are not in the SQL standard — while the probe that decides what may be "
                + "published reads `pg_class`, which does. A layer whose every attribute is "
                + "invisible is a layer nobody can label, filter or edit. D-231.");

            Assert.Contains(described.Fields, f => f.Name == "keep");
            Assert.Contains(described.Fields, f => f.Name == "code");

            // <b>The shape is not a field, on this relation kind as on any other.</b> A client
            // that finds the geometry column in the field list offers to label features with WKB.
            Assert.DoesNotContain(described.Fields, f => f.Name == "geom");
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// A column the credential may not select is still not described.
    /// </summary>
    /// <remarks>
    /// <b>The property the repair had to keep, asserted rather than argued.</b> Against a role
    /// granted <c>select</c> on three of five columns, <c>information_schema.columns</c> and
    /// <c>pg_attribute</c> with <c>has_column_privilege</c> return the same three, with the same
    /// type, nullability and length for each — measured before the change was written. This test
    /// is the half of that measurement a future edit can break.
    /// </remarks>
    [Fact]
    public async Task A_column_this_credential_cannot_read_is_not_described()
    {
        await MakeAsync();

        try
        {
            await using NpgsqlDataSource narrow = Narrowed();

            LayerDescription described = await new PostGisFeatureSource(narrow, Over("t"))
                .DescribeAsync(CancellationToken.None);

            Assert.Contains(described.Fields, f => f.Name == "keep");

            Assert.DoesNotContain(
                described.Fields,
                f => f.Name == "hidden");
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// A column of a domain type is described as the type the domain is built on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A regression this change introduced and re-reading caught, which is why it is a test
    /// rather than a note.</b> <c>information_schema.columns</c> reports <c>udt_name</c> as the
    /// <i>base</i> type for a domain — <c>varchar</c> for a column of
    /// <c>create domain postcode as varchar(10)</c> — and the first draft of the
    /// <c>pg_attribute</c> query reported <c>pg_type.typname</c>, which is <c>postcode</c>.
    /// <c>MapType</c> has never heard of it, so an ordinary text column would have arrived as
    /// whatever the fallback type is, on every layer whose author uses domains.
    /// </para>
    /// <para>
    /// <b>The length moves with the type</b>, because a domain carries its own
    /// <c>typtypmod</c> and the column's <c>atttypmod</c> is −1. Asserted here so the two halves
    /// cannot drift apart: a repair that resolved the name and not the width would report a
    /// <c>varchar</c> of unknown length, which is a smaller wrong and still a wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_column_of_a_domain_type_is_described_as_its_base_type()
    {
        await MakeAsync();

        try
        {
            LayerDescription described = await new PostGisFeatureSource(DataSource, Over("t"))
                .DescribeAsync(CancellationToken.None);

            FieldDescription postcode = Assert.Single(described.Fields, f => f.Name == "pc");

            Assert.True(
                postcode.Type == FieldType.Text,
                $"A column of `create domain postcode as varchar(10)` was described as "
                + $"{postcode.Type}. `information_schema` reports the base type and this query "
                + "has to as well, or every layer whose author uses domains gets its text "
                + "columns typed as Unknown.");

            Assert.Equal(10, postcode.MaxLength);
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>The layer, over one of this schema's relations.</summary>
    /// <param name="relation">The table or view.</param>
    /// <returns>The definition.</returns>
    private static LayerDefinition Over(string relation) =>
        new(relation, Schema, relation, "geom", 4326, "id", "id", isHosted: false);

    /// <summary>A data source connected as a role granted three of the five columns.</summary>
    /// <returns>The pool, which the caller disposes.</returns>
    /// <remarks>
    /// <b><c>set role</c> rather than a second login</b>, because the role needs no password and
    /// no <c>pg_hba</c> entry this way, and what is under test is the privilege check rather than
    /// authentication. Npgsql runs it on every connection the pool hands out.
    /// </remarks>
    private static NpgsqlDataSource Narrowed()
    {
        // <b>From the variable rather than from `DataSource.ConnectionString`.</b> Npgsql
        // redacts the password out of a live data source's connection string, so building a
        // second pool from it fails at authentication — *No password has been provided but the
        // backend requires one*, which reads like a server problem and is not one.
        string? raw = Environment.GetEnvironmentVariable("GRATICULA_TEST_PG");

        Assert.False(
            string.IsNullOrWhiteSpace(raw),
            "GRATICULA_TEST_PG is not set, so this test cannot open a second pool as a narrower "
            + "role. The fixture's own pool cannot be reused: what is under test is what a "
            + "*different* credential sees.");

        NpgsqlDataSourceBuilder builder = new(raw);

        // <b>Both initializers, because Npgsql refuses one.</b> *Both sync and async connection
        // initializers must be provided* — the pool may open a connection either way and a
        // half-initialized one would silently run as the fixture's own role, which is the role
        // this test exists to not be.
        builder.UsePhysicalConnectionInitializer(
            connection =>
            {
                using NpgsqlCommand become = new($"set role {Reader}", connection);
                become.ExecuteNonQuery();
            },
            async connection =>
            {
                await using NpgsqlCommand become = new($"set role {Reader}", connection);
                await become.ExecuteNonQueryAsync().ConfigureAwait(false);
            });

        return builder.Build();
    }

    /// <summary>The role that may read some of the columns.</summary>
    private const string Reader = "zzzfieldcat_reader";

    /// <summary>Builds the relations and the role.</summary>
    private async Task MakeAsync()
    {
        await DropAsync();

        await ExecuteAsync($"""
            create schema {Schema};

            create domain {Schema}_postcode as varchar(10);

            create table {Schema}.t (
              id serial primary key,
              keep text,
              hidden text,
              code varchar(12),
              pc {Schema}_postcode,
              geom geometry(Point, 4326));

            create materialized view {Schema}.mv as
              select id, keep, code, geom from {Schema}.t;

            create unique index {Schema}_mv_id on {Schema}.mv (id);

            drop role if exists {Reader};
            create role {Reader} nologin;
            grant usage on schema {Schema} to {Reader};
            grant select (id, keep, code, pc, geom) on {Schema}.t to {Reader};
            """);
    }

    /// <summary>Takes it all away again, whatever happened.</summary>
    private async Task DropAsync() =>
        await ExecuteAsync($"""
            drop schema if exists {Schema} cascade;
            drop domain if exists {Schema}_postcode cascade;
            drop role if exists {Reader};
            """);

    /// <summary>Runs one script against the fixture's own credential.</summary>
    /// <param name="sql">The script.</param>
    /// <returns>The work.</returns>
    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection =
            await DataSource.OpenConnectionAsync(CancellationToken.None);

        await using NpgsqlCommand command = new(sql, connection);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
