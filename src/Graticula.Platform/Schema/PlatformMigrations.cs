using System;
using System.Collections.Generic;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Schema;

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
    public static SchemaVersion ComponentSchemaVersion => new(39);

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
        FolderRegisterV18,
        SystemServiceStatusV19,
        SystemServiceBoundsV20,
        SystemServicePoolingV21,
        CredentialMustChangeV22,
        LayerSymbologyV23,
        ServiceRequestDeadlineV24,
        RolePrivilegesV25,
        GroupsV26,
        GroupSettingsV27,
        JobsV28,
        JobInspectKindV29,
        CoveragesV30,
        LogsV31,
        JobClaimIdentityV32,
        OwnerKeysV33,
        DeadLayerColumnDefaultsV34,
        LayerTimeFieldV35,
        GroupVisibilityWithoutPublicV36,
        GroupMemberListAndLeavingV37,
        SymbologyIsNoLongerBoundedV38,
        AServiceNamesItsReferenceV39,
    ]);


    /// <summary>
    /// A coverage: imagery registered where it lives, rather than a table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own table, because a raster is not a feature layer and bending
    /// <c>layer</c> to hold one would reach every face at once.</b> `layer` requires a
    /// schema, a table, a geometry column and an identity column — every layer this
    /// server holds is a PostGIS table, and a GeoTIFF on a disk is not one. Making
    /// those columns nullable would make them nullable for the seven faces that read
    /// them and rely on them being there.
    /// </para>
    /// <para>
    /// <b>No new service kind is needed.</b> `service.kind` is free text defaulting to
    /// `FeatureServer`, so an ImageServer is a service whose kind says so, and the
    /// sharing, status, owner and folder rules that already govern a service govern
    /// this one unchanged. That is the property worth keeping: a coverage is private or
    /// public by the same mechanism as everything else, and the authorisation path has
    /// no second case to get wrong.
    /// </para>
    /// <para>
    /// <b>One coverage per service, deliberately.</b>
    /// [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.2
    /// scopes the first cut to one raster, one rendering rule, one request — a mosaic
    /// is a second decision with a dataset model behind it. The unique constraint says
    /// so in the schema rather than in a comment, and relaxing it later is an expand.
    /// </para>
    /// <para>
    /// <b>What is stored is what registration read, and it is stored because the file
    /// is not opened again until somebody asks for pixels.</b> ADR-043 §3.3 registers
    /// in place; the service document has to be answerable without touching a disk that
    /// may be an object store.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Two logs a person can ask questions of: what was requested, and what the studio saw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>`audit_event` already existed and is untouched by this.</b> It has recorded every
    /// administrative action since the store's first migrations — 18,215 rows on the
    /// development store when this was written — and nothing has ever read it. What was
    /// missing was never the audit trail; it was a way to ask it anything, and the two
    /// things this adds beside it.
    /// [ADR-045](../../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md).
    /// </para>
    /// <para>
    /// <b>`request_log.query` holds a redacted query string, and the column comment says so
    /// because a schema outlives the code that fills it.</b> Esri clients send a session
    /// token as `?token=` — [D-120](../../../docs/architecture-debt.md) — so an unredacted
    /// query string is a credential, and this table has an index on it. Everything written
    /// here goes through `QueryRedaction.Redact` first.
    /// </para>
    /// <para>
    /// <b>No foreign key to `principal`, unlike `audit_event`.</b> A request log row is
    /// written for anonymous callers too, and one that referenced a principal would have to
    /// resolve it on the hot path. The name is copied instead: a log records what was true
    /// when it was written, and a principal renamed afterwards does not retroactively change
    /// who made a request.
    /// </para>
    /// <para>
    /// <b>Indexed on time descending only, and that is a deliberate floor rather than a
    /// finished answer.</b> Newest-first is how every one of these is read; a status or path
    /// index is a decision to make when a real query is slow, on evidence, rather than three
    /// speculative indexes making every insert more expensive on the hot path this ADR's
    /// first condition is about.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A claim says who took the job and what request shape it speaks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-96](../../../docs/architecture-debt.md), and it was found the expensive way.</b> A
    /// Python worker built and reversed earlier the same day was still running in a Docker
    /// container three hours after its project was deleted, still polling this table, and it
    /// claimed a real upload and failed it with `KeyError: 'archive'`. Two uploads succeeded and
    /// the third did not, because the race went the other way. The failure named a program nobody
    /// was running, and it took about forty minutes to trace to a container.
    /// </para>
    /// <para>
    /// <b>`claimed_by` is the diagnosis half.</b> `for update skip locked` gives the row to
    /// whoever asks first, which is ADR-011 §3.2 working exactly as designed; what the table
    /// could not say is who that was. A failure that names the worker is a failure an operator
    /// can act on.
    /// </para>
    /// <para>
    /// <b>`protocol` is the prevention half, and only for workers that read it.</b> It is the
    /// version of the request shape in `detail`: a worker claims a job only when it speaks that
    /// version or later. So changing what `detail` holds and bumping this stops an un-updated
    /// worker claiming, rather than letting it claim and fail. **It does nothing about a foreign
    /// worker that ignores the column** — that one is diagnosed by `claimed_by` and not
    /// prevented, and pretending otherwise would be the kind of guarantee this repository refuses
    /// to store.
    /// </para>
    /// <para>
    /// <b>Default 1 on the existing rows</b>, because every job already in the table was written
    /// by the only shape there has ever been.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The dead layer columns stop needing to be written (D-24, D-33).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Expand, and <c>minimum_reader_version</c> stays where it is.</b> Nothing is dropped
    /// and nothing changes meaning: <c>layer.is_hosted</c> gains a default, so a build that
    /// stops mentioning it inserts <c>false</c> exactly as the build that mentions it does. A
    /// version-33 reader is unaffected — the column is still there and still holds what it
    /// always held.
    /// </para>
    /// <para>
    /// <b>Why a default is worth a migration for a column nobody reads.</b>
    /// [D-24](../../../docs/architecture-debt.md)'s sentence is that *a dead column is a live
    /// hazard while a writer can still reach it*, and its expensive incident was a writer:
    /// <c>PUT /admin/layers/{name}/sharing</c> wrote <c>layer.sharing</c>, answered 200, and
    /// left the layer readable by anybody. Two of the three writers were stopped on
    /// 2026-08-24 because <c>sharing</c> already defaulted and <c>owner_principal_id</c> was
    /// nullable. <c>is_hosted</c> is <c>not null</c> with no default, so it was the one that
    /// could not stop — and the shape of the repair was a migration rather than an edit.
    /// </para>
    /// <para>
    /// <b>This is not the contract migration and does not pretend to be.</b> The three columns
    /// are still there, which is what the rollback window requires; what changes is that
    /// nothing in the serving or publishing path writes any of them. Dropping them is
    /// [D-33](../../../docs/architecture-debt.md) and waits for the release after the one that
    /// ships migration 11.
    /// </para>
    /// </remarks>
    private static Migration DeadLayerColumnDefaultsV34 => Migration.Expand(
        new SchemaVersion(34),
        "The layer's dead columns carry defaults, so nothing has to write them (D-24).",

        "alter table layer alter column is_hosted set default false");

    /// <summary>
    /// The two owner columns anything reads gain the key the dead one already had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-66](../../../docs/architecture-debt.md): the one owner column nothing reads has a
    /// foreign key; the two that are read have none.</b> <c>layer.owner_principal_id</c> —
    /// vestigial since migration 11 moved ownership onto the service — carries
    /// <c>references principal on delete set null</c>. <c>service.owner_principal_id</c> and
    /// <c>folder.owner_principal_id</c>, which the catalogue reports and the console displays,
    /// carried none. Measured against the live schema on 2026-08-18: both counts zero.
    /// </para>
    /// <para>
    /// <b>What that permitted.</b> A plain <c>delete from principal</c> cascaded the credential,
    /// the sessions, the roles and the api keys, set the dead column to null, and left every
    /// service and folder that member owned pointing at a principal that does not exist — with
    /// nothing raised. The catalogue reports an owner by joining to <c>principal</c>, so the
    /// orphan reports no owner, which is indistinguishable from a service published before
    /// ownership was recorded. The fact is simply gone.
    /// </para>
    /// <para>
    /// <b><c>on delete set null</c>, matching the key that was already here.</b> Not
    /// <c>restrict</c>: a member delete that a foreign key refuses is a delete an administrator
    /// cannot complete, and [ADR-015](../../../docs/adr/ADR-015-authentication.md) §6c already
    /// requires a disposition — this is the floor under that rule, not a second copy of it. Not
    /// <c>cascade</c>, which would delete somebody's services because their account was removed.
    /// </para>
    /// <para>
    /// <b>An expand, and it is worth saying why a constraint counts as one.</b> Nothing is
    /// dropped and no column changes shape, so a previous release runs unchanged against this
    /// schema — it writes owner ids that are principal ids, because that is all it has ever
    /// written. What the constraint forbids is a value no version of this server produces. §6c's
    /// dispositions are what keep the rows valid; this is what catches the writer who has not
    /// read §6c.
    /// </para>
    /// <para>
    /// <b>Orphans are cleared first, in the same migration.</b> Adding the constraint to a table
    /// that already holds a dangling id fails, and a migration that fails on somebody's data
    /// leaves them with a server that will not start. There were none here — measured — but a
    /// deployment that deleted a principal by hand before this shipped is exactly who this is
    /// for, and setting the column null is what the constraint would have done at the time.
    /// </para>
    /// </remarks>
    private static Migration OwnerKeysV33 => Migration.Expand(
        new SchemaVersion(33),
        "A service's and a folder's owner is a principal that exists (D-66).",

        """
        update service set owner_principal_id = null
         where owner_principal_id is not null
           and not exists (select 1 from principal p where p.id = service.owner_principal_id)
        """,

        """
        update folder set owner_principal_id = null
         where owner_principal_id is not null
           and not exists (select 1 from principal p where p.id = folder.owner_principal_id)
        """,

        """
        alter table service
          add constraint service_owner_is_a_principal
          foreign key (owner_principal_id) references principal (id) on delete set null
        """,

        """
        alter table folder
          add constraint folder_owner_is_a_principal
          foreign key (owner_principal_id) references principal (id) on delete set null
        """);

    private static Migration JobClaimIdentityV32 => Migration.Expand(
        new SchemaVersion(32),
        "A job records who claimed it and which request shape it was written in (D-96).",

        """
        alter table job
          add column claimed_by text    null,
          add column protocol   integer not null default 1
        """,

        "comment on column job.claimed_by is "
        + "'Which worker took this row. D-96: a failure has to name a program somebody is running.'",

        "comment on column job.protocol is "
        + "'The version of the request shape in detail. A worker claims only what it speaks.'",

        """
        alter table job
          add constraint job_protocol_positive check (protocol >= 1)
        """);

    private static Migration LogsV31 => Migration.Expand(
        new SchemaVersion(31),
        "A request log and a studio event log, so what the server and the studio did can be "
        + "asked rather than grepped (ADR-045).",

        """
        create table request_log (
            id              bigserial   not null primary key,
            occurred_at     timestamptz not null default now(),
            method          text        not null,
            path            text        not null,
            query           text        null,
            status          integer     not null,
            duration_ms     integer     not null,
            principal_name  text        null,
            source_address  inet        null,
            face            text        null,
            service         text        null,
            bytes           bigint      null,
            constraint request_log_status_plausible check (status between 100 and 599),
            constraint request_log_duration_not_negative check (duration_ms >= 0)
        )
        """,

        "comment on column request_log.query is "
        + "'Redacted by QueryRedaction before insert: an Esri token arrives here (D-120).'",

        "create index request_log_time_idx on request_log (occurred_at desc)",

        """
        create table client_event (
            id              bigserial   not null primary key,
            occurred_at     timestamptz not null default now(),
            kind            text        not null,
            page            text        null,
            message         text        not null,
            detail          jsonb       not null default '{}'::jsonb,
            principal_name  text        null,
            source_address  inet        null,
            agent           text        null,
            constraint client_event_kind_not_blank check (length(btrim(kind)) > 0),
            constraint client_event_message_not_blank check (length(btrim(message)) > 0),
            constraint client_event_message_bounded check (length(message) <= 2000),
            constraint client_event_page_bounded check (page is null or length(page) <= 2000),
            constraint client_event_agent_bounded check (agent is null or length(agent) <= 512)
        )
        """,

        "comment on table client_event is "
        + "'Written from an unauthenticated endpoint. Every text column here is untrusted "
        + "input and the length bounds are the last line of defence, not the first.'",

        "create index client_event_time_idx on client_event (occurred_at desc)");

    private static Migration CoveragesV30 => Migration.Expand(
        new SchemaVersion(30),
        "Imagery is registered where it lives and described here, so a service document "
        + "needs no disk (ADR-043).",

        """
        create table coverage (
            id             uuid        not null primary key,
            service_id     uuid        not null references service (id) on delete cascade,
            name           text        not null,
            path           text        not null,
            srid           integer     not null,
            width          integer     not null,
            height         integer     not null,
            band_count     integer     not null,
            sample_kind    integer     not null,
            no_data        double precision,
            min_x          double precision not null,
            min_y          double precision not null,
            max_x          double precision not null,
            max_y          double precision not null,
            tile_width     integer     not null default 0,
            tile_height    integer     not null default 0,
            overview_count integer     not null default 0,
            style          text,
            registered_at  timestamptz not null default now(),
            constraint coverage_size_positive
              check (width > 0 and height > 0 and band_count > 0),
            constraint coverage_extent_ordered
              check (max_x > min_x and max_y > min_y)
        )
        """,

        // One per service — see the remark. Relaxing this is an expand, and tightening
        // it later would not be.
        "create unique index coverage_one_per_service on coverage (service_id)",

        // <b>The path is unique, and this is a registration rule rather than a physical
        // one.</b> Publishing one file twice gives two services that answer identically
        // and diverge the moment one is restyled, which is the shape D-61 recorded for
        // a setting living on the wrong object. A deployment that genuinely wants two
        // views of one raster wants two rendering rules on one coverage, and that is a
        // different feature.
        "create unique index coverage_path_once on coverage (lower(path))");

    /// <summary>
    /// A system service can be stopped, like every other service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner, 2026-08-17, looking at the geometry service's row:</b> *"geometry server'in,
    /// startı stop'u, timeout'u vs si yok mu?"* — hasn't the geometry server got a start, a stop,
    /// a timeout and so on? It had none of the first two: <c>system_service</c> carried name,
    /// kind, folder and sharing, and nothing else. **And the console was showing a
    /// <c>started</c> pill anyway** — hard-coded in the row, a value the server never sent. That
    /// is the same class as <see href="../../../docs/architecture-debt.md">D-26</see>'s
    /// complaint: a control that displays a figure it did not read.
    /// </para>
    /// <para>
    /// <b>Expand-only: a column with a default, no contract change.</b> A version-18 reader does
    /// not select it and is unaffected, so <c>minimum_reader_version</c> does not move —
    /// <see href="../../../docs/adr/ADR-016-packaging-deployment-upgrade.md">ADR-016</see>'s
    /// rule.
    /// </para>
    /// <para>
    /// <b>Started is the default and that is a decision.</b> The geometry service has answered
    /// since it shipped; a migration that stopped it would take a working endpoint away from
    /// every existing deployment as a side effect of adding a column.
    /// </para>
    /// </remarks>
    private static Migration SystemServiceStatusV19 => Migration.Expand(
        new SchemaVersion(19),
        "Give a system service a started/stopped status, so it can be stopped like any other.",

        "alter table system_service add column status text not null default 'started'",

        """
        alter table system_service add constraint system_service_status_known
          check (status in ('started', 'stopped'))
        """)

        // <b>ADR-018 condition 4.</b> *Silently privatising somebody's published data
        // is a worse regression than the closed default was.* The description above is
        // true and is about the schema; this is the sentence an operator needs before
        // they run it, and until 2026-08-27 the plan had no way to carry one.
        .Cautioning(
            "Every layer that already exists becomes PRIVATE, and none of them will be "
            + "visible to anybody but an administrator until it is shared again. This "
            + "server had no sharing scope before this version, so there is no previous "
            + "setting to preserve and private is the only safe default. Nothing is "
            + "deleted: re-share with PUT /admin/layers/{name}/sharing, and "
            + "GET /admin/layers lists what you have.");

    /// <summary>
    /// A system service's own bounds, so an administrator can set them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner, on being told the overlay timeout was a configuration-file setting:</b> *"iyi
    /// de neden yok. yani ben neden max timeout süresi tanımlayamıyorum?"* — fine, but why not;
    /// why can I not define a maximum timeout? The reason given was that changing it live would
    /// mean rebuilding the worker pool under in-flight requests. **That was wrong.** The pool
    /// applies the deadline per operation, so only the number ever had to move, and the deferral
    /// was caution about work nobody needed to do.
    /// </para>
    /// <para>
    /// <b>Null means nobody has said, and the configured default answers.</b> The same three-way
    /// rule as a layer's cache TTL (migration 13) and its stored style: absent, set, and set to
    /// something meaning *none* are three different states, and collapsing the first two would
    /// make a fresh install indistinguishable from one where an administrator chose the default
    /// on purpose.
    /// </para>
    /// <para>
    /// <b>On the service rather than in a settings table.</b> This is the geometry service's
    /// timeout — it is the only consumer of the pool — so it belongs on the row somebody edits
    /// when they mean *that service*. A server-wide settings table would put one service's bound
    /// somewhere nothing else in it lives, and the next engine consumer would want its own value
    /// anyway.
    /// </para>
    /// </remarks>
    private static Migration SystemServiceBoundsV20 => Migration.Expand(
        new SchemaVersion(20),
        "Let a system service carry its own deadline and pre-flight threshold.",

        "alter table system_service add column deadline_seconds integer",
        "alter table system_service add column preflight_pairs bigint",

        """
        alter table system_service add constraint system_service_deadline_positive
          check (deadline_seconds is null or deadline_seconds between 1 and 3600)
        """,

        """
        alter table system_service add constraint system_service_preflight_not_negative
          check (preflight_pairs is null or preflight_pairs >= 0)
        """);

    /// <summary>
    /// The other two numbers on the reference's pooling page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner, with a screenshot of ArcGIS Server Manager's <em>Pooling</em> page for the
    /// geometry service:</b> *"e bunlar güzel örnekler değil mi?"* — aren't these good examples?
    /// They are. It carries five controls, and three of them named real gaps here:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// *The maximum time a client can use a service* — we had it for overlay, and migration 20
    /// made it settable.
    /// </description></item>
    /// <item><description>
    /// *The maximum time a client will wait to get a service* — we bounded the wait by the
    /// **work's** deadline, one number doing two jobs. Their split is right: a deployment can
    /// accept long work and still refuse to hold a connection behind somebody else's.
    /// </description></item>
    /// <item><description>
    /// *The maximum time an idle instance can be kept running* — we had **nothing**. A returned
    /// worker went into a bag and came out again for ever, so one overlay at nine in the morning
    /// held two processes until the server was restarted.
    /// </description></item>
    /// </list>
    /// <para>
    /// The two it did not transfer are the instance minimum and maximum per machine. Ours is one
    /// number, <c>OverlayWorkers</c>, so minimum and maximum are the same by construction — and
    /// elastic pooling needs a concrete problem before it is worth the machinery (§82). *Per
    /// machine* does not transfer at all: this is one process.
    /// </para>
    /// <para>
    /// <b>Null means nobody has said</b>, as with migration 20 — and for the idle budget **zero is
    /// a third answer**, meaning keep workers for ever, which is what this pool did before.
    /// </para>
    /// </remarks>
    private static Migration SystemServicePoolingV21 => Migration.Expand(
        new SchemaVersion(21),
        "Let a system service carry its own queue-wait and idle-worker budgets.",

        "alter table system_service add column wait_seconds integer",
        "alter table system_service add column idle_seconds integer",

        """
        alter table system_service add constraint system_service_wait_positive
          check (wait_seconds is null or wait_seconds between 1 and 3600)
        """,

        """
        alter table system_service add constraint system_service_idle_not_negative
          check (idle_seconds is null or idle_seconds between 0 and 86400)
        """);

    /// <summary>
    /// A password an administrator issued is dirty until its owner replaces it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner correction 2026-08-17, and it is better than what it replaced:</b> *"kullanıcıya
    /// yeni parola veremem. sistem bana yeni bir parola verir. bunu kullanıcı ile paylaşabilirim.
    /// ama sistem otomatik olarak o parolayı kirli kabul eder. kullanıcı giriş yapınca değiştirmek
    /// zorunda kalır."* — I cannot give the user a new password; the system gives me one, I can
    /// share it, and the system treats that password as dirty automatically, so the user has to
    /// change it when they sign in.
    /// </para>
    /// <para>
    /// <b>What was wrong with the version this replaces.</b> An administrator typed the first
    /// password, and the endpoint's own note admitted the consequence — *"this one is known to
    /// whoever typed it here"* — and then did nothing about it. A note describing a hazard is not a
    /// control. Worse, it was the administrator's choice of password: their habits, their reuse,
    /// their idea of *long enough*, on somebody else's account.
    /// </para>
    /// <para>
    /// <b>On the credential rather than on the principal, because that is what is dirty.</b> A
    /// person is not dirty; the secret they were handed is. It also means the flag disappears with
    /// the credential — a member who moves to an identity provider has no local password to be made
    /// to change.
    /// </para>
    /// <para>
    /// <b>Default false, so no existing account is locked out by a migration.</b> Every credential
    /// in a store before this was either chosen by its owner at setup or changed by them, which is
    /// exactly the state this column means by *clean*.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A layer's canonical symbology, which is the document every face derives from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-033 §5a: one column on the layer, holding a MapLibre style.</b> It is the
    /// only authored artefact for appearance; the tile face and the feature face are both
    /// derived from it on read, so the two cannot drift apart by being edited separately.
    /// </para>
    /// <para>
    /// <b>Text, not <c>jsonb</c>, for migration 14's reason and one more.</b> 14 said it
    /// first: <c>jsonb</c> normalises whitespace, reorders keys and collapses duplicates,
    /// so a cartographer diffing their style against the one the server returned would
    /// find spurious changes every time. The extra reason here is that this document is
    /// *normalised on write by us* — <c>sources</c>, <c>sprite</c> and <c>glyphs</c> are
    /// stripped (§5c) and an absolute URL is refused — and a caller can only be told what
    /// changed if what comes back is byte-for-byte what was stored.
    /// </para>
    /// <para>
    /// <b>On the layer, not the service, and that is the difference from migration 14.</b>
    /// A style names source layers and orders them, which is a service-level fact; a
    /// symbol is a fact about one layer's features. §5d keeps both: the per-service style
    /// survives as an override for the tile face, and this is the authoring unit.
    /// </para>
    /// <para>
    /// <b>The bound was a check constraint because ADR-033 §7's fifth condition said so in
    /// those words, and migration 38 drops it.</b> The reasoning here was right about
    /// *where* a bound belongs and wrong about the number: 256 KB was said to be *high
    /// enough never to be met by a real style*, and a colour per Turkish province meets it.
    /// ADR-054 withdrew the bound by owner decision 2026-09-05. This paragraph is left
    /// standing rather than rewritten, because a migration is a record of what was done on
    /// the day and the constraint really was added here.
    /// </para>
    /// <para><b>Expand.</b> Two nullable columns and a check that permits null; a layer
    /// with no symbology keeps the generated appearance, which §5b makes a real answer
    /// rather than a placeholder.</para>
    /// </remarks>
    /// <summary>
    /// How long a client may occupy this service, in seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner requirement, restated 2026-08-18:</b> *"sadece geometri değil, tüm servislerde
    /// timeout olmalı"* — every service needs a timeout, not only the geometry service. Migrations
    /// 20 and 21 gave the geometry service a deadline, a queue wait and an idle bound because that
    /// is where the request arrived; this is the same fact for every other service.
    /// </para>
    /// <para>
    /// <b>It is the whole request, not the database statement.</b> There has been a fixed
    /// 30-second `statement_timeout` on the connection pool since ADR-007 §4.8, and it bounds a
    /// query — not the projecting, encoding and writing that happen after the query returns. The
    /// reference calls this *the maximum time a client can use a service*, and nothing in this
    /// server bounded it before.
    /// </para>
    /// <para>
    /// <b>Null means the server's own default</b>, which is `Graticula:RequestDeadlineSeconds` and
    /// defaults to 600. So this column changes nothing on a service nobody configures — which is
    /// every service that exists today.
    /// </para>
    /// <para>
    /// <b>The check permits only a positive number, and there is no ceiling here.</b> A service may
    /// ask for less than the server allows and its request is bounded by the smaller of the two
    /// (`RequestDeadline.LowerTo`), so a large value stored here is harmless rather than a way
    /// around the deployment's limit — and a ceiling written into the schema would be a second
    /// place for the same rule to disagree from. Zero is refused rather than treated as *no bound*:
    /// a service that wants no bound of its own leaves this null, and a column where 0 and null
    /// mean different things is a column somebody reads wrong.
    /// </para>
    /// <para><b>Expand.</b> One nullable column and a check that permits null.</para>
    /// </remarks>
    /// <summary>
    /// What each role grants, as rows rather than as compiled code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-035, owner decision 2026-08-18:</b> *"sistemde tanımlı tüm rollerin yetkileri
    /// değiştirilebilir, admin hariç."* Until now role-to-privilege lived entirely in
    /// <c>Roles.BuildGrants()</c> — a C# dictionary compiled into the platform assembly — and the
    /// <c>role</c> table carried a name and a description and nothing else. So a role row could be
    /// inserted and would grant nothing, with no way to give it anything.
    /// </para>
    /// <para>
    /// <b>The seed is the point of this migration, more than the table is.</b> Every built-in
    /// role's grants are written from <c>Roles.Grants</c>, so a deployment upgrading to this schema
    /// keeps exactly the behaviour it had. An upgrade that silently widened or narrowed what a role
    /// confers is the worst outcome available here and the one nobody would notice, which is why
    /// ADR-035 condition 1 asks for it to be asserted rather than assumed.
    /// </para>
    /// <para>
    /// <b>The administrator is seeded like the others and is not read like the others.</b> Its rows
    /// exist so the roles screen can show what it holds; the authorization check short-circuits
    /// before consulting them (ADR-035 §4b), because *"admin can do everything"* stated as data is
    /// a claim an <c>UPDATE</c> can falsify. Deleting its rows changes nothing about what an
    /// administrator may do.
    /// </para>
    /// <para>
    /// <b>The four group privileges are in the catalogue and granted to nobody.</b> ADR-035 §4c
    /// defines them; no built-in role receives them here, so this migration cannot widen anything.
    /// A deployment that wants groups edits a role — which is the feature this migration exists to
    /// enable, and it makes the upgrade a genuine non-event rather than a nearly-one.
    /// </para>
    /// <para>
    /// <b>The privilege is text with no foreign key, deliberately.</b> There is no
    /// <c>privilege</c> table: the catalogue is the enum, and a table of it would be a second place
    /// for the same list to disagree from. An unknown name is refused on write by the application
    /// and ignored-with-a-log on read, so a store written by a newer version does not stop an older
    /// one from starting. The lesson is [D-70]: <c>sharing</c> was called *"a value rather than a
    /// migration"* and carries a check constraint on three tables, so a check constraint listing
    /// eighteen privilege names would be the same mistake at four times the size.
    /// </para>
    /// <para><b>Expand.</b> One new table and rows in it. Nothing existing changes shape.</para>
    /// </remarks>
    /// <summary>
    /// Groups: members, what is shared with them, and the fourth sharing scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-036], answering Q-112 on the owner's requirement:</b> *"studio tarafında gruplar
    /// olacak … gruba kullanıcılar ve nesneler atanabilir. şu an için grupla paylaşılabilecek yegane
    /// şey servisler."* Groups were deferred by ADR-018 §3b — *"adding them here would be adopting a
    /// subsystem to complete a table"* — and are undeferred now that every group operation has a
    /// privilege to hang from (ADR-035).
    /// </para>
    /// <para>
    /// <b>`sharing_group` rather than `group`, because `GROUP` is a reserved word.</b> The
    /// alternative is quoting it in every statement for the life of the product, and a care
    /// requirement that has to be remembered is the shape of defect this project keeps finding.
    /// </para>
    /// <para>
    /// <b>The fourth scope needs the check widened, and ADR-018 §3b said it would not.</b> That
    /// paragraph claimed the column *"takes a string so adding `group` later is a value rather than a
    /// migration"*; all three tables carrying it also carry a three-value check. The claim was
    /// corrected before this migration needed it — see [D-70]'s neighbours — and widening a check is
    /// expand-only, so the cost is this migration and not a rewrite.
    /// </para>
    /// <para>
    /// <b>`item_update` is written at creation and never changed</b> (ADR-036 §4c). Flipping a group
    /// from *view* to *update all* would make every item already shared with it editable by every
    /// member, retroactively, in one click. The column is here so that editing through a group is an
    /// addition later rather than a redesign; nothing in v1 reads it, and ADR-036 §4a says so.
    /// </para>
    /// <para>
    /// <b>Expand.</b> Three new tables, and three widened checks. Nothing existing loses a value.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A group's four editable policies, and a summary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-036] §4g, by owner decision 2026-08-18</b> — the reference's group Settings tab, sent
    /// with *"bizim ekranımız yetersiz ve basit kalıyor"*. Four axes an operator may set and could
    /// not: who may see the group exists, how people join it, who may add items to it, and whether
    /// it is protected from deletion.
    /// </para>
    /// <para>
    /// <b>Every default is what this server already did, so this migration changes no behaviour.</b>
    /// A group was discoverable only by its members, joinable only by invitation, contributed to only
    /// by its owner and managers, and deletable with a confirmation — so the columns record what was
    /// true and give somebody the ability to say otherwise. The same shape as migration 25's seed and
    /// for the same reason: an upgrade that quietly widened who may see a group is the worst outcome
    /// available here and the one nobody would notice.
    /// </para>
    /// <para>
    /// <b>`join_policy` admits a value the application refuses.</b> `request` needs a queue of
    /// pending requests — a table, a screen and a decision about who reviews them — and none of that
    /// exists. The check permits it so the column does not have to be widened later; the write path
    /// refuses it, because a policy the server stores and does not honour is
    /// [D-67](../../docs/architecture-debt.md) over again, and that debt was about exactly this:
    /// a setting reported and unenforced for two days.
    /// </para>
    /// <para>
    /// <b>`item_update` is not here and stays immutable.</b> §4c: widening it would make every service
    /// already shared with the group editable by every member, retroactively. The reference's own
    /// Settings page does not offer it either, which is the evidence that they draw the same line —
    /// *who may contribute* and *what a share confers* are different questions.
    /// </para>
    /// <para><b>Expand.</b> Five nullable-or-defaulted columns and three checks. Nothing loses a value.</para>
    /// </remarks>
    /// <summary>
    /// A record of long work: what was asked for, how it went, and what came of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-037] and the first increment of [ADR-011], which until now was a decision with no
    /// implementation.</b> There is no job table, no queue and no status endpoint in this product; a
    /// File Geodatabase import cannot be answered on the request that asks for it, because reading one
    /// takes minutes. So this is the smallest record that lets a caller be told *later*.
    /// </para>
    /// <para>
    /// <b>It is deliberately not a queue.</b> ADR-011 describes fair-shared scheduling, an OGC API
    /// Processes surface and a job engine two subsystems share; none of that is here. What is here is a
    /// row per request with a status somebody can poll, and the honest name for it is *a record*, not
    /// *the job system*. Building the queue to hold one job type would be deciding ADR-011's open
    /// questions by accident — the failure §82 exists to prevent, and the one
    /// [D-46](../../docs/architecture-debt.md) records for UI.
    /// </para>
    /// <para>
    /// <b>`owner_principal_id` is not null, and that is an authorization decision rather than a
    /// column.</b> A job is somebody's; the status endpoint shows a caller their own and an
    /// administrator everybody's, which is the same two-axis shape ADR-036 §3 uses for groups. A
    /// nullable owner would make *whose is this* unanswerable for exactly the rows where it matters —
    /// an import that wrote a table.
    /// </para>
    /// <para>
    /// <b>`detail` is text holding JSON rather than `jsonb`.</b> Nothing queries inside it: it carries
    /// what the request asked for and what the worker answered, read whole by one screen. `jsonb` would
    /// buy indexing nobody needs and cost a rewrite of every value on the way in — the same reasoning
    /// `audit_event.detail` already uses, and consistency with the row beside it is worth more here
    /// than a capability with no caller.
    /// </para>
    /// <para>
    /// <b>`progress` is an integer 0–100 and it is a report, not a promise.</b> A reader needs to know
    /// whether a five-minute import is moving; a worker that cannot say how far along it is leaves it
    /// at zero, which is honest. The check keeps a worker from reporting 140% because it counted
    /// features instead of fractions.
    /// </para>
    /// <para><b>Expand.</b> One new table and two indexes. Nothing existing loses a value.</para>
    /// </remarks>
    /// <summary>
    /// A second job kind: looking inside a geodatabase before choosing what to take from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two kinds because the operator cannot name a layer they have not seen.</b> A geodatabase holds
    /// many feature classes — one of the owner's real archives holds 55 — so an upload cannot say which
    /// one to import. The first job answers *what is in here*; the operator chooses; the second imports
    /// the chosen layer. Squeezing both into one job would mean either importing everything, or asking
    /// for a layer name before the archive has been read.
    /// </para>
    /// <para>
    /// <b>`inspect` is cheap and `import` is not, which is why they are separate rows rather than two
    /// phases of one.</b> Listing layers reads headers; importing reads features. A screen that has to
    /// wait for the second to offer the first would make choosing a layer as slow as importing one.
    /// </para>
    /// <para>
    /// <b>Migration 28's check constraint named one kind, and it was written knowing it would move.</b>
    /// That is the shape [D-74](../../docs/architecture-debt.md) argues for: a closed set with one
    /// value is cheap to widen and stops a typo becoming a job nobody can run. This is the widening.
    /// </para>
    /// <para><b>Expand.</b> One check constraint replaced by a wider one. No row loses a value.</para>
    /// </remarks>
    private static Migration JobInspectKindV29 => Migration.Expand(
        new SchemaVersion(29),
        "A second job kind, so a geodatabase can be looked into before a layer is chosen (ADR-037).",

        """
        alter table job drop constraint if exists job_kind_known
        """,

        """
        alter table job add constraint job_kind_known
          check (kind in ('geodatabase.inspect', 'geodatabase.import'))
        """);

    private static Migration JobsV28 => Migration.Expand(
        new SchemaVersion(28),
        "A record of long work, so an import can be answered later (ADR-037, ADR-011's first step).",

        """
        create table if not exists job (
            id                  uuid primary key,
            kind                text        not null,
            status              text        not null,
            progress            int         not null default 0,
            owner_principal_id  uuid        not null references principal (id),
            subject             text,
            detail              text,
            failure             text,
            created_at          timestamptz not null default now(),
            started_at          timestamptz,
            finished_at         timestamptz
        )
        """,

        """
        alter table job add constraint job_status_known
          check (status in ('queued', 'running', 'done', 'failed', 'cancelled'))
        """,

        """
        alter table job add constraint job_progress_ranged
          check (progress between 0 and 100)
        """,

        """
        alter table job add constraint job_kind_known
          check (kind in ('geodatabase.import'))
        """,

        // The two questions a screen asks: what is mine, newest first, and what is still running.
        """
        create index if not exists job_by_owner
            on job (owner_principal_id, created_at desc)
        """,

        """
        create index if not exists job_unfinished
            on job (status) where status in ('queued', 'running')
        """);

    private static Migration GroupSettingsV27 => Migration.Expand(
        new SchemaVersion(27),
        "A group's visibility, join policy, contributor policy and delete lock (ADR-036 §4g).",

        """
        alter table sharing_group
            add column visibility   text        not null default 'members',
            add column join_policy  text        not null default 'invitation',
            add column contribute   text        not null default 'managers',
            add column delete_locked boolean    not null default false,
            add column summary      text
        """,

        """
        alter table sharing_group add constraint sharing_group_visibility_known
          check (visibility in ('members', 'organization', 'public'))
        """,

        """
        alter table sharing_group add constraint sharing_group_join_policy_known
          check (join_policy in ('invitation', 'request', 'self'))
        """,

        """
        alter table sharing_group add constraint sharing_group_contribute_known
          check (contribute in ('members', 'managers'))
        """);

    private static Migration GroupsV26 => Migration.Expand(
        new SchemaVersion(26),
        "Groups, their members, what is shared with them, and the group sharing scope (ADR-036).",

        """
        create table sharing_group (
            id                 uuid        not null primary key,
            name               text        not null,
            title              text,
            description        text,

            -- <b>The owner, and it is required.</b> A group with no owner is one nobody may delete
            -- or transfer, which is the state ADR-015 6c refuses for a service.
            owner_principal_id uuid        not null references principal (id) on delete restrict,

            -- ADR-036 4b and 4c: a property of the group, fixed at creation. `none` is the default
            -- because a group whose purpose is *these people may read this* should not have to
            -- declare an editing posture it will never use.
            item_update        text        not null default 'none',

            created_at         timestamptz not null default now(),
            updated_at         timestamptz not null default now(),

            constraint sharing_group_name_not_blank check (length(btrim(name)) > 0),
            constraint sharing_group_item_update_known
                check (item_update in ('none', 'ownItems', 'allItems'))
        )
        """,

        // Case-insensitively unique, like every other name in this schema: two groups differing
        // only in case is the defect two services differing only in the case of their folder already
        // caused once.
        "create unique index sharing_group_name_unique on sharing_group (lower(name))",

        """
        create table sharing_group_member (
            group_id     uuid        not null references sharing_group (id) on delete cascade,
            principal_id uuid        not null references principal (id) on delete cascade,

            -- ADR-036 3: the second axis. `manager` holds the group's operations inside it without
            -- owning it — the owner's *"yönetici olarak atanırsan"*.
            membership   text        not null default 'member',

            added_at     timestamptz not null default now(),
            added_by     uuid        references principal (id) on delete set null,

            constraint sharing_group_member_pk primary key (group_id, principal_id),
            constraint sharing_group_membership_known
                check (membership in ('member', 'manager'))
        )
        """,

        """
        create table sharing_group_item (
            group_id   uuid        not null references sharing_group (id) on delete cascade,

            -- <b>A service today, and the table is named for the general case.</b> The owner: other
            -- things follow when the map model settles. A second column beside this one is a smaller
            -- change than a renamed table.
            service_id uuid        not null references service (id) on delete cascade,

            shared_at  timestamptz not null default now(),
            shared_by  uuid        references principal (id) on delete set null,

            constraint sharing_group_item_pk primary key (group_id, service_id)
        )
        """,

        // The read path asks *which groups is this shared with* far more often than the reverse.
        "create index sharing_group_item_by_service on sharing_group_item (service_id)",

        // And *which groups am I in*, on every request by a signed-in principal.
        "create index sharing_group_member_by_principal on sharing_group_member (principal_id)",

        // <b>The fourth scope, by name.</b> All three checks were created with explicit names in
        // migrations 11, 16 and 18 — verified against the live schema rather than assumed — so this
        // is three plain statements instead of a `do` block that reads the catalogue. A migration
        // that has to discover what it is altering is one nobody can read.
        "alter table service drop constraint service_sharing_known",
        """
        alter table service add constraint service_sharing_known
          check (sharing in ('private', 'organization', 'public', 'group'))
        """,

        "alter table layer drop constraint layer_sharing_known",
        """
        alter table layer add constraint layer_sharing_known
          check (sharing in ('private', 'organization', 'public', 'group'))
        """,

        "alter table system_service drop constraint system_service_sharing_known",
        """
        alter table system_service add constraint system_service_sharing_known
          check (sharing in ('private', 'organization', 'public', 'group'))
        """);

    private static Migration RolePrivilegesV25 => Migration.Expand(
        new SchemaVersion(25),
        "What each role grants, editable per deployment (ADR-035).",
        [.. RolePrivilegeStatements()]);

    /// <summary>The table, then one row per grant the code holds today.</summary>
    private static IEnumerable<string> RolePrivilegeStatements()
    {
        yield return
            """
            create table role_privilege (
                role_name text not null references role (name) on delete cascade,
                privilege text not null,
                constraint role_privilege_pk primary key (role_name, privilege),
                constraint role_privilege_not_blank check (length(btrim(privilege)) > 0)
            )
            """;

        // <b>Generated from the code that has been the answer until now.</b> Writing the list by
        // hand here would create a second statement of the same fact, and the two would disagree
        // the first time either moved — which is the failure mode the whole of ADR-035 §2 is about.
        foreach (string role in Roles.All)
        {
            foreach (Privilege privilege in Roles.PrivilegesOf(role))
            {
                yield return
                    "insert into role_privilege (role_name, privilege) values ("
                    + $"'{Literal(role)}', '{Literal(Roles.NameOf(privilege))}')";
            }
        }
    }

    private static Migration ServiceRequestDeadlineV24 => Migration.Expand(
        new SchemaVersion(24),
        "How long a client may occupy a service, per service, in seconds.",

        "alter table service add column request_deadline_seconds integer",

        """
        alter table service add constraint service_request_deadline_is_positive
          check (request_deadline_seconds is null or request_deadline_seconds > 0)
        """);

    private static Migration LayerSymbologyV23 => Migration.Expand(
        new SchemaVersion(23),
        "A canonical MapLibre symbology document per layer, which both faces derive from.",

        "alter table layer add column symbology text",

        "alter table layer add column symbology_updated_at timestamptz",

        """
        alter table layer add constraint layer_symbology_is_bounded
          check (symbology is null or length(symbology) <= 262144)
        """);

    private static Migration CredentialMustChangeV22 => Migration.Expand(
        new SchemaVersion(22),
        "Mark a credential an administrator issued as one its owner must replace.",

        "alter table local_credential add column must_change boolean not null default false");

    /// <summary>
    /// A folder becomes a thing rather than a string on a service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner request 2026-08-17: publish into a folder, or make one.</b>
    /// *"örneğin turkiye folderi. o zaman veri … /rest/services/turkiye/tr_il/featureserver
    /// diye gidecek"* — and then, looking at their reference's folder list: *"hosted da bir
    /// folder"*. Both halves need this table.
    /// </para>
    /// <para>
    /// <b>Because an empty folder has to be able to exist.</b> Until now a folder was a
    /// text column on <c>service</c>, so it existed exactly as long as something was in it:
    /// deleting the last service in <c>turkiye</c> deleted the folder, and creating one
    /// before publishing into it was impossible. That is the whole reason this is a table
    /// and not a validation rule.
    /// </para>
    /// <para>
    /// <b>And because the directory was lying by omission.</b> The root advertised
    /// <c>["hosted", "Utilities"]</c> as two constants in the host, so a service in any
    /// other folder was reachable at its URL and invisible to every client that browses the
    /// catalogue. A folder register is what lets that list be read rather than typed.
    /// </para>
    /// <para>
    /// <b>Case-insensitive on <c>lower(name)</c>, storing what the operator typed.</b>
    /// Migration 15 fixed the service index to match folders case-insensitively, so
    /// <c>Hosted</c> and <c>hosted</c> already had to be one folder; this keeps that true
    /// while letting the directory show <c>Turkiye</c> if that is how it was written.
    /// </para>
    /// <para>
    /// <b>Expand only, and deliberately no foreign key yet.</b> A key from
    /// <c>service.folder</c> to here would need every existing folder value normalised to
    /// one case first, which is a contract migration and a separate risk. So this seeds the
    /// register from what exists and the read path unions the two — nothing can disappear
    /// from the directory because a row is missing. The missing key is recorded as debt
    /// rather than left as an assumption.
    /// </para>
    /// </remarks>
    private static Migration FolderRegisterV18 => Migration.Expand(
        new SchemaVersion(18),
        "Folders become a register, so an empty one can exist and the directory can read them.",

        """
        create table folder (
            name        text        not null,
            created_at  timestamptz not null default now(),
            owner_principal_id uuid,
            constraint folder_name_is_addressable
              check (name <> '' and name !~ '[/\\?#%]' and length(name) <= 128)
        )
        """,

        // One folder per name regardless of case, matching how every service lookup
        // resolves a folder since migration 15.
        """
        create unique index folder_name_unique on folder (lower(name))
        """,

        // Seeded from what is already in use, so the register starts complete: the two
        // names the host had hard-coded, plus any folder a service or system service is
        // already in. `on conflict do nothing` because 'Utilities' arrives from both.
        """
        insert into folder (name)
        select distinct folder from service where folder is not null and folder <> ''
        on conflict do nothing
        """,

        """
        insert into folder (name)
        select distinct folder from system_service where folder is not null and folder <> ''
        on conflict do nothing
        """,

        """
        insert into folder (name) values ('hosted')
        on conflict do nothing
        """);

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
    /// with a group — arrives without a new column. <b>Not without a migration, which this
    /// comment claimed until 2026-08-18:</b> the check constraint written three lines below
    /// lists exactly three values, so the fourth scope needs it widened. That is expand-only
    /// and cheap — the wrong part was the claim that nothing schema-shaped is needed.
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

    /// <summary>
    /// Which column carries a layer's phenomenon time, when the schema cannot say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[Q-129](../../../docs/open-questions.md), recorded the moment the derivation
    /// was chosen and paid before anybody animated the wrong column.</b> The time
    /// dimension was derived: exactly one <c>Date</c> column, or no dimension at all.
    /// A table with <c>created_at</c> and <c>observed_at</c> published nothing, which
    /// is honest and useless; a table with only <c>created_at</c> published a
    /// dimension over the wrong column, which is worse — an animation of when rows
    /// were inserted, indistinguishable from an animation of when things happened.
    /// </para>
    /// <para>
    /// <b>Null keeps the derivation, which is why this is additive.</b> Every layer
    /// that has a single date column goes on working with nothing set, and the
    /// declaration is only needed where the schema is ambiguous. The column is not
    /// validated here against the layer's fields: a registered table's schema can
    /// drift under us (A-023), so a constraint the database could enforce today is
    /// one it could not enforce tomorrow. The check belongs where the fields are
    /// read, and that is the admin endpoint and the dimension itself.
    /// </para>
    /// </remarks>
    private static Migration LayerTimeFieldV35 => Migration.Expand(
        new SchemaVersion(35),
        "Declare which column is a layer's time, when its schema has more than one (Q-129).",

        "alter table layer add column time_field text",

        // A column name, not a document. PostgreSQL's own identifier limit is 63
        // bytes and a value longer than that cannot name anything that exists.
        """
        alter table layer add constraint layer_time_field_is_a_name
          check (time_field is null or (length(time_field) between 1 and 63))
        """);

    /// <summary>
    /// A group is visible to its members or to the organisation. There is no third.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner decision 2026-08-25, closing [Q-119](../../../docs/open-questions.md).</b> The
    /// third value meant *discoverable by anybody, including an anonymous caller*, and
    /// there was nowhere for that to happen: `/admin/groups` refuses an anonymous caller,
    /// so `public` and `organization` were enforced identically while the console said
    /// otherwise. The question was where a public group gets discovered; the answer is
    /// that it does not, because there is no such thing here.
    /// </para>
    /// <para>
    /// <b>Nothing in any store holds it, and the update runs anyway.</b>
    /// `PostgresGroupDirectory` has refused `public` on write since the setting shipped —
    /// readable and unwritable, the same shape `request` still has — so this should
    /// demote nothing. It is written because *should* is a claim about every deployment
    /// that exists, including the ones nobody here has seen, and a constraint that fails
    /// on upgrade is a server that will not start.
    /// </para>
    /// <para>
    /// <b>Demoted to `organization` rather than to `members`.</b> A group somebody made
    /// discoverable is a group they wanted found; narrowing it to its own members would
    /// change what an operator chose, quietly. `organization` is what `public` was
    /// actually being enforced as, so this makes the stored value match the behaviour
    /// rather than changing the behaviour.
    /// </para>
    /// <para>
    /// <b>Expand rather than contract, deliberately.</b> It tightens a check constraint
    /// and drops no column and no data, so a reader from before this migration still
    /// works: it would accept a value the store can no longer contain, which costs
    /// nothing. Contract is for a change that makes an older reader wrong.
    /// </para>
    /// </remarks>
    private static Migration GroupVisibilityWithoutPublicV36 => Migration.Expand(
        new SchemaVersion(36),
        "A group is visible to members or to the organisation; 'public' is gone (Q-119).",

        "update sharing_group set visibility = 'organization' where visibility = 'public'",

        "alter table sharing_group drop constraint if exists sharing_group_visibility_known",

        """
        alter table sharing_group add constraint sharing_group_visibility_known
          check (visibility in ('members', 'organization'))
        """);

    /// <summary>
    /// Who may see a group's member list, and whether a member may leave it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner decision 2026-08-25, from ArcGIS's own group settings.</b> Two of the three
    /// the owner named; the third — shared update — needed no column, because
    /// <c>item_update</c> had been stored since groups shipped and was honoured nowhere.
    /// </para>
    /// <para>
    /// <b>`member_list` is capped by the group, not independent of it.</b> The two values are
    /// *its members* and *its owner and managers*, and neither reaches outside the group — so
    /// this setting can only ever narrow what `visibility` already allows. An
    /// organisation-wide member list was considered and is not offered: a group visible to
    /// the organisation is discoverable by name, and *who is in it* is a different disclosure
    /// from *it exists*.
    /// </para>
    /// <para>
    /// <b>`members_may_leave` defaults to true, and the capability it governs is new.</b>
    /// Until this migration there was no way for a member to leave a group at all — removal
    /// was an owner's, a manager's or an administrator's act — so the flag would have been a
    /// setting on nothing. Shipping the checkbox without the door is exactly the shape
    /// [D-67](../../../docs/architecture-debt.md) records and the shape `item_update` had.
    /// True is the default because it is what every existing group's members could not do and
    /// now can, which is an addition rather than a change: an administrative group is a
    /// deliberate choice, and defaulting to *nobody may leave* would silently make every
    /// group one.
    /// </para>
    /// <para>
    /// <b>Expand: two columns with defaults, no data touched.</b> A reader from before this
    /// migration ignores both and behaves exactly as it did.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The symbology column stops being bounded, by owner decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-054](../../../docs/adr/ADR-054-the-symbology-document-is-not-bounded.md), which
    /// withdraws ADR-033 §7's fifth condition.</b> That condition asked for the bound to live in
    /// a check constraint rather than in C#, and it was right about *where* — this migration is
    /// not saying the constraint was in the wrong place. It is saying there is no longer a bound
    /// for it to hold.
    /// </para>
    /// <para>
    /// <b>Expand, and unusually literally: it only widens what is accepted.</b> Every row that
    /// satisfied the constraint still satisfies its absence, and a reader from before this
    /// migration reads the same documents it always did — it can only now meet a longer one, and
    /// nothing in the reader ever measured them. There is no contract phase to follow because
    /// nothing was narrowed.
    /// </para>
    /// <para>
    /// <b>Going back is a data question rather than a schema one.</b> Re-adding the constraint
    /// on a database that has since stored a longer document fails, and it should: the way back
    /// is to shorten those documents first. The migration is not written here because a rollback
    /// that silently truncated somebody's classification would be worse than no rollback.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The reference a service is served in, which need not be the one its tables are stored in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-057](../../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5c.</b>
    /// A service had no reference of its own: every face answered in whatever the layer's table
    /// happened to be, and an operator composing a service out of tables stored in three
    /// different systems had no way to say which one clients should get. The owner asked for
    /// exactly that while the Publish screen was being designed — *servisin projeksiyonu 3857
    /// ama aslında db&#39;deki tablonun projeksiyonu 4326 olabilir*.
    /// </para>
    /// <para>
    /// <b>Nullable, and null is not a default in disguise.</b> Null means *the layer&#39;s own*,
    /// which is what every existing service does today, so this migration changes the behaviour
    /// of nothing already published. A number means *serve in this, whatever the table says*,
    /// and the reprojection is the one the drawing and query paths already do — `outSrid` and
    /// `filterSrid` on the query, PostGIS doing the work per feature.
    /// </para>
    /// <para>
    /// <b>Positive if present, checked here rather than in the application.</b> An EPSG code of
    /// zero or a negative one is not a reference, and a column that can hold one is a column
    /// that will. The check names what it refuses.
    /// </para>
    /// <para>
    /// <b>Expand.</b> One nullable column with no backfill: there is nothing to backfill to,
    /// because the absence is the meaning.
    /// </para>
    /// </remarks>
    private static Migration AServiceNamesItsReferenceV39 => Migration.Expand(
        new SchemaVersion(39),
        "A service names the reference it is served in; null keeps the layer's own.",

        "alter table service add column if not exists srid integer",

        """
        alter table service drop constraint if exists service_srid_is_a_reference
        """,

        """
        alter table service add constraint service_srid_is_a_reference
          check (srid is null or srid > 0)
        """);

    private static Migration SymbologyIsNoLongerBoundedV38 => Migration.Expand(
        new SchemaVersion(38),
        "The symbology document has no length bound; ADR-054 withdraws it.",

        """
        alter table layer drop constraint if exists layer_symbology_is_bounded
        """);

    private static Migration GroupMemberListAndLeavingV37 => Migration.Expand(
        new SchemaVersion(37),
        "Who may see a group's members, and whether a member may leave it.",

        """
        alter table sharing_group
          add column if not exists member_list text not null default 'members'
        """,

        """
        alter table sharing_group drop constraint if exists sharing_group_member_list_known
        """,

        """
        alter table sharing_group add constraint sharing_group_member_list_known
          check (member_list in ('members', 'managers'))
        """,

        """
        alter table sharing_group
          add column if not exists members_may_leave boolean not null default true
        """);
}
