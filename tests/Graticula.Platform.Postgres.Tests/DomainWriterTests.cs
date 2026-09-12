using System;
using System.Collections.Generic;
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
/// The writer's half of domains and subtypes — ADR-065, and ADR-013 condition 5: a domain the
/// document reports is a domain a write cannot get around.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the overrides, as <see cref="EditorTrackingWriterTests"/> is</b>: each writer is
/// built from the table's own description with the overrides applied by
/// <see cref="FieldOverrides.Apply"/>, the path the server takes, so a domain that failed to reach
/// the writer fails here.
/// </para>
/// <para>
/// <b>Every refusal is asserted to have written nothing</b>, because a refusal that wrote anyway
/// is the worse of the two defects and a result's <c>success: false</c> does not show it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DomainWriterTests : PostgresFixture
{
    private const int Srid = 3857;

    private static readonly FieldDomain Materials = FieldDomain.Coded(
        "Material",
        [
            new CodedValue(DomainValue.Of("CU"), "Copper"),
            new CodedValue(DomainValue.Of("PVC"), "PVC"),
            new CodedValue(DomainValue.Of("DI"), "Ductile iron"),
        ]);

    private static readonly FieldDomain Diameters = FieldDomain.Range("Diameter", DomainValue.Of(50), DomainValue.Of(1200));

    [Fact]
    public async Task An_add_outside_a_column_s_domain_is_refused_by_name_and_writes_nothing()
    {
        PostGisFeatureWriter writer = await WriterAsync("dm_adds", withSubtypes: false);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add(("material", "CU"), ("diameter", 300)), Add(("material", "LEAD"), ("diameter", 300))],
                [], [], RollbackOnFailure: false),
            CancellationToken.None);

        Assert.True(outcome.Adds[0].Succeeded, outcome.Adds[0].Error);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Contains("'Material'", outcome.Adds[1].Error!, StringComparison.Ordinal);
        Assert.Contains("'CU' (Copper)", outcome.Adds[1].Error!, StringComparison.Ordinal);

        EditOutcome range = await writer.ApplyAsync(
            new EditBatch([Add(("material", "PVC"), ("diameter", 5000))], [], []),
            CancellationToken.None);

        Assert.False(range.Adds[0].Succeeded);
        Assert.Contains("outside the domain 'Diameter'", range.Adds[0].Error!, StringComparison.Ordinal);

        Assert.Equal(1L, await CountAsync("dm_adds"));
    }

    [Fact]
    public async Task An_update_outside_a_domain_is_refused_and_the_row_is_unchanged()
    {
        PostGisFeatureWriter writer = await WriterAsync("dm_updates", withSubtypes: false);
        long id = await RowAsync("dm_updates", kind: null, material: "CU", diameter: 100);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("diameter", 40))], []),
            CancellationToken.None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.Equal(100, await DiameterAsync("dm_updates", id));

        // A null is the column's nullability to decide, not the domain's.
        EditOutcome cleared = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("diameter", null))], []),
            CancellationToken.None);

        Assert.True(cleared.Updates[0].Succeeded, cleared.Updates[0].Error);
    }

    [Fact]
    public async Task The_subtype_column_takes_only_subtype_codes()
    {
        PostGisFeatureWriter writer = await WriterAsync("dm_kinds", withSubtypes: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("kind", (short)9), ("material", "CU"))], [], []),
            CancellationToken.None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("not one of this layer's subtypes", outcome.Adds[0].Error!, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync("dm_kinds"));
    }

    [Fact]
    public async Task A_subtype_s_domain_governs_its_features_and_only_its_features()
    {
        PostGisFeatureWriter writer = await WriterAsync("dm_subtype_adds", withSubtypes: true);

        // Main (1) allows only copper and ductile iron; Lateral (2) keeps the column's own list.
        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [
                    Add(("kind", (short)1), ("material", "DI")),
                    Add(("kind", (short)1), ("material", "PVC")),
                    Add(("kind", (short)2), ("material", "PVC")),
                    Add(("material", "PVC")),
                ],
                [], [], RollbackOnFailure: false),
            CancellationToken.None);

        Assert.True(outcome.Adds[0].Succeeded, outcome.Adds[0].Error);

        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Contains("for subtype 1 (Main)", outcome.Adds[1].Error!, StringComparison.Ordinal);

        Assert.True(outcome.Adds[2].Succeeded, outcome.Adds[2].Error);

        // No subtype sent: the column's own domain, which allows PVC.
        Assert.True(outcome.Adds[3].Succeeded, outcome.Adds[3].Error);
    }

    [Fact]
    public async Task An_update_that_does_not_say_its_subtype_is_checked_against_the_one_it_is()
    {
        PostGisFeatureWriter writer = await WriterAsync("dm_subtype_updates", withSubtypes: true);

        long main = await RowAsync("dm_subtype_updates", kind: 1, material: "CU", diameter: 300);
        long lateral = await RowAsync("dm_subtype_updates", kind: 2, material: "CU", diameter: 300);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [], [Change(main, ("material", "PVC")), Change(lateral, ("material", "PVC"))], [],
                RollbackOnFailure: false),
            CancellationToken.None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.Contains("for subtype 1 (Main)", outcome.Updates[0].Error!, StringComparison.Ordinal);
        Assert.True(outcome.Updates[1].Succeeded, outcome.Updates[1].Error);

        // Moving the Main feature to Lateral in the same edit is what makes PVC allowed.
        EditOutcome moved = await writer.ApplyAsync(
            new EditBatch([], [Change(main, ("kind", (short)2), ("material", "PVC"))], []),
            CancellationToken.None);

        Assert.True(moved.Updates[0].Succeeded, moved.Updates[0].Error);
    }

    [Fact]
    public async Task A_refused_value_in_an_all_or_nothing_batch_takes_the_batch_with_it()
    {
        PostGisFeatureWriter writer = await WriterAsync("dm_batch", withSubtypes: false);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("material", "CU")), Add(("material", "LEAD"))], [], []),
            CancellationToken.None);

        Assert.True(outcome.RolledBack);
        Assert.Equal(0L, await CountAsync("dm_batch"));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<PostGisFeatureWriter> WriterAsync(string name, bool withSubtypes)
    {
        await ExecuteAsync(
            $"""
            create table "{SchemaName}"."{name}" (
                objectid serial primary key,
                kind     smallint,
                material text,
                diameter integer,
                geom     geometry(Point, {Srid})
            )
            """);

        LayerDefinition layer = new(
            name: name,
            schemaName: SchemaName,
            tableName: name,
            geometryColumn: "geom",
            srid: Srid,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: true);

        LayerDescription table =
            await new PostGisFeatureSource(DataSource, layer).DescribeAsync(CancellationToken.None);

        List<FieldOverride> overrides =
        [
            new FieldOverride("material", null, false, Domain: Materials),
            new FieldOverride("diameter", null, false, Domain: Diameters),
        ];

        if (withSubtypes)
        {
            overrides.Add(new FieldOverride("kind", null, false, Subtypes: new LayerSubtypes(
                "kind",
                1,
                [
                    new Subtype(
                        1,
                        "Main",
                        new Dictionary<string, DomainValue>(),
                        new Dictionary<string, FieldDomain>
                        {
                            ["material"] = FieldDomain.Coded(
                                "MainMaterial",
                                [new CodedValue(DomainValue.Of("CU"), "Copper"), new CodedValue(DomainValue.Of("DI"), "Ductile iron")]),
                        }),
                    new Subtype(2, "Lateral", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
                ])));
        }

        LayerDescription described = FieldOverrides.Apply(table, overrides);

        Assert.NotNull(described.Find("material")!.Value.Domain);
        Assert.Equal(withSubtypes, described.Subtypes is not null);

        return new PostGisFeatureWriter(
            DataSource, layer, described.Fields, described.Tracking, described.Subtypes);
    }

    private async Task<long> RowAsync(string table, short? kind, string material, int diameter)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"insert into \"{SchemaName}\".\"{table}\" (kind, material, diameter) "
            + "values (@kind, @material, @diameter) returning objectid");
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlTypes.NpgsqlDbType.Smallint)
        {
            Value = (object?)kind ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("material", material);
        command.Parameters.AddWithValue("diameter", diameter);

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> CountAsync(string table)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select count(*) from \"{SchemaName}\".\"{table}\"");

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int?> DiameterAsync(string table, long id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select diameter from \"{SchemaName}\".\"{table}\" where objectid = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteScalarAsync() is int value ? value : null;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static FeatureAdd Add(params (string Column, object? Value)[] values)
    {
        Dictionary<string, object?> attributes = [];

        foreach ((string column, object? value) in values)
        {
            attributes[column] = value;
        }

        return new FeatureAdd(attributes, new Point(1, 2));
    }

    private static FeatureUpdate Change(long id, params (string Column, object? Value)[] values)
    {
        Dictionary<string, object?> attributes = [];

        foreach ((string column, object? value) in values)
        {
            attributes[column] = value;
        }

        return new FeatureUpdate(id, attributes, null);
    }
}
