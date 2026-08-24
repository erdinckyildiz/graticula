using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Graticula.Providers.PostGis;

/// <summary>
/// Whether the spatial reference a publisher declared is the one the table holds.
/// </summary>
/// <param name="Declared">The SRID the publish request asked for.</param>
/// <param name="Stored">
/// The SRID the geometry column actually carries, or <see langword="null"/> when the
/// table holds no geometry to ask.
/// </param>
/// <param name="Complaint">
/// What is wrong in one sentence, or <see langword="null"/> when nothing is.
/// </param>
/// <remarks>
/// <para>
/// <b>[D-156](../../../docs/architecture-debt.md), and the override it guards is
/// deliberate.</b> [geometry-crs-policy](../../../docs/geometry-crs-policy.md) §2 asks
/// for a publisher to be able to say <em>the declared SRID is wrong, treat this as
/// EPSG:x</em>, because a registered read-only source cannot be corrected at its own
/// table. That override shipped. The two obligations §2 attaches in the same breath —
/// <em>detect at registration with a cheap heuristic</em> and <em>refuse or warn
/// loudly; never publish silently over a detected mismatch</em> — did not, so the
/// capability arrived without the sentence that makes it safe.
/// </para>
/// <para>
/// <b>Two checks, and only the first is certain.</b> Comparing the declared SRID
/// against <c>ST_SRID</c> is exact: the table says what it holds. Comparing the
/// coordinates against the declared reference's domain is a heuristic and is named as
/// one — it catches the common failure §2 describes, <em>a table declared 4326 holding
/// projected metres</em>, and it cannot catch a table declared in the wrong projected
/// system, because metres look like metres.
/// </para>
/// <para>
/// <b>What this never does is guess the right answer.</b> It reports what it found and
/// leaves the publisher to say what they meant, for the same reason
/// <see cref="GeometryValidity"/> counts invalid rows and never calls
/// <c>ST_MakeValid</c>: a server that silently changes what it was handed is a server
/// nobody can reason about.
/// </para>
/// </remarks>
public sealed record DeclaredReference(int Declared, int? Stored, string? Complaint)
{
    /// <summary>Whether the declaration and the table agree, as far as this can tell.</summary>
    public bool Agrees => Complaint is null;

    /// <summary>
    /// Asks the table what reference it holds, and whether its coordinates could be in it.
    /// </summary>
    /// <param name="source">The data source the table lives in.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">The geometry column.</param>
    /// <param name="declared">The SRID the publisher asked for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was found, or null if the table could not be asked.</returns>
    /// <remarks>
    /// <b>Three round trips, and §2 called this <em>one query</em>.</b> Building the
    /// statement server-side with <c>format(%I)</c> costs one, asking
    /// <c>spatial_ref_sys</c> whether the declared code is geographic costs another, and
    /// the extent itself is the third. The first two are constant-time lookups; the
    /// extent is the only one that touches the table, and it reads an index rather than
    /// scanning. So the shape §2 was protecting — a publish that makes somebody wait —
    /// holds, and the count does not.
    /// </remarks>
    public static async Task<DeclaredReference?> MeasureAsync(
        NpgsqlDataSource source,
        string schema,
        string table,
        string column,
        int declared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        // <b>Quoted by PostgreSQL rather than by us — the same two-step
        // <see cref="GeometryValidity"/> uses, and for the same reason.</b> There is no
        // parameter form for an identifier in SQL, so `format(… %I …)` builds the
        // statement server-side and the identifiers never pass through a string of ours.
        //
        // <b>`ST_Extent` ignores the SRID and returns the stored numbers</b>, which is
        // what a check on the declaration has to read: asking PostGIS to transform first
        // would answer in the reference this is trying to verify.
        const string Sql = """
            select format(
              'select max(st_srid(%1$I))          as stored,
                      st_xmin(st_extent(%1$I))    as xmin,
                      st_ymin(st_extent(%1$I))    as ymin,
                      st_xmax(st_extent(%1$I))    as xmax,
                      st_ymax(st_extent(%1$I))    as ymax
                 from %2$I.%3$I
                where %1$I is not null',
              $1, $2, $3)
            """;

        string measure;

        await using (NpgsqlCommand build = source.CreateCommand(Sql))
        {
            build.Parameters.AddWithValue(column);
            build.Parameters.AddWithValue(schema);
            build.Parameters.AddWithValue(table);

            measure = (string)(await build.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false))!;
        }

        // <b>Asked of the database rather than derived from the number.</b> A range of
        // EPSG codes is a copy of somebody else's register that goes stale silently, which
        // is the argument [Q-123](../../../docs/open-questions.md) makes against exactly
        // that shortcut. `spatial_ref_sys` is the deployment's own answer, and a code it
        // does not know comes back null and is reported as unknown rather than as safe.
        bool? geographic;

        await using (NpgsqlCommand ask = source.CreateCommand(
            "select srtext like 'GEOGCS%' from spatial_ref_sys where srid = $1"))
        {
            ask.Parameters.AddWithValue(declared);

            object? answer = await ask.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);

            geographic = answer is bool value ? value : null;
        }

        await using NpgsqlCommand command = source.CreateCommand(measure);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int? stored = reader.IsDBNull(0) ? null : reader.GetInt32(0);

        // An empty table declares nothing and proves nothing. Publishing one is
        // ordinary — a hosted layer starts empty — so this is silence, not a finding.
        if (stored is null || reader.IsDBNull(1))
        {
            return new DeclaredReference(declared, stored, null);
        }

        double xmin = reader.GetDouble(1);
        double ymin = reader.GetDouble(2);
        double xmax = reader.GetDouble(3);
        double ymax = reader.GetDouble(4);

        if (stored != declared && stored != 0)
        {
            return new DeclaredReference(
                declared, stored,
                $"the column holds EPSG:{stored} and this asked for EPSG:{declared}");
        }

        // <b>The heuristic §2 names, and it is stated as a heuristic.</b> Degrees have a
        // domain; a table declared geographic whose coordinates leave it is holding
        // something else, and the usual something else is metres.
        if (geographic is true
            && (xmin < -180 || xmax > 180 || ymin < -90 || ymax > 90))
        {
            return new DeclaredReference(
                declared, stored,
                $"EPSG:{declared} is a geographic system measured in degrees, and this "
                + $"table's coordinates run from ({xmin:F0}, {ymin:F0}) to ({xmax:F0}, "
                + $"{ymax:F0}) — outside ±180/±90, so they are not degrees");
        }

        // <b>The other direction, and it is weaker on purpose.</b> A projected table whose
        // whole extent fits inside the degree box is *probably* degrees under a projected
        // code — and a small survey near the origin of its own grid would look identical,
        // so this reports and does not refuse on its own account. Whoever reads it knows
        // their data; this only makes sure somebody looks.
        if (geographic is false
            && xmin >= -180 && xmax <= 180 && ymin >= -90 && ymax <= 90)
        {
            return new DeclaredReference(
                declared, stored,
                $"EPSG:{declared} is a projected system, and this table's whole extent fits "
                + $"inside ±180/±90 — ({xmin:F4}, {ymin:F4}) to ({xmax:F4}, {ymax:F4}) — which "
                + "is what degrees stored under a projected code look like");
        }

        return new DeclaredReference(declared, stored, null);
    }
}
