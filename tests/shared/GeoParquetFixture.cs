using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DuckDB.NET.Data;
using Graticula.Geometries;

namespace Graticula.Testing;

/// <summary>
/// Writes GeoParquet files in the shape GDAL writes them, for tests of the layer that reads them.
/// </summary>
/// <remarks>
/// <para>
/// <b>GDAL's shape rather than DuckDB's</b>, measured 2026-09-13 against a file the importer wrote
/// from a geodatabase: the geometry as a WKB blob, a <c>&lt;column&gt;_bbox</c> struct of four
/// single-precision corners, and a <c>geo</c> key naming the primary column, its reference as
/// PROJJSON, its geometry types, its bbox and the covering. DuckDB's own writer produces none of the
/// covering and refuses any reference but CRS84 — so a fixture it wrote would test a file nobody
/// serves.
/// </para>
/// <para>
/// <b>The covering corners are rounded to nearest, not outwards.</b> A writer that rounds outwards
/// makes a box test on the covering column exact; one that rounds to nearest can shrink a box by
/// half a float step, and the reader has to be right for both. So the fixture is the harder of the
/// two.
/// </para>
/// </remarks>
internal static class GeoParquetFixture
{
    private static readonly string[] Corners = ["xmin", "ymin", "xmax", "ymax"];

    /// <summary>An attribute column: its name and DuckDB type.</summary>
    public readonly record struct Column(string Name, string Type);

    /// <summary>Writes a file.</summary>
    /// <param name="path">Where.</param>
    /// <param name="columns">The attribute columns, in order.</param>
    /// <param name="rows">Each row's attribute values, in column order, and its shape.</param>
    /// <param name="srid">The EPSG code, or null to leave <c>crs</c> absent (which means CRS84).</param>
    /// <param name="geometryColumn">The geometry column's name.</param>
    /// <param name="covering">Whether to write the covering box column.</param>
    /// <param name="geo">The <c>geo</c> JSON to write instead of the one computed, for broken files.</param>
    public static void Write(
        string path,
        IReadOnlyList<Column> columns,
        IEnumerable<(object?[] Values, Geometry? Shape)> rows,
        int? srid = 4326,
        string geometryColumn = "geom",
        bool covering = true,
        string? geo = null)
    {
        using DuckDBConnection db = new("DataSource=:memory:");
        db.Open();

        StringBuilder create = new("create table t (");

        foreach (Column column in columns)
        {
            create.Append(Quote(column.Name)).Append(' ').Append(column.Type).Append(", ");
        }

        create.Append("g blob, xmin float, ymin float, xmax float, ymax float)");
        Execute(db, create.ToString());

        Envelope extent = Envelope.Empty;
        HashSet<string> kinds = [];

        using (DuckDBAppender appender = db.CreateAppender("t"))
        {
            foreach ((object?[] values, Geometry? shape) in rows)
            {
                IDuckDBAppenderRow row = appender.CreateRow();

                foreach (object? value in values)
                {
                    row = Append(row, value);
                }

                if (shape is null)
                {
                    row.AppendNullValue().AppendNullValue().AppendNullValue().AppendNullValue().AppendNullValue();
                }
                else
                {
                    Envelope box = shape.Envelope;
                    extent = extent.Union(box);
                    kinds.Add(shape.Kind.ToString());

                    row.AppendValue(WkbWriter.ToArray(shape))
                       .AppendValue((float)box.MinX).AppendValue((float)box.MinY)
                       .AppendValue((float)box.MaxX).AppendValue((float)box.MaxY);
                }

                row.EndRow();
            }
        }

        geo ??= Geo(geometryColumn, srid, kinds, extent, covering);

        string select = string.Join(", ", columns.Select(c => Quote(c.Name)))
            + (columns.Count > 0 ? ", " : string.Empty)
            + $"g as {Quote(geometryColumn)}"
            + (covering
                ? $", case when g is null then null else {{'xmin': xmin, 'ymin': ymin, 'xmax': xmax, 'ymax': ymax}} end as {Quote(geometryColumn + "_bbox")}"
                : string.Empty);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        Execute(db,
            $"copy (select {select} from t) to {Literal(path.Replace('\\', '/'))} "
            + $"(format parquet, row_group_size 1000, kv_metadata {{geo: {Literal(geo)}}})");
    }

    private static string Geo(string column, int? srid, HashSet<string> kinds, Envelope extent, bool covering)
    {
        StringBuilder json = new();
        json.Append("{\"version\":\"1.1.0\",\"primary_column\":\"").Append(column).Append("\",\"columns\":{\"")
            .Append(column).Append("\":{\"encoding\":\"WKB\",\"geometry_types\":[")
            .Append(string.Join(",", kinds.Order(StringComparer.Ordinal).Select(k => "\"" + k + "\"")))
            .Append(']');

        if (srid is { } code && code != 4326)
        {
            json.Append(",\"crs\":{\"type\":\"ProjectedCRS\",\"name\":\"EPSG ")
                .Append(code.ToString(CultureInfo.InvariantCulture))
                .Append("\",\"id\":{\"authority\":\"EPSG\",\"code\":")
                .Append(code.ToString(CultureInfo.InvariantCulture)).Append("}}");
        }

        if (!extent.IsEmpty)
        {
            json.Append(",\"bbox\":[")
                .Append(string.Join(",", new[] { extent.MinX, extent.MinY, extent.MaxX, extent.MaxY }
                    .Select(v => v.ToString("R", CultureInfo.InvariantCulture))))
                .Append(']');
        }

        if (covering)
        {
            string box = column + "_bbox";
            json.Append(",\"covering\":{\"bbox\":{")
                .Append(string.Join(",", Corners
                    .Select(c => $"\"{c}\":[\"{box}\",\"{c}\"]")))
                .Append("}}");
        }

        json.Append("}}}");
        return json.ToString();
    }

    private static IDuckDBAppenderRow Append(IDuckDBAppenderRow row, object? value) => value switch
    {
        null => row.AppendNullValue(),
        string s => row.AppendValue(s),
        int i => row.AppendValue(i),
        long l => row.AppendValue(l),
        short s => row.AppendValue(s),
        double d => row.AppendValue(d),
        float f => row.AppendValue(f),
        bool b => row.AppendValue(b),
        decimal m => row.AppendValue(m),
        DateTime t => row.AppendValue(t),
        DateOnly d => row.AppendValue(d),
        Guid g => row.AppendValue(g),
        byte[] bytes => row.AppendValue(bytes),
        _ => throw new ArgumentException($"The fixture writer has no arm for {value.GetType().Name}."),
    };

    private static void Execute(DuckDBConnection db, string sql)
    {
        using DuckDBCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
