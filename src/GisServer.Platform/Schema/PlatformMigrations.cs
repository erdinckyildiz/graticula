namespace GisServer.Platform.Schema;

/// <summary>
/// The platform store's schema history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append only — from the first tagged release.</b> A migration that has
/// shipped is never edited, because a store that already ran it will not run it
/// again and the two would silently diverge.
/// </para>
/// <para>
/// <b>Before that release, editing a migration is correct and this says so
/// deliberately.</b> No store exists that we do not control: the only databases
/// that have run these are throwaway test schemas. Adding a column by appending
/// a migration nobody needed would leave the initial schema permanently odd, to
/// honour a rule whose purpose — preventing divergence between stores — cannot
/// yet be served. The rule binds at v1.0.0, and the reason it does not bind now
/// is written here so the judgement is not re-made silently later.
/// </para>
/// <para>
/// Scope is <c>docs/v1-scope.md</c>: PostGIS only, ArcGIS FeatureServer,
/// VectorTileServer and GeometryServer, over hosted and registered data.
/// </para>
/// </remarks>
public static class PlatformMigrations
{
    /// <summary>The schema level this build was written against.</summary>
    public static SchemaVersion ComponentSchemaVersion => new(2);

    /// <summary>Every migration, in order.</summary>
    public static MigrationSet All { get; } = new(
    [
        CatalogueV1,
        IdentityV2,
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
        // geometry_type is declared rather than inferred from the data. ArcGIS
        // puts it in the response header before any row has been read, and a
        // layer whose query matches nothing still has a type — inferring from
        // the first feature would make an empty result untypeable.
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
            geometry_type     text        not null,
            is_hosted         boolean     not null,
            created_at        timestamptz not null default now(),
            updated_at        timestamptz not null default now(),
            constraint layer_name_not_blank check (length(btrim(name)) > 0),
            constraint layer_srid_positive check (srid > 0),
            constraint layer_geometry_type_known check (geometry_type in (
                'Point', 'MultiPoint', 'LineString', 'MultiLineString', 'Polygon', 'MultiPolygon')),
            constraint layer_table_unique unique (data_source_id, schema_name, table_name, geometry_column)
        )
        """,

        "create index layer_data_source_idx on layer (data_source_id)");

    /// <summary>
    /// Identity: principals, local credentials, sessions and API keys
    /// ([ADR-015](../../../docs/adr/ADR-015-authentication.md)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No role set is defined here.</b> Q-59 — what the roles are and what
    /// each may do — is open, and independent review O3 found that citing
    /// "administrators only" as a control while the term is undefined is not a
    /// control at all. The <em>shape</em> is created; the contents are not
    /// invented.
    /// </para>
    /// <para>
    /// <b>Nothing stores a token.</b> Sessions and API keys hold a hash. A table
    /// of live bearer tokens is a credential database, and the whole argument for
    /// opaque server-side tokens (ADR-015 §3) was that revocation should work —
    /// which is undermined if a database dump hands over every live credential.
    /// </para>
    /// </remarks>
    private static Migration IdentityV2 => Migration.Expand(
        new SchemaVersion(2),
        "Create principals, local credentials, sessions, API keys and the role shape.",

        // disabled_at rather than a boolean: when matters as much as whether,
        // and ADR-015 §3 promises an administrator can see and terminate.
        """
        create table principal (
            id           uuid        not null primary key,
            kind         text        not null,
            name         text        not null unique,
            display_name text        null,
            created_at   timestamptz not null default now(),
            disabled_at  timestamptz null,
            constraint principal_kind_known check (kind in ('user', 'service', 'anonymous')),
            constraint principal_name_not_blank check (length(btrim(name)) > 0)
        )
        """,

        // Anonymous is a principal (ADR-015 §2a), so it exists as a row rather
        // than as a null check scattered through the authorization code. Seeded
        // here because it is structural, not configuration.
        """
        insert into principal (id, kind, name, display_name)
        values ('00000000-0000-0000-0000-000000000001', 'anonymous', 'anonymous', 'Anonymous')
        """,

        // Separate from principal because most principals have no password: a
        // service authenticates with a key, an OIDC user authenticates elsewhere,
        // and anonymous authenticates not at all.
        //
        // algorithm and parameters are stored per row so that a future hardening
        // — Argon2id cost increase, or a different algorithm — can re-hash on
        // next login rather than invalidating every password at once.
        """
        create table local_credential (
            principal_id  uuid        not null primary key references principal (id) on delete cascade,
            algorithm     text        not null,
            parameters    jsonb       not null,
            password_hash bytea       not null,
            updated_at    timestamptz not null default now()
        )
        """,

        // token_hash, never the token. Unique so a hash collision or a duplicate
        // issue is a constraint violation rather than two live sessions.
        """
        create table session (
            id             uuid        not null primary key,
            principal_id   uuid        not null references principal (id) on delete cascade,
            token_hash     bytea       not null unique,
            created_at     timestamptz not null default now(),
            expires_at     timestamptz not null,
            revoked_at     timestamptz null,
            source_address inet        null,
            constraint session_expiry_after_creation check (expires_at > created_at)
        )
        """,

        "create index session_principal_idx on session (principal_id)",

        // Long-lived by nature, so scoped narrowly and revocable (ADR-015 §5).
        """
        create table api_key (
            id           uuid        not null primary key,
            principal_id uuid        not null references principal (id) on delete cascade,
            name         text        not null,
            token_hash   bytea       not null unique,
            created_at   timestamptz not null default now(),
            expires_at   timestamptz null,
            revoked_at   timestamptz null,
            last_used_at timestamptz null,
            constraint api_key_name_unique_per_principal unique (principal_id, name)
        )
        """,

        // The shape only. Q-59 decides what roles exist and what each carries;
        // inventing them here would be the "administrators only" problem review
        // O3 named — a control resting on a term nobody has defined.
        """
        create table role (
            name        text not null primary key,
            description text not null,
            constraint role_name_not_blank check (length(btrim(name)) > 0)
        )
        """,

        """
        create table principal_role (
            principal_id uuid        not null references principal (id) on delete cascade,
            role_name    text        not null references role (name) on delete restrict,
            granted_at   timestamptz not null default now(),
            granted_by   uuid        null references principal (id) on delete set null,
            constraint principal_role_pk primary key (principal_id, role_name)
        )
        """,

        // Rate limiting state, in the store rather than in memory. Two reasons,
        // and the second is the one that decides it: a restart must not clear an
        // attacker's budget, and ADR-007's deployment model allows more than one
        // worker, where in-memory counters mean the limit is really N times what
        // it says.
        //
        // The name attempted is recorded, not a principal id, because a guess at
        // a name that does not exist must be counted too — counting only real
        // accounts turns the endpoint into a free enumeration oracle.
        """
        create table login_attempt (
            id             uuid        not null primary key,
            attempted_name text        not null,
            source_address inet        null,
            attempted_at   timestamptz not null default now(),
            succeeded      boolean     not null
        )
        """,

        // Both indexes carry attempted_at because every read is windowed. A
        // lookup by name alone would scan an ever-growing history to answer a
        // question about the last fifteen minutes.
        "create index login_attempt_name_idx on login_attempt (attempted_name, attempted_at)",
        "create index login_attempt_address_idx on login_attempt (source_address, attempted_at)",

        // ADR-015 §6. In the store rather than in memory because condition 4
        // requires the token to survive a restart that happens mid-setup and
        // still be single-use: an in-memory token would be reissued on restart,
        // which is a second valid token rather than the same one.
        //
        // used_at is what makes it single-use, and it is set in the same
        // transaction as the administrator it creates.
        """
        create table setup_token (
            id         uuid        not null primary key,
            token_hash bytea       not null unique,
            created_at timestamptz not null default now(),
            expires_at timestamptz not null,
            used_at    timestamptz null,
            constraint setup_token_expiry_after_creation check (expires_at > created_at)
        )
        """);
}
