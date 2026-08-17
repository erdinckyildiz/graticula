using System;
using System.Collections.Generic;
using GisServer.Platform.Identity;

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
    public static SchemaVersion ComponentSchemaVersion => new(17);

    /// <summary>Every migration, in order.</summary>
    public static MigrationSet All { get; } = new(
    [
        CatalogueV1,
        IdentityV2,
        RolesV3,
        AuditV4,
        SharingV5,
        StatusV6,
        DatastoreV7,
        AttachmentQuotaV8,
        RelationshipsV9,
        SystemServicesV10,
        ServicesV11,
        GroupLayersV12,
        TileLifetimeV13,
        ServiceStyleV14,
        FolderCaseV15,
        ServiceCapabilitiesV16,
        ServiceCostCeilingsV17,
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

    /// <summary>
    /// The role set ADR-018 §2 decides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A separate migration rather than an edit to <see cref="IdentityV2"/>,
    /// and the reason is not the append-only rule</b> — that rule does not bind
    /// until v1.0.0 and the class remarks say so. It is that the role set is
    /// <c>INFERRED</c> (ADR-018 condition 1) and may change on one sentence from
    /// the project owner. Keeping it in its own migration means amending it
    /// touches one place, and it gives the version handshake a real 2 → 3
    /// upgrade to perform against a store that already exists.
    /// </para>
    /// <para>
    /// <b>Expand, and <c>minimum_reader_version</c> stays 1.</b> It only inserts
    /// rows. A server built against schema 2 reads this store perfectly well —
    /// it simply ignores rows in a table it never queries — so nothing here
    /// closes the rollback window (ADR-016 §4a).
    /// </para>
    /// <para>
    /// <b>Anonymous is granted nothing</b> (ADR-018 §3). A fresh server publishes
    /// nothing to the unauthenticated, and making a portal public is one grant.
    /// This is a behaviour change and it will look like a regression: until now
    /// every published layer was world-readable. The failure modes are not
    /// symmetric — a public portal that needs a grant is found in a minute by
    /// the person setting it up, and a private dataset that was public by
    /// default is found by someone else.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The role set and user types of ADR-018, which are ArcGIS Portal's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Amended 2026-08-14, one day after it was written</b>, when the owner
    /// directed that we adopt Portal's role and user-type capability matrix
    /// rather than the four roles we had invented. The previous version of this
    /// migration said in as many words that keeping the role seed in its own
    /// migration meant amending it would touch one place. This is that.
    /// </para>
    /// <para>
    /// Editing a shipped migration is correct before v1.0.0 and the class
    /// remarks say why: the only stores that have run it are ours.
    /// </para>
    /// <para>
    /// <b>Expand, and <c>minimum_reader_version</c> stays 1.</b> It inserts
    /// rows and adds one nullable column with a default, so a server built
    /// against an earlier schema still reads this store.
    /// </para>
    /// </remarks>
    private static Migration RolesV3 => Migration.Expand(
        new SchemaVersion(3),
        "Seed the ArcGIS Portal role set and user types from ADR-018.",
        RoleAndUserTypeStatements());

    /// <summary>
    /// The role and user-type rows, generated from the code that resolves them.
    /// </summary>
    /// <remarks>
    /// <b>Generated rather than typed.</b> The names exist in two places — the
    /// code that resolves privileges, and the rows a grant references — and two
    /// hand-maintained copies of one fact eventually disagree. When they do, a
    /// grant names something the server does not know, and the principal
    /// silently loses what it carried rather than getting an error.
    /// </remarks>
    private static string[] RoleAndUserTypeStatements()
    {
        List<string> statements = [];

        foreach (string role in Roles.All)
        {
            statements.Add(
                $"insert into role (name, description) values ('{Literal(role)}', "
                + $"'{Literal(Roles.DescriptionOf(role))}')");
        }

        // ADR-018 3a. A ceiling on what any role may confer, enforced so that
        // importing a Portal deployment (Q-16) cannot silently widen what the
        // source system granted.
        statements.Add(
            """
            create table user_type (
                name        text not null primary key,
                description text not null,
                constraint user_type_name_not_blank check (length(btrim(name)) > 0)
            )
            """);

        foreach (string userType in UserTypes.All)
        {
            statements.Add(
                $"insert into user_type (name, description) values ('{Literal(userType)}', "
                + $"'{Literal(UserTypes.DescriptionOf(userType))}')");
        }

        // Nullable with a default rather than not-null: a principal row written
        // by an older server must remain readable, and the default is the
        // no-ceiling type so nothing is withheld by accident.
        statements.Add(
            $"""
            alter table principal add column user_type text not null
              default '{Literal(UserTypes.Unrestricted)}'
              references user_type (name) on delete restrict
            """);

        return [.. statements];
    }

    private static string Literal(string value) =>
        value.Contains('\'', StringComparison.Ordinal)
            ? throw new InvalidOperationException(
                $"'{value}' contains a quote. These values are written into migration SQL as "
                + "literals, so this would need parameterised migrations rather than an escape.")
            : value;

    private static Migration AuditV4 => Migration.Expand(
        new SchemaVersion(4),
        "Create the administrative audit trail.",

        """
        create table audit_event (
            id             uuid        not null primary key,
            occurred_at    timestamptz not null default now(),
            principal_id   uuid        null references principal (id) on delete set null,
            principal_name text        not null,
            source_address inet        null,
            action         text        not null,
            resource       text        null,
            detail         jsonb       not null default '{}'::jsonb,
            succeeded      boolean     not null,
            constraint audit_event_action_not_blank check (length(btrim(action)) > 0)
        )
        """,

        // Newest first is the only order anyone reads an audit trail in.
        "create index audit_event_time_idx on audit_event (occurred_at desc)",

        // "What did this account do" and "what happened to this layer" are the
        // two questions asked, so both get an index rather than a scan.
        "create index audit_event_principal_idx on audit_event (principal_id, occurred_at desc)",
        "create index audit_event_resource_idx on audit_event (resource, occurred_at desc)");

    /// <summary>
    /// Ownership and sharing on layers (ADR-018 §3b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is how reading works</b>, not an enhancement to it. Portal has no
    /// read privilege: whether a caller may see an item comes from the item's
    /// owner and scope. Adopting Portal's matrix therefore made the sharing axis
    /// due immediately, having been deferred one day earlier.
    /// </para>
    /// <para>
    /// <b>Existing layers become private</b>, which is the safe direction and
    /// will look like a regression to whoever published them. ADR-018 condition
    /// 4 requires the upgrade to say so rather than let it be discovered.
    /// </para>
    /// <para>
    /// The scope is text, not an enum type, so Portal's fourth scope — shared
    /// with a group — arrives as a value rather than a migration.
    /// </para>
    /// </remarks>
    private static Migration SharingV5 => Migration.Expand(
        new SchemaVersion(5),
        "Add ownership and sharing scope to layers.",

        // No owner for a layer registered before ownership existed. Null rather
        // than assigning one arbitrarily: an owner nobody chose is a lie the
        // audit trail would then repeat.
        "alter table layer add column owner_principal_id uuid null "
        + "references principal (id) on delete set null",

        """
        alter table layer add column sharing text not null default 'private'
        """,

        """
        alter table layer add constraint layer_sharing_known
          check (sharing in ('private', 'organization', 'public'))
        """,

        // Every read filters on this, and a portal with one public layer among
        // a thousand private ones should not scan them all to find it.
        "create index layer_sharing_idx on layer (sharing)");

    /// <summary>
    /// Service status: started or stopped (ADR-020 §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the sharing scope.</b> Sharing answers <em>who may see this</em>
    /// and status answers <em>does it run at all</em>. Making a service
    /// unavailable by marking it private would hide it from everybody except
    /// its owner and administrators — who would then still hit a source that is
    /// mid-rebuild — and it would overwrite the sharing setting that has to be
    /// restored afterwards.
    /// </para>
    /// <para>
    /// <b>Default started.</b> A layer that has just been published is one
    /// somebody wanted; a second call to turn it on would make publishing a
    /// two-step operation for no benefit.
    /// </para>
    /// </remarks>
    private static Migration StatusV6 => Migration.Expand(
        new SchemaVersion(6),
        "Add operational status to layers.",

        "alter table layer add column status text not null default 'started'",

        """
        alter table layer add constraint layer_status_known
          check (status in ('started', 'stopped'))
        """);

    /// <summary>
    /// Marks which registered source is the datastore, so <c>is_hosted</c> can
    /// stop being a column nobody ever sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug this repairs.</b> <c>layer.is_hosted</c> has existed since
    /// version 1 and every insert wrote <c>false</c>. Nothing could ever be
    /// hosted, which made [Q-67]'s rule — vector tiles come only from hosted
    /// data — refuse every layer in existence. The VectorTileServer surface was
    /// built, correct, and unreachable.
    /// </para>
    /// <para>
    /// <b>Hosted is derived, not declared.</b> A layer is hosted when its data
    /// lives in the datastore, and the datastore is a specific registered
    /// source rather than a property somebody ticks at publish time. Deriving it
    /// means the two facts cannot drift: there is no way to publish against an
    /// external Oracle and mark it hosted, and no way to move a table into the
    /// datastore and forget to.
    /// </para>
    /// <para>
    /// <b>At most one.</b> The partial unique index is the whole guarantee —
    /// [ADR-019](../../../docs/adr/ADR-019-portal-server-split.md) fuses one
    /// datastore into the product and [Q-69] makes it mandatory, so two would
    /// mean *hosted* had two answers and Q-67's rule had none.
    /// </para>
    /// <para>
    /// <b>Expand, so the reader floor does not move.</b> The column has a
    /// default and the index is partial; a version 6 reader ignores both and
    /// keeps working, which is what makes this safe to apply before every node
    /// is upgraded.
    /// </para>
    /// </remarks>
    private static Migration DatastoreV7 => Migration.Expand(
        new SchemaVersion(7),
        "Mark which registered source is the datastore, so hosted data can exist.",

        "alter table data_source add column is_datastore boolean not null default false",

        """
        create unique index data_source_one_datastore
          on data_source ((true)) where is_datastore
        """);

    /// <summary>
    /// Gives every layer an attachment quota, because attachments cannot ship
    /// without one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-013](../../../docs/adr/ADR-013-feature-service-data-model.md) §4e
    /// states this as a precondition, not a follow-up:</b> the datastore is
    /// mandatory and is about to contain arbitrary user binaries, so its backup
    /// size stops being a function of feature count and grows without bound.
    /// *"Per-layer quotas are the control, and they must exist before
    /// attachments ship rather than after the first full disk."*
    /// </para>
    /// <para>
    /// <b>One gigabyte, and it is a starting point rather than a finding.</b> It
    /// is large enough that a few thousand photographs fit and small enough that
    /// a runaway upload loop is noticed before it fills a volume. Per-layer so
    /// one layer cannot consume the appliance; adjustable because the right
    /// number is a property of the deployment and nobody here knows it.
    /// </para>
    /// <para>
    /// <b>Expand.</b> A column with a default; a version 7 reader ignores it
    /// entirely, so <c>minimum_reader_version</c> does not move.
    /// </para>
    /// </remarks>
    private static Migration AttachmentQuotaV8 => Migration.Expand(
        new SchemaVersion(8),
        "Give every layer an attachment quota, which ADR-013 §4e requires before attachments ship.",

        "alter table layer add column attachment_quota_bytes bigint not null default 1073741824",

        """
        alter table layer add constraint layer_attachment_quota_not_negative
          check (attachment_quota_bytes >= 0)
        """);

    /// <summary>
    /// Declared relationships between layers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared, not reverse-engineered</b> —
    /// [ADR-013](../../../docs/adr/ADR-013-feature-service-data-model.md) §3.
    /// Reading relationship classes out of a geodatabase's <c>GDB_ITEMS</c>
    /// tables is reverse-engineering Esri internals, which CLAUDE.md §5 forbids;
    /// it only works when the source is a geodatabase, which most PostGIS
    /// schemas are not; and it breaks silently whenever Esri changes the layout.
    /// </para>
    /// <para>
    /// <b>Which makes this strictly more capable than the model it replaces.</b>
    /// An administrator can relate two ordinary tables that were never designed
    /// to be related, on any supported database, with no geodatabase anywhere.
    /// </para>
    /// <para>
    /// <b>The keys are column names, and nothing here can check them.</b> A
    /// declaration is metadata; whether <c>parcels.parcel_id</c> actually joins
    /// to <c>owners.parcel_id</c> is a fact about two tables this row does not
    /// see. §7's condition is that publish validates it, and that validation
    /// lives in the admin API where both tables can be reached.
    /// </para>
    /// <para>
    /// <b>Expand.</b> A new table only; nothing existing is touched, so
    /// <c>minimum_reader_version</c> stays where it is and a version 8 reader
    /// runs unchanged — it simply reports no relationships.
    /// </para>
    /// </remarks>
    private static Migration RelationshipsV9 => Migration.Expand(
        new SchemaVersion(9),
        "Declared relationships between layers (ADR-013 §3).",

        """
        create table relationship (
            id                uuid        not null primary key,
            name              text        not null unique,
            origin_layer_id   uuid        not null references layer (id) on delete cascade,
            origin_key        text        not null,
            related_layer_id  uuid        not null references layer (id) on delete cascade,
            related_key       text        not null,
            cardinality       text        not null,
            composite         boolean     not null default false,
            created_at        timestamptz not null default now(),
            constraint relationship_cardinality_known
              check (cardinality in ('OneToOne', 'OneToMany')),
            constraint relationship_name_not_blank
              check (length(btrim(name)) > 0),
            constraint relationship_not_reflexive
              check (origin_layer_id <> related_layer_id or origin_key <> related_key)
        )
        """,

        // Both directions are queried — a client asks for a parcel's owners and
        // for an owner's parcels — and without these each is a sequential scan
        // of the relationship table on every request.
        "create index relationship_origin on relationship (origin_layer_id)",
        "create index relationship_related on relationship (related_layer_id)");

    /// <summary>
    /// Services that are not layers, so they can be shared like everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner correction, 2026-08-15: "geometry server is also a service."</b>
    /// Until now sharing was a property of a <em>layer</em>, and GeometryServer —
    /// which has no layer — was therefore governed by nothing at all and reachable
    /// anonymously. That was not a decision; it was a gap nobody had named,
    /// because the authorization model was built around content and the geometry
    /// service is not content.
    /// </para>
    /// <para>
    /// <b>One table, and the same three scopes.</b> A system service is private,
    /// organisation-wide or public exactly as a layer is
    /// ([ADR-018](../../../docs/adr/ADR-018-authorization-and-roles.md) §3b), so
    /// an administrator has one concept to learn rather than two.
    /// </para>
    /// <para>
    /// <b>Seeded organisation-wide rather than public.</b> ADR-018's default is
    /// closed, and *private* is meaningless for a service with no owner — there
    /// would be nobody it was private to. Organisation-wide is the closed default
    /// that still leaves the service usable, and an administrator can open it.
    /// </para>
    /// <para>
    /// <b>Expand.</b> A new table and one seeded row; nothing existing is touched.
    /// </para>
    /// </remarks>
    private static Migration SystemServicesV10 => Migration.Expand(
        new SchemaVersion(10),
        "Services that are not layers, so GeometryServer can be shared like everything else.",

        """
        create table system_service (
            name        text        not null primary key,
            kind        text        not null,
            folder      text,
            sharing     text        not null default 'private',
            updated_at  timestamptz not null default now(),
            constraint system_service_sharing_known
              check (sharing in ('private', 'organization', 'public'))
        )
        """,

        """
        insert into system_service (name, kind, folder, sharing)
        values ('Geometry', 'GeometryServer', 'Utilities', 'organization')
        """);

    /// <summary>
    /// A service becomes a container of layers, which is what a service is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner correction, 2026-08-15: "a service is a combination of layers
    /// actually. so multiple layers can be shown as a service."</b> Until now
    /// one published layer <em>was</em> one service, and the assumption was
    /// wired in hard enough to be visible in the URLs: every route in the server
    /// ended in <c>/0</c>, because there could never be a layer 1.
    /// </para>
    /// <para>
    /// <b>The old model was not a simplification, it was a different product.</b>
    /// ArcGIS's unit of publication, sharing, naming and stopping is the
    /// <em>service</em>; layers are what a service contains. Somebody publishing
    /// three related layers — points, lines, fences — publishes one service with
    /// three layers, and every client adds it as one thing. One-layer-per-service
    /// makes them add three unrelated services and gives an administrator three
    /// sharing switches to keep in step.
    /// </para>
    /// <para>
    /// <b>Sharing, status, folder and owner move to the service, and only to the
    /// service.</b> A service with three layers and three sharing scopes has no
    /// answer to "who may see this service", so those columns cannot stay on the
    /// layer and also mean anything. The layer's copies are left in place by this
    /// migration and read by nothing — see the warning below.
    /// </para>
    /// <para>
    /// <b>Expand, and the backfill is the point.</b> Every existing layer gets a
    /// service of its own name, in the folder its data implies, carrying its
    /// sharing, status and owner — so every URL that worked before this migration
    /// works after it, at the same address, with the same authorization. The new
    /// model contains the old one exactly.
    /// </para>
    /// <para>
    /// <b>Two sources of truth exist between this migration and its contract, and
    /// that is a real hazard rather than a formality.</b> The <c>is_hosted</c>
    /// column is the cautionary tale in this very file: it stayed writable, drifted
    /// to false everywhere, and silently disabled every vector tile service. The
    /// defence here is that <c>layer.sharing</c> and <c>layer.status</c> are read
    /// by nothing after this migration — the catalogue query selects the service's
    /// columns — so a stale value cannot be believed. Dropping them is a contract
    /// migration and is tracked as <b>D-29</b>.
    /// </para>
    /// </remarks>
    private static Migration ServicesV11 => Migration.Expand(
        new SchemaVersion(11),
        "A service contains layers, so three related layers can be published as one service.",

        """
        create table service (
            id           uuid        not null primary key,
            name         text        not null,
            folder       text,
            kind         text        not null default 'FeatureServer',
            description  text,
            owner_principal_id uuid,
            sharing      text        not null default 'private',
            status       text        not null default 'started',
            created_at   timestamptz not null default now(),
            updated_at   timestamptz not null default now(),
            constraint service_sharing_known
              check (sharing in ('private', 'organization', 'public')),
            constraint service_status_known
              check (status in ('started', 'stopped'))
        )
        """,

        // Unique per folder rather than globally: /rest/services/roads and
        // /rest/services/hosted/roads are two addresses and may be two services.
        // A null folder is the root, and null is not distinct from null here, so
        // the root cannot hold two services of one name either.
        """
        create unique index service_name_in_folder
          on service (coalesce(folder, ''), lower(name))
        """,

        "alter table layer add column service_id uuid references service (id)",

        // The number in the URL. Unique within a service, and it is what
        // /FeatureServer/{id} resolves against.
        "alter table layer add column layer_index integer",

        // One service per existing layer, keeping its name, folder, sharing,
        // status and owner — so nothing moves and no URL changes.
        """
        insert into service (id, name, folder, kind, owner_principal_id, sharing, status)
        select
            gen_random_uuid(),
            l.name,
            case when d.is_datastore then 'hosted' else null end,
            'FeatureServer',
            l.owner_principal_id,
            l.sharing,
            l.status
        from layer l
        join data_source d on d.id = l.data_source_id
        """,

        """
        update layer l
        set service_id = s.id, layer_index = 0
        from service s
        where s.name = l.name
          and s.folder is not distinct from
              (select case when d.is_datastore then 'hosted' else null end
               from data_source d where d.id = l.data_source_id)
        """,

        "alter table layer alter column service_id set not null",
        "alter table layer alter column layer_index set not null",

        """
        alter table layer add constraint layer_index_unique_in_service
          unique (service_id, layer_index)
        """,

        "create index layer_service_idx on layer (service_id)",
        "create index service_sharing_idx on service (sharing)",

        // The layer name is no longer the service address, so it no longer needs
        // to be unique across the whole server — two services may each have a
        // layer called "Parcels". It stays unique within its service.
        "alter table layer drop constraint layer_name_key",

        """
        alter table layer add constraint layer_name_unique_in_service
          unique (service_id, name)
        """);

    /// <summary>
    /// Group layers: a service's layer list becomes a tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner request, 2026-08-15: "enable group layers also."</b> The
    /// screenshot behind the previous correction showed exactly this — one
    /// service whose first entry contained three others — and the previous
    /// migration flattened it, on the mistaken grounds that group layers were a
    /// MapServer concept. ArcGIS documents <c>type: "Group Layer"</c> with
    /// <c>subLayerIds</c> for feature services too.
    /// </para>
    /// <para>
    /// <b>A group layer holds no data, so it is not a row in <c>layer</c>.</b>
    /// Putting it there would mean making <c>data_source_id</c>,
    /// <c>schema_name</c>, <c>table_name</c>, <c>geometry_column</c> and
    /// <c>srid</c> nullable — five columns that are currently guaranteed and
    /// that every reader would then have to defend against — to store a row that
    /// is a name and a parent. The tables are separate and the thing they share
    /// is the number.
    /// </para>
    /// <para>
    /// <b>Which makes index allocation the real problem, and
    /// <c>next_layer_index</c> the answer.</b> Two tables cannot share a unique
    /// constraint, so nothing at the database level would stop a group and a
    /// feature layer both taking index 3 — and <c>/FeatureServer/3</c> would then
    /// be ambiguous. Computing <c>max(index) + 1</c> across both tables reads
    /// correct and races: two concurrent publishes see the same maximum. A
    /// counter on the service row makes allocation a single <c>update … returning</c>,
    /// which takes a row lock and therefore serialises. It also gives
    /// <em>never reused</em> for free — the counter does not go backwards when a
    /// layer is removed, and a saved web map that stored index 2 can never be
    /// silently repointed at something new.
    /// </para>
    /// <para>
    /// <b>Cycles are impossible by construction rather than by a check.</b> A
    /// parent must already exist when its child is created — that is what the
    /// foreign key says — and nothing can be re-parented. A cycle needs one of
    /// those two to be false. If re-parenting is ever added, it needs its own
    /// guard, and this paragraph is where to look for why.
    /// </para>
    /// <para>
    /// <b>Expand.</b> One new table and two new columns; the counter is
    /// backfilled from what each service already has, so nothing is renumbered.
    /// </para>
    /// </remarks>
    private static Migration GroupLayersV12 => Migration.Expand(
        new SchemaVersion(12),
        "Group layers, so a service's layer list can be a tree as ArcGIS's is.",

        "alter table service add column next_layer_index integer not null default 0",

        """
        update service s
        set next_layer_index =
            coalesce((select max(l.layer_index) + 1 from layer l where l.service_id = s.id), 0)
        """,

        """
        create table group_layer (
            id                 uuid        not null primary key,
            service_id         uuid        not null references service (id) on delete cascade,
            layer_index        integer     not null,
            name               text        not null,
            parent_layer_index integer,
            created_at         timestamptz not null default now(),
            constraint group_layer_name_not_blank check (length(btrim(name)) > 0),
            constraint group_layer_index_unique unique (service_id, layer_index),
            constraint group_layer_not_its_own_parent check (parent_layer_index <> layer_index)
        )
        """,

        // The parent of anything is a group layer, and the database says so.
        // Without this a client could be handed a subLayerIds list naming a
        // feature layer as a container, which no client knows how to draw.
        """
        alter table group_layer add constraint group_layer_parent_is_a_group
          foreign key (service_id, parent_layer_index)
          references group_layer (service_id, layer_index)
        """,

        "alter table layer add column parent_layer_index integer",

        """
        alter table layer add constraint layer_parent_is_a_group
          foreign key (service_id, parent_layer_index)
          references group_layer (service_id, layer_index)
        """,

        "create index group_layer_service_idx on group_layer (service_id)");

    /// <summary>
    /// How long a layer's tiles stay fresh, set by whoever knows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-25, and [ADR-010](../../../docs/adr/ADR-010-caching.md) §5.3 asked
    /// for this from the start.</b> Tile cache lifetime was one global number —
    /// an hour — applied to a cadastral reference layer and a live incident
    /// layer alike. A-028 records why that cannot work: volatility is domain
    /// knowledge nobody but the administrator has.
    /// </para>
    /// <para>
    /// <b>Nullable, and null is not zero.</b> Null means <em>this layer has
    /// never been told</em>, and the server's configured default applies. A
    /// zero would mean <em>never cache</em>, which is a real and different
    /// answer that an administrator may want for a layer that changes
    /// continuously.
    /// </para>
    /// <para>
    /// <b>On the layer rather than the service.</b> Tiles are cached per layer
    /// and volatility is a property of the data, not of the container — a
    /// service may hold a monthly boundary layer beside a live sensor layer,
    /// and one number for both is the problem being fixed rather than a
    /// smaller version of it.
    /// </para>
    /// <para><b>Expand.</b> One nullable column; nothing existing is touched.</para>
    /// </remarks>
    private static Migration TileLifetimeV13 => Migration.Expand(
        new SchemaVersion(13),
        "Per-layer tile cache lifetime, so volatility is set by whoever knows it.",

        "alter table layer add column cache_seconds integer",

        """
        alter table layer add constraint layer_cache_seconds_not_negative
          check (cache_seconds is null or cache_seconds >= 0)
        """);

    /// <summary>
    /// A cartographic style a person wrote, instead of one this server guessed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The generated style is a placeholder and always was.</b> Every polygon
    /// layer came out the same blue, drawn in publication order, with no labels
    /// because there were no glyphs to draw them with (that half is
    /// [ADR-027](../../../docs/adr/ADR-027-glyphs-and-sprites.md)). Choosing a
    /// colour is not a decision a server can make: it depends on what the layer
    /// means, what it sits on top of, and who is looking at it.
    /// </para>
    /// <para>
    /// <b>Text, not jsonb, and that is deliberate.</b> A style is a document
    /// somebody authored and will read again. <c>jsonb</c> normalises whitespace,
    /// reorders keys and collapses duplicates, so what came back would not be
    /// what was sent — and a cartographer diffing their style against the one
    /// the server returned would find spurious changes every time. Validity is
    /// checked before the write instead, which is where the caller can be told
    /// what is wrong with it.
    /// </para>
    /// <para>
    /// <b>On the service, because that is what a style describes.</b> A style
    /// names source layers and orders them; half a style per layer is not a
    /// thing that renders.
    /// </para>
    /// <para><b>Expand.</b> Two nullable columns; nothing existing is touched,
    /// and a service with no style keeps getting the generated one.</para>
    /// </remarks>
    private static Migration ServiceStyleV14 => Migration.Expand(
        new SchemaVersion(14),
        "A stored cartographic style per service, replacing the generated default.",

        "alter table service add column style text",

        "alter table service add column style_updated_at timestamptz",

        // A style large enough to matter is a style somebody generated by
        // accident. Real ones are tens of kilobytes; the cap is high enough
        // never to be met on purpose and low enough that the column cannot
        // become a place to store something else.
        """
        alter table service add constraint service_style_is_bounded
          check (style is null or length(style) <= 1048576)
        """);

    /// <summary>
    /// The service name index becomes case-insensitive on the folder too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The index and the lookup disagreed, and the lookup is the one callers
    /// experience.</b> Migration 11 created
    /// <c>unique (coalesce(folder, ''), lower(name))</c> — case-insensitive on
    /// the name and case-<em>sensitive</em> on the folder. Every read asks
    /// <c>coalesce(lower(folder), '') = coalesce(lower(@folder), '')</c>, which
    /// is case-insensitive on both. So the constraint permitted
    /// <c>Hosted/parcels</c> and <c>hosted/parcels</c> as two services, and the
    /// lookup then matched both and returned whichever row came first.
    /// </para>
    /// <para>
    /// <b>That is a correctness defect and not only a tidiness one.</b> The two
    /// services can carry different sharing scopes, so the same URL could
    /// resolve to the public one or the private one depending on row order —
    /// which means an anonymous caller sees it or gets a 404 for reasons nobody
    /// can predict or explain. Folders are taken from the administrator's
    /// request rather than generated, so creating the pair needs no trick.
    /// Reproduced against a real Postgres before this was written: both inserts
    /// succeeded and the lookup matched two rows.
    /// </para>
    /// <para>
    /// <b>The lookup wins the disagreement, deliberately.</b> An ArcGIS REST
    /// address is matched case-insensitively in practice, and the server already
    /// behaves that way at every URL; it is the constraint underneath that was
    /// the odd one out. Making the index agree with the behaviour is the smaller
    /// change and it is the one that removes the ambiguity rather than moving
    /// it.
    /// </para>
    /// <para>
    /// <b>Expand, and it refuses rather than half-applies.</b> Building the new
    /// unique index fails outright if a deployment already holds a colliding
    /// pair — which is the correct outcome: two services that were legal
    /// yesterday and ambiguous today need an administrator to say which one
    /// survives, and a migration must not choose. The check before it exists to
    /// make that failure say so in words rather than as a duplicate-key error on
    /// an index name nobody recognises.
    /// </para>
    /// <para>
    /// <b>The old index is dropped in the same step, and that is expand rather
    /// than contract</b> because nothing reads an index by name: a reader
    /// running the previous build issues the same SQL and the planner picks
    /// whatever exists. Keeping both would leave the case-sensitive uniqueness
    /// in force, which is the thing being removed.
    /// </para>
    /// </remarks>
    private static Migration FolderCaseV15 => Migration.Expand(
        new SchemaVersion(15),
        "Folder names are matched case-insensitively, as every read already assumed.",

        // Named, so the failure explains itself. Without this the migration
        // fails on create-index with "could not create unique index ...
        // Key (coalesce(folder, ''), lower(name))=(...) is duplicated", which
        // says nothing about what an administrator should do next.
        """
        do $$
        declare
            clash text;
        begin
            select string_agg(distinct coalesce(folder, '') || '/' || name, ', ')
              into clash
            from service
            group by coalesce(lower(folder), ''), lower(name)
            having count(*) > 1;

            if clash is not null then
                raise exception
                    'Two or more services differ only in the case of their folder or name: %. '
                    'Until now the catalogue permitted that and every lookup matched both, '
                    'returning whichever row came first. Rename or remove one of each pair, '
                    'then run this migration again. It cannot choose for you: the pair may '
                    'carry different sharing scopes.', clash;
            end if;
        end $$
        """,

        """
        create unique index service_name_in_folder_ci
          on service (coalesce(lower(folder), ''), lower(name))
        """,

        "drop index service_name_in_folder");

    /// <summary>
    /// A service's configured capability ceiling, and a timeout it may lower.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every column is nullable, and null is the whole compatibility
    /// story.</b> Null means *unset* — the service offers whatever its data
    /// supports and its caller's privileges allow, which is exactly what every
    /// service did before this migration existed. So this is additive in the
    /// strongest sense: no row changes, no default changes behaviour, and a
    /// reader that does not know about these columns produces the same documents
    /// it produced yesterday. ADR-031 condition 4 asks for that to be proven
    /// rather than asserted, and it is provable precisely because null is the
    /// default.
    /// </para>
    /// <para>
    /// <b>Two nullable booleans rather than a set of face names.</b> A
    /// <c>text[]</c> of faces reads well and cannot be constrained without a
    /// trigger; two columns can be checked, and there are two faces because
    /// v1-scope says there are two. A third face becomes a third column and a
    /// migration, which is the right cost for adding a protocol.
    /// </para>
    /// <para>
    /// <b>The ceiling is a set and is constrained to known names.</b> An
    /// unrecognised capability in this column would be silently dropped by the
    /// intersection — the failure mode where a service looks configured and is
    /// not — so the constraint refuses it at write time instead. The names are
    /// ArcGIS's, because that is the vocabulary the document speaks; mapping them
    /// to our privileges happens in one place in the host.
    /// </para>
    /// <para>
    /// <b>The timeout is milliseconds and has a floor.</b> ADR-007 §4.8 makes a
    /// per-connection <c>statement_timeout</c> mandatory and D-42 is the record of
    /// that control being removable by accident, in the permissive direction. A
    /// service may ask for less time than its source allows; the check stops a
    /// zero, which PostgreSQL reads as *no limit* and which would therefore turn
    /// this knob into the hole D-42 closed.
    /// </para>
    /// </remarks>
    private static Migration ServiceCapabilitiesV16 => Migration.Expand(
        new SchemaVersion(16),
        "A service carries a capability ceiling and a statement timeout it may lower (ADR-031).",

        """
        alter table service
          add column serves_features      boolean,
          add column serves_tiles         boolean,
          add column capability_ceiling    text[],
          add column statement_timeout_ms  integer
        """,

        """
        alter table service
          add constraint service_capability_ceiling_known check (
            capability_ceiling is null
            or capability_ceiling <@ array['Query','Create','Update','Delete','Extract']::text[]
          )
        """,

        // Positive and bounded. The upper bound is a day: a statement a service
        // permits to run longer than that is not a query anybody is waiting for,
        // and the number exists so a typo of an extra zero is refused rather than
        // stored.
        """
        alter table service
          add constraint service_statement_timeout_positive check (
            statement_timeout_ms is null
            or (statement_timeout_ms > 0 and statement_timeout_ms <= 86400000)
          )
        """);

    /// <summary>
    /// What a request may cost a service: rows, bytes in, bytes out, edits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Q-113's other five knobs, and the reason they are one migration rather
    /// than five.</b> Every one of them bounds the cost of a single request, they
    /// are set on the same screen, and an operator who wants one usually wants the
    /// neighbouring one — splitting them would be five migrations to reach one
    /// coherent state.
    /// </para>
    /// <para>
    /// <b>Null is unset, as in migration 16, and that is what keeps it additive.</b>
    /// A null max record count means the server's own figure applies, which is what
    /// every service does today.
    /// </para>
    /// <para>
    /// <b>"Max features per layer" is deliberately not a sixth column.</b> The peer's
    /// screen offers it beside a max record count, and on a service whose layers are
    /// queried one at a time it is the same ceiling stated twice — two knobs over one
    /// fact, which ADR-031 §2b refused for sharing and refuses here for the same
    /// reason. If a per-layer ceiling is ever wanted it belongs on the layer row, not
    /// as a second service-level number that can disagree with this one.
    /// </para>
    /// <para>
    /// <b>Bytes are <c>bigint</c> and rows are <c>integer</c></b>, because a payload
    /// ceiling above two gigabytes is a legitimate thing to write and a row count
    /// above two billion is not.
    /// </para>
    /// </remarks>
    private static Migration ServiceCostCeilingsV17 => Migration.Expand(
        new SchemaVersion(17),
        "A service bounds what one request may cost it: rows, bytes in, bytes out, edits (Q-113).",

        """
        alter table service
          add column max_record_count          integer,
          add column default_record_count      integer,
          add column max_response_bytes        bigint,
          add column max_request_bytes         bigint,
          add column max_edits_per_transaction integer
        """,

        // Positive, and the default may not exceed the maximum — a service whose
        // default page is larger than its own ceiling is a configuration that
        // contradicts itself, and the database is where that cannot be stored
        // rather than where it is later detected.
        """
        alter table service
          add constraint service_record_counts_sane check (
            (max_record_count is null or max_record_count > 0)
            and (default_record_count is null or default_record_count > 0)
            and (
              max_record_count is null
              or default_record_count is null
              or default_record_count <= max_record_count
            )
          )
        """,

        """
        alter table service
          add constraint service_cost_ceilings_positive check (
            (max_response_bytes is null or max_response_bytes > 0)
            and (max_request_bytes is null or max_request_bytes > 0)
            and (max_edits_per_transaction is null or max_edits_per_transaction > 0)
          )
        """);
}
