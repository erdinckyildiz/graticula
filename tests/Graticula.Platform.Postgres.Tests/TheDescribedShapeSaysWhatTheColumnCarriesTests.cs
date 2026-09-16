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
/// The describe reads the geometry column's declaration, so the layer document can stop offering
/// an edit every save would refuse — ADR-074 §4.
/// </summary>
/// <remarks>
/// Written against a real column rather than a parsed string, because the parse is the easy half:
/// what had to be measured is that <c>postgis_typmod_type</c> answers the suffixed name for a
/// declared column and something without a suffix for a bare one, in the same round trip that
/// already reads the fields.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TheDescribedShapeSaysWhatTheColumnCarriesTests : PostgresFixture
{
    private async Task<LayerDescription> DescribedAsync(string name, string type)
    {
        await using (NpgsqlCommand create = DataSource.CreateCommand(
            $"create table \"{SchemaName}\".\"{name}\" (objectid serial primary key, label text, geom geometry({type}, 3857))"))
        {
            await create.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(
            name: name, schemaName: SchemaName, tableName: name, geometryColumn: "geom", srid: 3857,
            identityColumn: "objectid", integerIdentityColumn: "objectid", isHosted: true);

        return await new PostGisFeatureSource(DataSource, layer).DescribeAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("zm_flat", "Point", GeometryOrdinates.None)]
    [InlineData("zm_z", "PointZ", GeometryOrdinates.Z)]
    [InlineData("zm_m", "PointM", GeometryOrdinates.M)]
    [InlineData("zm_zm", "PointZM", GeometryOrdinates.Z | GeometryOrdinates.M)]
    [InlineData("zm_lines", "LineStringZ", GeometryOrdinates.Z)]
    public async Task The_column_s_declaration_is_what_is_described(
        string table, string type, GeometryOrdinates expected)
    {
        LayerDescription described = await DescribedAsync(table, type);

        Assert.Equal(expected, described.StoredOrdinates);

        // The fields are still the fields — this rides on the query that was already being sent.
        Assert.NotNull(described.Find("label"));
        Assert.Null(described.Find("geom"));
    }

    /// <summary>
    /// A column typed as bare <c>geometry</c> declares nothing, and nothing is claimed for it.
    /// </summary>
    /// <remarks>
    /// Such a column can hold a three-dimensional shape, and the honest report of a declaration
    /// that was never made is *none*: the alternative is reading every row to describe a layer.
    /// The write path asks each row what it actually carries and refuses there, so understating
    /// here costs a refusal somebody can read rather than an ordinate nobody gets back.
    /// </remarks>
    [Fact]
    public async Task An_undeclared_column_reports_nothing_rather_than_guessing()
    {
        await using (NpgsqlCommand create = DataSource.CreateCommand(
            $"create table \"{SchemaName}\".zm_bare (objectid serial primary key, geom geometry)"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand insert = DataSource.CreateCommand(
            $"insert into \"{SchemaName}\".zm_bare (geom) values (st_setsrid(st_makepoint(1, 2, 3), 3857))"))
        {
            await insert.ExecuteNonQueryAsync();
        }

        LayerDefinition layer = new(
            name: "zm_bare", schemaName: SchemaName, tableName: "zm_bare", geometryColumn: "geom",
            srid: 3857, identityColumn: "objectid", integerIdentityColumn: "objectid", isHosted: true);

        LayerDescription described =
            await new PostGisFeatureSource(DataSource, layer).DescribeAsync(CancellationToken.None);

        Assert.Equal(GeometryOrdinates.None, described.StoredOrdinates);

        // And the row really does carry a Z, which is what makes this the understating case
        // rather than an empty table proving nothing.
        await using NpgsqlCommand flag = DataSource.CreateCommand(
            $"select st_zmflag(geom) from \"{SchemaName}\".zm_bare");

        Assert.Equal(2, (short)(await flag.ExecuteScalarAsync())!);
    }
}
