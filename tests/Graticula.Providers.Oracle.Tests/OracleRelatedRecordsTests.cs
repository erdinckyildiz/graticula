using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>A relationship between two tables of one Oracle database, followed in one query (ADR-013, ADR-205).</summary>
[Trait("Needs", "Oracle")]
public sealed class OracleRelatedRecordsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static string Owner => OracleFixture.Source.User.ToUpperInvariant();

    private static async Task ExecuteAsync(string sql)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(OracleFixture.Locator, pooling: true), None);
        await using OracleCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(None);
    }

    [Fact]
    public async Task The_related_rows_of_several_features_come_back_grouped_and_typed()
    {
        await ExecuteAsync("drop table if exists zzr_owner purge");
        await ExecuteAsync("drop table if exists zzr_parcel purge");
        await ExecuteAsync("create table zzr_parcel (objectid number(10) primary key, parcel_no varchar2(10))");
        await ExecuteAsync("create table zzr_owner (objectid number(10) primary key, parcel_no varchar2(10), name varchar2(40), share_pct number(5))");
        await ExecuteAsync("insert into zzr_parcel values (1, 'P-1')");
        await ExecuteAsync("insert into zzr_parcel values (2, 'P-2')");
        await ExecuteAsync("insert into zzr_parcel values (3, 'P-3')");
        await ExecuteAsync("insert into zzr_owner values (10, 'P-1', 'Ayşe', 50)");
        await ExecuteAsync("insert into zzr_owner values (11, 'P-1', 'Mehmet', 50)");
        await ExecuteAsync("insert into zzr_owner values (12, 'P-2', 'Zeynep', 100)");

        LayerDefinition parcels = new("Parcels", Owner, "ZZR_PARCEL", "SHAPE", 4326, "OBJECTID", "OBJECTID", isHosted: false);
        LayerDefinition owners = new("Owners", Owner, "ZZR_OWNER", "SHAPE", 4326, "OBJECTID", "OBJECTID", isHosted: false);

        IReadOnlyList<FieldDescription> fields =
        [
            new("OBJECTID", FieldType.Integer, false, null),
            new("NAME", FieldType.Text, true, 40),
            new("SHARE_PCT", FieldType.SmallInteger, true, null),
        ];

        IReadOnlyList<RelatedGroup> groups = await new OracleRelatedRecords(OracleFixture.Locator, parcels, owners, sameDatabase: true)
            .QueryAsync("PARCEL_NO", "PARCEL_NO", [1, 2, 3], fields, 100, None);

        Assert.Equal([1L, 2L], groups.Select(g => g.ObjectId));
        Assert.Equal(["Ayşe", "Mehmet"], groups[0].Records.Select(r => (string?)r["NAME"]).Order());
        Assert.Equal((short)100, Assert.IsType<short>(groups[1].Records[0]["SHARE_PCT"]));
    }
}
