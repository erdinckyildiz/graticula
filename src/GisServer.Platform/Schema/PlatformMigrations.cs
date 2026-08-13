namespace GisServer.Platform.Schema;

/// <summary>
/// The platform store's schema history.
/// </summary>
/// <remarks>
/// <para>
/// Append only. A migration that has shipped is never edited, because a store
/// that already ran it will not run it again and the two would silently diverge.
/// </para>
/// <para>
/// Scope is <c>docs/v1-scope.md</c>: PostGIS only, ArcGIS FeatureServer,
/// VectorTileServer and GeometryServer, over hosted and registered data.
/// </para>
/// </remarks>
public static class PlatformMigrations
{
    /// <summary>The schema level this build was written against.</summary>
    public static SchemaVersion ComponentSchemaVersion => new(1);

    /// <summary>Every migration, in order.</summary>
    public static MigrationSet All { get; } = new(
    [
        CatalogueV1,
    ]);

    /// <summary>
    /// The initial schema: the stamp itself, registered data sources, and
    /// published layers.
    /// </summary>
    private static Migration CatalogueV1 => Migration.Expand(
        new SchemaVersion(1),
        "Create the platform stamp, data source registrations and the layer catalogue.",

        // The stamp. Read before any other migration runs, so its absence is how
        // the migrator recognises an empty store (IPlatformSchemaStore.ReadStamp).
        // Single-row by constraint rather than by convention: two stamps would be
        // a store that disagrees with itself about what it is.
        """
        create table platform_schema (
            only_row               boolean     not null default true,
            applied_version        integer     not null,
            minimum_reader_version integer     not null,
            applied_at             timestamptz not null default now(),
            constraint platform_schema_pk primary key (only_row),
            constraint platform_schema_single_row check (only_row),
            constraint platform_schema_reader_not_ahead
                check (minimum_reader_version <= applied_version)
        )
        """,

        // A registered source. v1 is PostGIS only (Q-88), but the kind is stored
        // rather than assumed so that adding one later is a data change and not
        // a schema change.
        //
        // The connection secret is encrypted at rest with a key supplied
        // externally at startup (ADR-002 §4.7). key_version is here from the
        // start because independent review O7 found that rotation has no design
        // and that a restore across a rotation makes every credential
        // undecryptable with no diagnosis. Recording which key sealed a row is
        // what makes that diagnosable, and it cannot be added afterwards to rows
        // already written.
        """
        create table data_source (
            id                 uuid        not null primary key,
            name               text        not null unique,
            kind               text        not null,
            connection_secret  bytea       not null,
            key_version        integer     not null,
            created_at         timestamptz not null default now(),
            updated_at         timestamptz not null default now(),
            constraint data_source_kind_known check (kind in ('postgis')),
            constraint data_source_name_not_blank check (length(btrim(name)) > 0)
        )
        """,

        // A published layer over a table in a registered source.
        //
        // identity_column is declared, never inferred — Q-57. We do not
        // synthesise from row_number(), which is not stable across queries, and
        // we do not keep a side mapping table, which would be state about
        // somebody else's table and would drift on their first edit.
        //
        // object_id_column is nullable, and its nullability is ADR-013 §2a made
        // physical: OGC API Features accepts a string id, ArcGIS FeatureServer
        // requires a unique integer. A registered table keyed by uuid or text is
        // servable natively and not through the ArcGIS surface. Null here means
        // exactly that, and the capability report reads it rather than
        // discovering it during a request.
        """
        create table layer (
            id                uuid        not null primary key,
            name              text        not null unique,
            data_source_id    uuid        not null references data_source (id) on delete restrict,
            schema_name       text        not null,
            table_name        text        not null,
            geometry_column   text        not null,
            srid              integer     not null,
            identity_column   text        not null,
            object_id_column  text        null,
            is_hosted         boolean     not null,
            created_at        timestamptz not null default now(),
            updated_at        timestamptz not null default now(),
            constraint layer_name_not_blank check (length(btrim(name)) > 0),
            constraint layer_srid_positive check (srid > 0),
            constraint layer_table_unique unique (data_source_id, schema_name, table_name, geometry_column)
        )
        """,

        "create index layer_data_source_idx on layer (data_source_id)");
}
