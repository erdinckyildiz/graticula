using System;
using DuckDB.NET.Data;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A filter DuckDB cannot convert is the caller's mistake and answers 400, as the same filter does on PostGIS.
/// </summary>
/// <remarks>
/// <b>Found 2026-10-11 by a reviewer walking the showcase.</b> <c>where=objectid='x'</c> answered a PostGIS layer
/// with a 400 that said why and every GeoParquet, DuckDB and MotherDuck layer with a 500 — the headline Istanbul
/// buildings among them. The exception is DuckDB's own, raised here by asking DuckDB the same thing rather than
/// constructed, so a change in how DuckDB words it fails this test instead of passing it.
/// </remarks>
public sealed class ADuckDbConversionErrorIsTheCallersTests
{
    [Fact]
    public void A_value_DuckDB_cannot_convert_is_a_400_that_says_so()
    {
        using DuckDBConnection connection = new("DataSource=:memory:");
        connection.Open();

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "select 'x'::BIGINT";

        DuckDBException thrown = Assert.Throws<DuckDBException>(() => command.ExecuteScalar());

        ErrorResponse.Refusal refusal = ErrorResponse.Explain(thrown);

        Assert.Equal(400, refusal.Status);
        Assert.Contains("does not convert", refusal.Anonymous, StringComparison.Ordinal);
    }
}
