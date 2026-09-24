using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Tiles;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>
/// Encodes rows handed to it — from anywhere — with the same <c>ST_AsMVTGeom</c>/<c>ST_AsMVT</c>
/// expression <see cref="PostGisTileSource"/> runs over a table, ADR-021.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-13 for <c>GeoParquetTileSource</c>, and this is the datastore round trip that
/// answer describes.</b> A GeoParquet layer's rows come from DuckDB; PostGIS still does the
/// encoding, which means the rows have to cross into PostgreSQL rather than starting there. The
/// shape is <c>PostGisProjector</c>'s: the batch travels as arrays bound to one statement,
/// <c>unnest(...) with ordinality</c> puts it back in order, and one round trip answers for
/// however many rows the tile holds.
/// </para>
/// <para>
/// <b>Attributes travel as <c>jsonb</c>, typed back out by the caller's own schema.</b> A tile's
/// tag columns are not fixed — every layer has its own — so there is no one row type to bind
/// arrays against the way <c>PostGisProjector</c> binds a <c>bytea[]</c> of geometries alone.
/// <c>jsonb_to_record</c> rebuilds a row shape from the field list <see cref="MvtTag"/>
/// already carries, casting each value to the PostgreSQL type its <see cref="FieldType"/> maps
/// to — the same type a hosted column of that kind would have, so <c>ST_AsMVT</c> tags it
/// identically either way.
/// </para>
/// <para>
/// <b>The same box test, the same buffer, the same constants.</b> <see cref="PostGisTileSource.Extent"/>,
/// <see cref="PostGisTileSource.Buffer"/> and <see cref="PostGisTileSource.WebMercator"/> are
/// reused rather than restated, so a change to one cannot silently stop applying to the other
/// source of a tile's bytes.
/// </para>
/// </remarks>
public sealed class PostGisMvtEncoder : IMvtEncoder
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates an encoder over the datastore.</summary>
    /// <param name="dataSource">The datastore's pool — the same one <see cref="PostGisProjector"/> uses,
    /// because encoding is a service of the datastore rather than of any registered database.</param>
    public PostGisMvtEncoder(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<byte[]> EncodeAsync(
        IReadOnlyList<MvtRow> rows,
        TileAddress address,
        string layerName,
        int srid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        if (rows.Count == 0)
        {
            return [];
        }

        // <b>The attribute shape is the first row's.</b> Every row here came from one
        // FeatureQuery over one layer, so every row names the same fields in the same order —
        // the schema a query answers with, not something each row negotiates for itself.
        IReadOnlyList<MvtTag> shape = rows[0].Attributes;

        byte[][] wkb = new byte[rows.Count][];
        string[] attrs = new string[rows.Count];

        for (int i = 0; i < rows.Count; i++)
        {
            wkb[i] = WkbWriter.ToArray(rows[i].Geometry);
            attrs[i] = AttributesJson(rows[i].Attributes, shape);
        }

        string sql = BuildSql(layerName, srid, shape);

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("z", address.Z);
        command.Parameters.AddWithValue("x", address.X);
        command.Parameters.AddWithValue("y", address.Y);
        command.Parameters.AddWithValue("srid", srid);
        command.Parameters.Add(new NpgsqlParameter("geometries", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = wkb,
        });
        command.Parameters.Add(new NpgsqlParameter("attrs", NpgsqlDbType.Array | NpgsqlDbType.Jsonb)
        {
            Value = attrs,
        });

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // Same reasoning as PostGisTileSource: an aggregate over rows every one of which was
        // clipped away by ST_AsMVTGeom is a correct empty tile, not a failure.
        return result as byte[] ?? [];
    }

    /// <summary>One row's tags as a JSON object, in <paramref name="shape"/>'s order.</summary>
    /// <remarks>
    /// <b>Written by hand rather than through a POCO</b>, because the column set is only known
    /// at run time — it is whichever attributes the tile query asked for, a different set for
    /// every layer.
    /// </remarks>
    private static string AttributesJson(
        IReadOnlyList<MvtTag> row, IReadOnlyList<MvtTag> shape)
    {
        using System.IO.MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            for (int i = 0; i < shape.Count; i++)
            {
                // <b>By position, not by re-finding the name.</b> Every row was built by the same
                // query as `shape`, so the i-th attribute is always the i-th column; a caller that
                // sent a differently-shaped row is a defect this exists to make loud rather than
                // to paper over with a lookup.
                object? value = i < row.Count ? row[i].Value : null;
                writer.WritePropertyName(shape[i].Name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case sbyte or byte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong u:
                writer.WriteNumberValue(u);
                return;
            case float f:
                writer.WriteNumberValue(f);
                return;
            case double d:
                writer.WriteNumberValue(d);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("o", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("o", CultureInfo.InvariantCulture));
                return;
            case Guid g:
                writer.WriteStringValue(g);
                return;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    /// <summary>
    /// The one statement: unnest the batch, cast each attribute back to its own type, and run
    /// the same <c>ST_AsMVTGeom</c>/<c>ST_AsMVT</c> <see cref="PostGisTileSource"/> runs — with the
    /// same generalisation by zoom (Q-157), from the same two methods, so a GeoParquet layer's tile
    /// leaves out and simplifies what a table's does.
    /// </summary>
    private static string BuildSql(string layerName, int srid, IReadOnlyList<MvtTag> shape)
    {
        string safeName = layerName.Replace("'", "''", StringComparison.Ordinal);
        bool native = srid == PostGisTileSource.WebMercator;

        // <b>The same two envelopes PostGisTileSource compares against</b>, for the same reason:
        // the `&&` test has to run in the row's own reference so a row that never reaches
        // ST_AsMVTGeom is excluded the same way a table row would be, and the output geometry has
        // to be in 3857 because that is what a tile is.
        string filterBox = native
            ? "bounds.geom"
            : $"ST_Transform(bounds.geom, {srid.ToString(CultureInfo.InvariantCulture)})";

        string outputGeometry = native
            ? "expanded.raw_geom"
            : "ST_Transform(expanded.raw_geom, 3857)";

        // <b>No <c>jsonb_to_record</c> at all when there are no tags.</b> Its record type cannot
        // be declared with zero columns, and a layer publishing no attribute tags is real — every
        // geometry-only layer is one.
        string expandedSelect = string.Empty;
        string expandedFrom = "input";
        string tileColumns = string.Empty;

        if (shape.Count > 0)
        {
            StringBuilder record = new();
            StringBuilder select = new();
            StringBuilder columns = new();

            for (int i = 0; i < shape.Count; i++)
            {
                string quoted = LayerDefinition.Quote(shape[i].Name);

                if (i > 0)
                {
                    record.Append(", ");
                }

                record.Append(quoted).Append(' ').Append(PgType(shape[i].Type));
                select.Append(", r.").Append(quoted).Append(" as ").Append(quoted);
                columns.Append(", expanded.").Append(quoted);
            }

            expandedSelect = select.ToString();
            expandedFrom = $"input cross join lateral jsonb_to_record(input.attrs) as r({record})";
            tileColumns = columns.ToString();
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             with bounds as (select ST_TileEnvelope(@z, @x, @y) as geom),
             input as (
                 select g.g as wkb, a.a as attrs
                 from unnest(@geometries) with ordinality as g(g, n)
                 join unnest(@attrs) with ordinality as a(a, n) using (n)
             ),
             expanded as (
                 select ST_SetSRID(ST_GeomFromWKB(input.wkb), @srid) as raw_geom{expandedSelect}
                 from {expandedFrom}
             ),
             tile as (
                 select ST_AsMVTGeom({PostGisTileSource.Generalised("o.g")}, bounds.geom, {PostGisTileSource.Extent},
                            {PostGisTileSource.Buffer}, true) as geom{tileColumns}
                 from expanded, bounds,
                      lateral (select {outputGeometry} as g) o
                 where expanded.raw_geom && {filterBox}
                   and {PostGisTileSource.LargeEnough("o.g")}
             )
             select ST_AsMVT(tile.*, '{safeName}', {PostGisTileSource.Extent}, 'geom') from tile
             """);
    }

    /// <summary>The PostgreSQL type a column of this <see cref="FieldType"/> would have, hosted.</summary>
    /// <remarks>
    /// <b>Matches <c>PostGisImporter</c>'s own mapping in the direction that matters here</b>: a
    /// value cast to the same type a hosted column of that kind carries reaches <c>ST_AsMVT</c>
    /// exactly as that column would, which is what makes the oracle test's bytes comparable at
    /// all. <see cref="FieldType.Unknown"/> and <see cref="FieldType.Binary"/> never reach this —
    /// the tile endpoint's own allow-list excludes both before a row is ever built — so both fall
    /// to <c>text</c> rather than being given a considered mapping.
    /// </remarks>
    private static string PgType(FieldType type) => type switch
    {
        FieldType.Boolean => "boolean",
        FieldType.SmallInteger => "smallint",
        FieldType.Integer => "integer",
        FieldType.BigInteger => "bigint",
        FieldType.Single => "real",
        FieldType.Double => "double precision",
        FieldType.Guid => "uuid",
        FieldType.Date => "timestamptz",
        _ => "text",
    };
}
