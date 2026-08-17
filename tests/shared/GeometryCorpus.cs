namespace Graticula.Testing;

/// <summary>
/// How the oracle suites find real geometry to compare against PostGIS.
/// </summary>
/// <remarks>
/// <para>
/// <b>One copy, compiled into both projects that need it.</b> This query lived
/// verbatim in <c>WorkerAgainstPostgisTests</c> and
/// <c>GeometryOperationsAgainstPostgisTests</c>, in different test projects. On
/// 2026-08-16 both broke for the same reason and the first fix went into one of
/// them — which is [D-46] exactly, committed in the same hour it was written down.
/// </para>
/// <para>
/// <b>Richest first, and that ordering is load-bearing.</b> It used to take the
/// alphabetically first table with a geometry column, which made the corpus depend
/// on a schema name: adding <c>tools/ci-corpus.sql</c> to a developer database
/// proved it, because <c>cicorpus</c> sorts before <c>hosted</c> and both suites
/// silently switched from real cadastral polygons to sixty generated ones and
/// stayed green. A test that quietly starts checking something easier is the
/// failure mode these suites exist to avoid.
/// </para>
/// <para>
/// <b>And polygonal, which was missing until lines outnumbered everything.</b>
/// These suites cut shapes and compare areas, so a table of lines gives ten shapes
/// that cannot be divided and an assertion comparing a shape against itself. They
/// said so and failed rather than passing — the design working — but they were
/// asking for "a geometry column" while needing "an area". Importing 46,041
/// OpenStreetMap road segments into a development database put lines at the top of
/// richest-first for the first time; the next dataset anybody imports would have
/// done the same.
/// </para>
/// <para>
/// <c>reltuples</c> is the planner's estimate rather than a count, which is the
/// point: counting every candidate would cost a sequential scan each, and an
/// estimate orders them correctly without reading a row.
/// </para>
/// </remarks>
internal static class GeometryCorpus
{
    /// <summary>
    /// Candidate tables holding areas, richest first.
    /// </summary>
    /// <remarks>
    /// <c>GEOMETRY</c> is accepted alongside the polygon types because a column
    /// declared without a type constraint may still hold polygons, and refusing it
    /// would exclude tables this server itself creates on import.
    /// </remarks>
    public const string PolygonTables = """
        select c.table_schema || '.' || quote_ident(c.table_name)
        from information_schema.columns c
        join pg_class p
          on p.relname = c.table_name
         and p.relnamespace = c.table_schema::regnamespace
        join geometry_columns g
          on g.f_table_schema = c.table_schema
         and g.f_table_name = c.table_name
         and g.f_geometry_column = c.column_name
        where c.udt_name = 'geometry'
          and c.column_name = 'geom'
          and upper(g.type) in ('POLYGON', 'MULTIPOLYGON', 'GEOMETRY')
          and c.table_schema not in ('gisserver', 'tiger', 'topology')
          -- <b>and not another test's private schema.</b> PostgresFixture gives
          -- each test class a gisserver_test_* schema and drops it at the end, so
          -- a class running in parallel can have its tables listed here and
          -- dropped before they are read. That surfaced as 42P01 on a table this
          -- query had just returned.
          and c.table_schema not like 'gisserver\_test\_%'
        group by 1, p.reltuples
        order by p.reltuples desc, 1
        """;
}
