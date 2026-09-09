using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A layer's shape says whether the store will accept a write, so the document cannot offer
/// an edit the database refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-231](../../docs/architecture-debt.md)'s writing half, 2026-09-10.</b> The ArcGIS layer
/// document advertised <c>Query,Create,Update,Delete</c> from the caller's privileges alone —
/// <c>PrivilegedCapabilities</c> asked the database nothing at all — so a layer over a
/// materialized view or a join view told a client it could edit and then refused every edit
/// with <c>42809</c> or <c>55000</c>. Advertising an operation the store will refuse is
/// [ADR-008](../../docs/adr/ADR-008-query-engine.md) §2's never-over-claim rule broken outright,
/// and unlike the rest of that row it is a defect rather than a product decision.
/// </para>
/// <para>
/// <b>Four relation kinds, because the wrong answer is right for two of them.</b> Measured on
/// 2026-09-10 before anything was written: <c>has_table_privilege</c> — what the code asked —
/// answers <b>yes for all four</b>, because it reports the grant rather than the relation.
/// <c>pg_column_is_updatable(oid, attnum, true)</c> gets all four right. A test over a table
/// alone would pass against either.
/// </para>
/// <para>
/// <b>And it is read live, which is the other half of the decision.</b> A view that refuses
/// writes today accepts them tomorrow, the moment somebody adds an <c>INSTEAD OF</c> trigger —
/// so a flag written into the catalogue at publish time would be right on the day it was
/// written and quietly wrong from then on.
/// <see cref="A_view_becomes_writable_the_moment_a_trigger_makes_it_so"/> is that argument
/// turned into a measurement.
/// </para>
/// <para>
/// <b>The probe is checked against the describe here rather than trusted to agree.</b>
/// <c>PostgresDataSourceProbe</c> answers <em>may this be published</em> and
/// <c>PostGisFeatureSource</c> answers <em>may this be edited</em>; they live in different
/// assemblies and cannot share the SQL, because the only assembly both reach is Tier 1, where
/// [CLAUDE.md](../../CLAUDE.md) §4 forbids a PostgreSQL concept. Two copies of one question is
/// [D-46](../../docs/architecture-debt.md)'s shape, and this file is what stops them drifting.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TheDescribedShapeSaysWhetherTheDatabaseWillTakeAWriteTests : PostgresFixture
{
    /// <summary>A table takes writes, and so does a view PostgreSQL can rewrite onto one.</summary>
    [Fact]
    public async Task A_table_and_an_auto_updatable_view_take_writes()
    {
        await MakeAsync();

        Assert.True(
            await WritableAsync("t"),
            "A plain table reported that the database would not write to it. Everything else "
            + "here is a narrowing of this case, so if this is wrong every layer on the server "
            + "has just been advertised read-only.");

        Assert.True(
            await WritableAsync("v"),
            "A view that selects every column of one table is auto-updatable: PostgreSQL "
            + "rewrites the write onto the table and it succeeds. Reporting it read-only would "
            + "take editing away from the ordinary way a GIS shop exposes a subset of a table.");
    }

    /// <summary>
    /// A join view and a materialized view take none, and both said they did.
    /// </summary>
    /// <remarks>
    /// <b>This is the pair D-231 is about.</b> Both published, both were <c>arcGisServable</c>
    /// with an object id, both advertised <c>Query,Create,Update,Delete</c>, and both answered
    /// every <c>applyEdits</c> with a PostgreSQL refusal — <c>55000 cannot update view</c> and
    /// <c>42809 cannot change materialized view</c>, measured over HTTP on 2026-09-10.
    /// </remarks>
    [Fact]
    public async Task A_join_view_and_a_materialized_view_take_none()
    {
        await MakeAsync();

        Assert.False(
            await WritableAsync("j"),
            "A view over a join reported that the database would take a write. PostgreSQL "
            + "answers `55000: cannot update view` to every one of them, and a layer document "
            + "built on this answer offers an ArcGIS client an edit button that cannot work.");

        Assert.False(
            await WritableAsync("m"),
            "A materialized view reported that the database would take a write. PostgreSQL "
            + "answers `42809: cannot change materialized view` — it is a stored query result, "
            + "and the only thing that changes it is REFRESH.");
    }

    /// <summary>
    /// A view answers differently the moment a trigger changes the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this cannot be a column written at publish time.</b> The same relation, in the
    /// same test, answers no and then yes with nothing republished — an <c>INSTEAD OF</c>
    /// trigger is the documented way to make a non-updatable view writable, and a deployment
    /// that adds one would otherwise have to republish every layer over it to be believed.
    /// The staleness that remains is the describe cache's own thirty seconds, which is the
    /// same bound the field list already lives under.
    /// </para>
    /// <para>
    /// <b>The <c>true</c> in <c>pg_column_is_updatable(oid, attnum, true)</c> is what makes
    /// this work</b> — it is <em>count a trigger</em>. With <c>false</c> the join view would
    /// still report read-only after the trigger, which is an under-claim: quieter than the
    /// defect above and still a layer nobody can edit through a document that says so.
    /// </para>
    /// <para>
    /// <b>One trigger is not enough, and that was measured rather than assumed — it is why
    /// this test has two triggers it does not otherwise need.</b> The first draft added an
    /// <c>INSTEAD OF UPDATE</c> trigger alone and the assertion below failed. PostgreSQL's
    /// <c>pg_column_is_updatable</c> requires <c>UPDATE</c> <em>and</em> <c>DELETE</c> before it
    /// answers true, which is the right bar for a capability string that offers both: a view
    /// that can be updated and not deleted from is not one an editing client can be handed.
    /// Recorded here because the failure looked like a defect in the repair and was the
    /// function being stricter than the test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_view_becomes_writable_the_moment_a_trigger_makes_it_so()
    {
        await MakeAsync();

        Assert.False(await WritableAsync("j"), "The join view should start out read-only.");

        await ExecuteAsync($"""
            create function "{SchemaName}".j_write() returns trigger language plpgsql as $$
            begin
              if tg_op = 'UPDATE' then
                update "{SchemaName}".t set label = new.label where id = new.id;
                return new;
              end if;

              delete from "{SchemaName}".t where id = old.id;
              return old;
            end;
            $$;

            create trigger j_instead_of_update instead of update on "{SchemaName}".j
              for each row execute function "{SchemaName}".j_write();

            create trigger j_instead_of_delete instead of delete on "{SchemaName}".j
              for each row execute function "{SchemaName}".j_write();
            """);

        Assert.True(
            await WritableAsync("j"),
            "A view with INSTEAD OF UPDATE and DELETE triggers takes writes, and the shape "
            + "still reported it read-only. This is the case a flag stored at publish time "
            + "could never have followed, and it is the reason this is read on every describe.");
    }

    /// <summary>
    /// A view whose geometry is computed is read-only even though its other columns are not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one place the geometry column is the question rather than the relation, measured
    /// 2026-09-10.</b> <c>select st_translate(geom, 1, 1) as geom</c> is auto-updatable as a
    /// relation — <c>pg_relation_is_updatable</c> answers 28, every write bit — and its geometry
    /// column is not: an <c>UPDATE</c> naming <c>geom</c> is refused, and every ArcGIS add
    /// carries one.
    /// </para>
    /// <para>
    /// <b>So the answer here is deliberately an under-claim, and the direction is the
    /// decision.</b> Attribute-only writes to this view would succeed and are refused with it.
    /// Understating a capability produces a refusal somebody can ask about; overstating one
    /// produces an edit that fails after the client had already committed to the batch, which
    /// is the whole of [D-231](../../docs/architecture-debt.md).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_view_whose_geometry_is_computed_is_not_writable()
    {
        await MakeAsync();

        await ExecuteAsync($"""
            create view "{SchemaName}".computed as
              select id, label, st_translate(geom, 1, 1) as geom from "{SchemaName}".t;
            """);

        Assert.False(
            await WritableAsync("computed"),
            "A view whose geometry column is an expression reported writable. The relation is "
            + "auto-updatable and the geometry column is not, so asking the relation rather "
            + "than the column advertises an edit every ArcGIS add would fail.");
    }

    /// <summary>
    /// A relation whose columns this credential cannot read still says whether it takes writes.
    /// </summary>
    /// <remarks>
    /// <b>The reason the attribute join is a <c>left join</c>, asserted rather than argued.</b>
    /// The query used to be driven by <c>pg_attribute</c>, so a relation with no readable
    /// column returned no rows at all — and a per-relation fact carried on a per-column row
    /// would have come back as <em>unknown</em> exactly there. Unknown does not narrow anything
    /// ([ADR-008](../../docs/adr/ADR-008-query-engine.md) §2 is about over-claiming, and an
    /// absence must not be read as a refusal), so the one shape where the answer is least
    /// affordable is the one that would have lost it.
    /// </remarks>
    [Fact]
    public async Task A_relation_with_no_readable_column_still_answers()
    {
        await MakeAsync();

        await ExecuteAsync($"""
            drop role if exists {Blinkered};
            create role {Blinkered} nologin;
            grant usage on schema "{SchemaName}" to {Blinkered};
            grant select (geom) on "{SchemaName}".t to {Blinkered};
            grant insert, update, delete on "{SchemaName}".t to {Blinkered};
            """);

        try
        {
            await using NpgsqlDataSource blinkered = As(Blinkered);

            LayerDescription described = await new PostGisFeatureSource(blinkered, Over("t"))
                .DescribeAsync(CancellationToken.None);

            Assert.Empty(described.Fields);

            Assert.True(
                described.Writable,
                "A credential that may write to the table but may read only its geometry got "
                + "no answer about writing at all. The relation has to drive the query for the "
                + "answer to survive having no visible columns.");
        }
        finally
        {
            await ExecuteAsync($"""
                revoke all on "{SchemaName}".t from {Blinkered};
                revoke all on schema "{SchemaName}" from {Blinkered};
                drop role if exists {Blinkered};
                """);
        }
    }

    /// <summary>
    /// The catalogue the console reads and the catalogue the layer document reads agree.
    /// </summary>
    /// <remarks>
    /// <b>Two questions asked of one relation from two assemblies.</b> The console prints
    /// <em>Writable: yes</em> from the probe when an operator picks a table to publish; the
    /// layer document decides whether to advertise <c>Create,Update,Delete</c> from the
    /// describe. An operator who is told a relation is writable and then handed a read-only
    /// layer has been given two answers and no way to choose between them, which is worse than
    /// either answer alone.
    /// </remarks>
    [Fact]
    public async Task The_probe_and_the_describe_agree_about_every_relation_kind()
    {
        await MakeAsync();

        Dictionary<string, SourceTable> probed = await ProbeAsync();

        foreach (string relation in new[] { "t", "v", "j", "m" })
        {
            Assert.True(
                probed.ContainsKey(relation),
                $"The probe did not list `{relation}`, so the two catalogues cannot be "
                + "compared and one of them has stopped seeing a relation kind the other "
                + "serves — which is the original shape of D-231.");

            Assert.Equal(probed[relation].Writable, await WritableAsync(relation));
        }
    }

    /// <summary>The role used for the no-readable-column case.</summary>
    private const string Blinkered = "zzzwritable_blinkered";

    /// <summary>What the describe says about one of this schema's relations.</summary>
    /// <param name="relation">The table or view.</param>
    /// <returns>Its writability, which must not be unknown for a relation that exists.</returns>
    private async Task<bool> WritableAsync(string relation)
    {
        LayerDescription described = await new PostGisFeatureSource(DataSource, Over(relation))
            .DescribeAsync(CancellationToken.None);

        Assert.True(
            described.Writable is not null,
            $"`{relation}` exists and the shape reported its writability as unknown. Unknown "
            + "means nobody asked, and nothing narrows on it — so an unknown here is the "
            + "over-claim D-231 records, arriving through a different door.");

        return described.Writable!.Value;
    }

    /// <summary>The layer, over one of this schema's relations.</summary>
    /// <param name="relation">The table or view.</param>
    /// <returns>The definition.</returns>
    private LayerDefinition Over(string relation) =>
        new(relation, SchemaName, relation, "geom", 4326, "id", "id", isHosted: false);

    /// <summary>What the probe makes of this schema.</summary>
    /// <returns>Its relations by name.</returns>
    private async Task<Dictionary<string, SourceTable>> ProbeAsync()
    {
        // <b>The environment's string, not the data source's.</b> Npgsql redacts the password
        // out of a live `NpgsqlDataSource.ConnectionString`, so handing that to the probe is a
        // connection attempt with no credential — reported, correctly, as `CannotConnect`.
        string connectionString =
            Environment.GetEnvironmentVariable("GRATICULA_TEST_PG")
            ?? throw new InvalidOperationException(
                "GRATICULA_TEST_PG is not set, so nothing can be probed.");

        ProbeResult result = await new PostgresDataSourceProbe()
            .ProbeAsync(connectionString, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Usable, result.Outcome);

        return result.Tables
            .Where(t => string.Equals(t.SchemaName, SchemaName, StringComparison.Ordinal))
            .ToDictionary(t => t.TableName, StringComparer.Ordinal);
    }

    /// <summary>A pool that runs as another role.</summary>
    /// <param name="role">Whose privileges to run under.</param>
    /// <returns>The pool, which the caller disposes.</returns>
    /// <remarks>
    /// <b><c>set role</c> rather than a second login</b>, because the role then needs no
    /// password and no <c>pg_hba</c> entry, and what is under test is the privilege check
    /// rather than authentication.
    /// </remarks>
    private static NpgsqlDataSource As(string role)
    {
        string? raw = Environment.GetEnvironmentVariable("GRATICULA_TEST_PG");

        Assert.False(
            string.IsNullOrWhiteSpace(raw),
            "GRATICULA_TEST_PG is not set, so this test cannot open a second pool as a narrower "
            + "role. The fixture's own pool cannot be reused: what is under test is what a "
            + "*different* credential sees.");

        NpgsqlDataSourceBuilder builder = new(raw);

        // Both initializers, because Npgsql refuses one: the pool may open a connection either
        // way, and a half-initialized one runs as the fixture's own role — which is the role
        // this is deliberately not.
        builder.UsePhysicalConnectionInitializer(
            connection =>
            {
                using NpgsqlCommand become = new($"set role {role}", connection);
                become.ExecuteNonQuery();
            },
            async connection =>
            {
                await using NpgsqlCommand become = new($"set role {role}", connection);
                await become.ExecuteNonQueryAsync().ConfigureAwait(false);
            });

        return builder.Build();
    }

    /// <summary>Builds one of each relation kind over the same table.</summary>
    /// <returns>The work.</returns>
    /// <remarks>
    /// <b>All four over one table on purpose.</b> The kinds differ in exactly one thing —
    /// whether PostgreSQL can turn a write into a write on a real row — and holding the columns,
    /// the geometry and the identity constant is what makes the four answers comparable.
    /// </remarks>
    private async Task MakeAsync() =>
        await ExecuteAsync($"""
            create table "{SchemaName}".t (
              id serial primary key,
              label text,
              geom geometry(Point, 4326));

            create table "{SchemaName}".owners (id integer primary key, owner text);

            -- Auto-updatable: one table, no aggregate, no join. PostgreSQL rewrites writes
            -- onto `t` and they succeed.
            create view "{SchemaName}".v as
              select id, label, geom from "{SchemaName}".t;

            -- Not updatable: a view selecting from two relations has no single row to write
            -- to, and PostgreSQL says so rather than guessing.
            create view "{SchemaName}".j as
              select t.id, t.label, o.owner, t.geom
              from "{SchemaName}".t t
              join "{SchemaName}".owners o on o.id = t.id;

            -- Not updatable, and servable: a materialized view with a unique single-column
            -- integer index has an object id, so it publishes and an ArcGIS client can query
            -- it. That is what made this the case D-231 was found on.
            create materialized view "{SchemaName}".m as
              select id, label, geom from "{SchemaName}".t;

            create unique index m_id on "{SchemaName}".m (id);
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
