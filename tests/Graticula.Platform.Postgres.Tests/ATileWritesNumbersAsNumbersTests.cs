using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// What a vector tile says a value is — kept apart from <see cref="PostGisTileSourceTests"/>, which needs the
/// corpus and is filtered out of CI, so that this runs everywhere the suite runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ATileWritesNumbersAsNumbersTests : PostgresFixture
{
    /// <summary>
    /// A <c>numeric</c> column reaches a tile as a number, not as its text.
    /// </summary>
    /// <remarks>
    /// <c>ST_AsMVT</c> writes a type it does not know through its text output, so a registered table's
    /// <c>numeric(10,2)</c> arrived as the string <c>"12.50"</c>, and a style classifying on it drew nothing. Both
    /// halves are asserted on one table: as PostgreSQL gives it, and as the layer describes it (a double).
    /// </remarks>
    [Fact]
    public async Task A_numeric_column_reaches_a_tile_as_a_number()
    {
        await using (NpgsqlCommand setup = DataSource.CreateCommand("""
            drop table if exists numeric_probe;
            create table numeric_probe (
                objectid serial primary key,
                val      numeric(10,2),
                cnt      integer,
                geom     geometry(Point, 4326));
            insert into numeric_probe (val, cnt, geom)
            select i * 1.25, i, ST_SetSRID(ST_MakePoint(28.9 + i * 0.0005, 41.0 + i * 0.0005), 4326)
            from generate_series(1, 20) i;
            """))
        {
            await setup.ExecuteNonQueryAsync();
        }

        LayerDefinition points = new(
            name: "numeric-probe",
            schemaName: SchemaName,
            tableName: "numeric_probe",
            geometryColumn: "geom",
            srid: 4326,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: true);

        TileAddress address = new(12, 2376, 1535);

        PostGisTileSourceTests.Mvt.Feature raw = PostGisTileSourceTests.Mvt.Decode(await new PostGisTileSource(DataSource, points, ["val", "cnt"])
            .BuildAsync(address, "probe", CancellationToken.None)).Single().Features[0];
        PostGisTileSourceTests.Mvt.Feature typed = PostGisTileSourceTests.Mvt.Decode(await new PostGisTileSource(DataSource, points, ["val", "cnt"], numbers: ["val"])
            .BuildAsync(address, "probe", CancellationToken.None)).Single().Features[0];

        Assert.IsType<string>(raw.Attributes["val"]);
        Assert.IsType<double>(typed.Attributes["val"]);
        Assert.Equal(typed.Attributes["cnt"], raw.Attributes["cnt"]);
    }

}
