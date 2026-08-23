using System.Collections.Immutable;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Geometries;
using Graticula.Cartography;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Graticula.Host;

/// <summary>A candidate data source to test or register.</summary>
/// <param name="Name">An administrator-chosen name. Not needed to test.</param>
/// <param name="ConnectionString">The credential, in the clear over TLS.</param>
internal sealed record DataSourceRequest(string? Name, string? ConnectionString);

/// <summary>A layer to publish.</summary>
/// <remarks>
/// <b><c>ServiceName</c> is the service to publish into, or null for a service
/// of this layer's own.</b> Naming an existing service adds this layer to it at
/// the next free index, which is how three related layers become one service —
/// owner correction 2026-08-15, <em>"a service is a combination of layers"</em>.
/// Omitting it keeps the behaviour every layer published before that date got.
/// </remarks>
internal sealed record PublishRequest(
    string? Name,
    Guid DataSourceId,
    string? SchemaName,
    string? TableName,
    string? GeometryColumn,
    string? IdentityColumn,
    string? ObjectIdColumn,
    int Srid,
    string? GeometryType,
    string? Sharing,
    string? ServiceName = null,
    int? ParentLayerId = null,
    int? CacheSeconds = null,
    string? Folder = null,

    // <b>D-53: refuse a source whose geometry PostGIS calls invalid.</b> Off by default,
    // and that default is the decision rather than the timid option. A registered table
    // belongs to somebody else — 18 invalid rows in 25,280 is a fact about their data,
    // not a reason for this server to decline to serve it — and every layer published
    // before today was published without the question being asked. So the count is
    // *always reported* and the refusal is asked for by whoever wants it.
    bool RequireValidGeometry = false);

/// <summary>Who receives everything a member owns.</summary>
/// <param name="To">The receiving member's sign-in name.</param>
internal sealed record TransferRequest(string? To);

/// <summary>A folder to create in the services directory.</summary>
/// <param name="Name">What to call it. One URL segment, matched without regard to case.</param>
internal sealed record FolderRequest(string? Name);

/// <summary>A group layer to create inside a service.</summary>
/// <param name="Name">What to call it.</param>
/// <param name="Folder">Which folder the service is in, or null for the root.</param>
/// <param name="ParentLayerId">A group to nest it under, or null for the top level.</param>
internal sealed record GroupLayerRequest(string? Name, string? Folder, int? ParentLayerId);

/// <summary>An empty service to create.</summary>
/// <param name="Name">Its name within the folder.</param>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="Description">What it is for, or null.</param>
/// <param name="Sharing">Who may read it. Private unless said otherwise.</param>
internal sealed record ServiceRequest(
    string? Name, string? Folder, string? Description, string? Sharing);

/// <summary>A group to create.</summary>
/// <param name="Name">Its name, unique case-insensitively.</param>
/// <param name="Title">A display title, or null.</param>
/// <param name="Description">What it is for, or null.</param>
/// <param name="ItemUpdate">
/// What it confers on the items shared with it — `none` (the default), `ownItems` or `allItems`.
/// **Fixed here and never again** (ADR-036 §4c): changing it would make every item already shared
/// with the group editable by every member, retroactively.
/// </param>
internal sealed record NewGroupRequest(
    string? Name, string? Title, string? Description, string? ItemUpdate);

/// <summary>A group's editable policies — ADR-036 §4g.</summary>
/// <param name="Title">A display title, or null.</param>
/// <param name="Summary">One line, shown on the group's Overview.</param>
/// <param name="Description">What it is for.</param>
/// <param name="Visibility">
/// Who may discover it: `members` (the default), `organization` or `public`. **Not who may read its
/// items** — that is its members and nobody else, whatever this says.
/// </param>
/// <param name="JoinPolicy">
/// `invitation` (the default), `request` or `self`. **`request` is refused**: it needs a queue of
/// pending requests, and a policy the server stores without honouring is D-67 over again.
/// </param>
/// <param name="Contribute">
/// `managers` (the default) or `members` — who may share a service with the group. Distinct from the
/// item-update capability, which is fixed at creation and has no write path at all.
/// </param>
/// <param name="DeleteLocked">Whether the group is protected from deletion.</param>
/// <remarks>
/// <b>The whole set at once, not a patch.</b> Four radio groups on one screen; a patch API would make
/// *"set visibility"* and *"set everything, with visibility changed"* two requests with one intent,
/// which is where a race lives.
/// </remarks>
internal sealed record GroupSettingsRequest(
    string? Title,
    string? Summary,
    string? Description,
    string? Visibility,
    string? JoinPolicy,
    string? Contribute,
    bool DeleteLocked);

/// <summary>Whether a member is a manager of the group.</summary>
/// <param name="Manager">
/// True to make them a manager — ADR-036 §3's second axis. A manager holds the group's operations
/// inside it without owning it, and may not delete or transfer it.
/// </param>
internal sealed record GroupMemberRequest(bool Manager);

/// <summary>A role to create, with what it grants.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">A one-line description, shown on the roles screen.</param>
/// <param name="Privileges">The privilege names it grants.</param>
internal sealed record NewRoleRequest(
    string? Name, string? Description, IReadOnlyList<string>? Privileges);

/// <summary>What a role grants, replacing all of it.</summary>
/// <param name="Privileges">
/// The complete set. **Not a patch** — ADR-035's directory replaces rather than merges, because
/// *untick one* and *tick everything but one* would otherwise be two requests with one intent.
/// </param>
internal sealed record RolePrivilegesRequest(IReadOnlyList<string>? Privileges);

/// <summary>A change of sharing scope.</summary>
internal sealed record SharingRequest(string? Sharing);

/// <summary>
/// What a service is configured to offer. Null means unset, everywhere.
/// </summary>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="ServesFeatures">Whether the feature face is offered, or null for unset.</param>
/// <param name="ServesTiles">Whether the tile face is offered, or null for unset.</param>
/// <param name="Capabilities">
/// The ceiling, or null for unset. An **empty array is not null**: it means this
/// service offers nothing, which ADR-031 §2a keeps as a legitimate state distinct
/// from stopped.
/// </param>
/// <param name="StatementTimeoutMilliseconds">
/// A timeout this service asks for, or null for the source's own. May only lower.
/// </param>
/// <param name="MaxRecordCount">Most rows one response may carry, or null.</param>
/// <param name="DefaultRecordCount">Rows when the caller does not ask, or null.</param>
/// <param name="MaxResponseBytes">Most bytes one response body may reach, or null.</param>
/// <param name="MaxRequestBytes">Most bytes one request body may carry, or null.</param>
/// <param name="MaxEditsPerTransaction">Most edits one applyEdits may carry, or null.</param>
/// <param name="RequestDeadlineSeconds">
/// How long a client may occupy this service, or null for the server's own bound. **The whole
/// request**, not the database statement — <c>StatementTimeoutMilliseconds</c> above is that, and
/// it stops counting when the query returns. May only lower.
/// </param>
internal sealed record ServiceCapabilitiesRequest(
    string? Folder,
    bool? ServesFeatures,
    bool? ServesTiles,
    IReadOnlyList<string>? Capabilities,
    int? StatementTimeoutMilliseconds,
    int? MaxRecordCount = null,
    int? DefaultRecordCount = null,
    long? MaxResponseBytes = null,
    long? MaxRequestBytes = null,
    int? MaxEditsPerTransaction = null,
    int? RequestDeadlineSeconds = null);

/// <summary>How long a layer's tiles stay fresh.</summary>
/// <param name="Seconds">
/// Seconds, or null to fall back to the server default. <b>Zero is not
/// null</b>: zero means never serve a cached tile, which is a real answer for
/// a layer that changes continuously.
/// </param>
internal sealed record CacheLifetimeRequest(int? Seconds);

/// <summary>A member to create.</summary>
/// <param name="Name">Their sign-in name.</param>
/// <param name="DisplayName">What to show, or null for the name.</param>
/// <param name="Role">The role to grant.</param>
/// <param name="UserType">Their ceiling, or null for the default.</param>
/// <remarks>
/// <b>There is no password field, and that is the decision.</b> Owner rule 2026-08-17: the system
/// issues the password, the administrator may pass it along, and its owner has to replace it. A
/// field here would let a caller choose somebody else's secret, which is the thing being removed —
/// see <see cref="IssuedPassword"/>.
/// </remarks>
internal sealed record MemberRequest(
    string? Name, string? DisplayName, string? Role, string? UserType);

/// <summary>A role to hold, or null to hold none.</summary>
internal sealed record MemberRoleRequest(string? Role);

/// <summary>What one operation on a service with no layers may spend.</summary>
/// <param name="DeadlineSeconds">
/// The cut-off in seconds, or null for the configured default. <b>Null is a value here, not an
/// omission</b> — it is how an administrator asks for the server's default back without typing a
/// copy of it that stops tracking the setting.
/// </param>
/// <param name="PreflightPairs">
/// The pre-flight threshold in candidate segment pairs, zero meaning none, or null for the
/// configured default.
/// </param>
/// <param name="WaitSeconds">
/// How long a request may queue for a free worker, or null for the configured default. <b>A
/// separate budget from the deadline</b>, which is ArcGIS Server Manager's Pooling page's split and
/// the one this server was missing: a deployment can accept long work and still refuse to hold a
/// connection behind somebody else's.
/// </param>
/// <param name="IdleSeconds">
/// How long a worker may sit unused before it is reclaimed — <b>zero meaning never</b>, which is
/// what this pool did before — or null for the configured default.
/// </param>

internal sealed record SystemLimitsRequest(
    int? DeadlineSeconds,
    long? PreflightPairs,
    int? WaitSeconds,
    int? IdleSeconds);

/// <summary>
/// The administrative surface (ADR-017).
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate prefix, versioned separately</b> (§5a), because the admin
/// surface will change far faster than the data APIs.
/// </para>
/// <para>
/// <b>Every mutating call is audited</b> (§5d), including the ones that fail.
/// A register of successful actions answers <em>who did this</em> and cannot
/// answer <em>who tried</em>, and during an incident the second is the question.
/// </para>
/// <para>
/// <b>What is deliberately not here yet</b>, so the gap is visible rather than
/// assumed: §5b's rule that long operations return <c>202</c> and a job id. The
/// job system (ADR-011) is not built, and every operation below completes in one
/// round trip, so returning a job identifier would mean inventing a job resource
/// that is already finished. When registration grows a schema crawl, this
/// becomes wrong and §5b applies.
/// </para>
/// </remarks>
internal static class AdminEndpoints
{
    /// <summary>
    /// Every URL prefix that serves somebody's data, and therefore must be governed.
    /// </summary>
    /// <remarks>
    /// <b>One list, read by the audit and by the test that checks the audit.</b> It
    /// used to be a predicate here and a separate array of family names in
    /// <c>ServiceFolderConformanceTests</c>, so adding a surface meant editing two
    /// places — and the second was the one nobody edited. That is how WMS, MapServer
    /// and OGC API Features went unaudited while <c>/admin/routes</c> reported
    /// <em>ungoverned: 0</em>, which is
    /// [D-119](../../docs/architecture-debt.md)'s own defect recurring on the surfaces
    /// built after it closed.
    /// </remarks>
    public static readonly string[] Served =
    [
        "/rest/services",
        "/wfs",
        "/wms",
        "/ogc/features",
    ];

    /// <summary>Maps the admin surface.</summary>
    public static void MapAdmin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>The endpoint an Esri client probes before it will believe this is a
        // server.</b> ArcGIS Pro's *New ArcGIS Server* connection never touches
        // `/rest`: it asks `GET /admin/generateToken?f=json` and posts a SOAP body
        // to `/services`, and decides from those. Both answered 404, so the
        // connection could not be created and the REST surface behind it was never
        // reached. Found by pointing a real Pro at this server and reading the log
        // (Q-126).
        //
        // <b>Anonymous, deliberately, and it is the one route under /admin that
        // is.</b> A token endpoint that required a token would be a lock with its
        // key inside. The credential check is the same LoginService every other
        // door uses, so the throttle and the audit record are shared rather than
        // reimplemented — what differs is only the error shape, which the admin
        // API spells differently from the REST one.
        app.MapGet("/admin/generateToken", AdminTokenAsync);
        app.MapPost("/admin/generateToken", AdminTokenAsync);

        app.MapGet("/admin/health", HealthAsync);
        app.MapPost("/admin/datasources/test", TestDataSourceAsync);
        app.MapPost("/admin/datasources", RegisterDataSourceAsync);
        app.MapGet("/admin/datasources", ListDataSourcesAsync);

        // <b>The one field a deployment is certain to have to change, and there was no way to.</b>
        // Owner, 2026-08-19: *"registered db path'ini güncelleyemiyorum sanırım"* — a moved host, a new
        // port or a rotated password meant registering a second source and republishing every layer.
        app.MapPut("/admin/datasources/{id:guid}", UpdateDataSourceAsync);
        app.MapDelete("/admin/datasources/{id:guid}", RemoveDataSourceAsync);
        app.MapGet("/admin/datasources/{id:guid}/capability", CapabilityAsync);
        app.MapPost("/admin/layers", PublishAsync);
        app.MapGet("/admin/layers", ListLayersAsync);

        // <b>What this caller owns, and what is shared with them — ADR-034 §5f.</b> The
        // listing above needs `admin:viewAllContent`, so a publisher asking for their own
        // layers is refused: it is an administrator's view of everybody's content. Studio
        // cannot be built on it. This answers the other question, for whoever is asking,
        // with no privilege beyond being signed in.
        app.MapGet("/content/layers", ListMyContentAsync);
        app.MapGet("/content/items", ListContentItemsAsync);

        // <b>ADR-037's surface, and it is a read only.</b> A job is created by the act that needs one —
        // an upload — and never by asking for one, so there is no POST here. Cancelling is absent for
        // the same reason `JobStatus.Cancelled` is unreachable: stopping a worker mid-write needs a
        // decision about what it leaves behind, and offering the control before making that decision
        // is how a half-written table gets created.
        app.MapGet("/admin/jobs", ListJobsAsync);
        app.MapGet("/admin/jobs/{id:guid}", DescribeJobAsync);
        app.MapPut("/admin/layers/{name}/sharing", SetSharingAsync);
        app.MapPut("/admin/layers/{name}/cache", SetCacheLifetimeAsync);
        app.MapPost("/admin/layers/{name}/start", (HttpContext c, string name, IAdminCatalog a, IAuditLog l, CancellationToken t) =>
            SetStatusAsync(c, name, ServiceStatus.Started, a, l, t));
        app.MapPost("/admin/layers/{name}/stop", (HttpContext c, string name, IAdminCatalog a, IAuditLog l, CancellationToken t) =>
            SetStatusAsync(c, name, ServiceStatus.Stopped, a, l, t));
        app.MapDelete("/admin/layers/{name}", UnpublishAsync);
        app.MapPost("/admin/layers/{name}/refresh", RefreshAsync);

        // A service that is not a layer, shared the same way. Owner correction
        // 2026-08-15: "we might make all services public, private or
        // organization" — including the geometry service, which has no layer
        // and was therefore governed by nothing.
        // <b>Members, and until 2026-08-17 there were none to speak of.</b> A deployment had
        // exactly one account for ever: first-run setup made the administrator and nothing made a
        // second, so `admin:manageMembers` was a privilege with nothing behind it and Studio was a
        // surface with no possible occupant — D-56.
        app.MapGet("/admin/members", ListMembersAsync);
        app.MapPost("/admin/members", CreateMemberAsync);
        app.MapPut("/admin/members/{name}/role", SetMemberRoleAsync);
        app.MapPut("/admin/members/{name}/password", SetMemberPasswordAsync);

        // <b>ADR-015 §6c, owner decision 2026-08-18.</b> A member who owns nothing is removed
        // outright; one who owns something is refused unless the request says what to do with it.
        // The holdings are readable on their own so the console can ask before it acts.
        app.MapGet("/admin/members/{name}/holdings", GetMemberHoldingsAsync);
        app.MapPost("/admin/members/{name}/transfer", TransferMemberContentAsync);
        app.MapDelete("/admin/members/{name}", RemoveMemberAsync);
        app.MapPost("/admin/members/{name}/disable", (HttpContext c, string name,
            IMemberDirectory d, IIdentityStore i, IAuditLog l, CancellationToken t) =>
            SetMemberDisabledAsync(c, name, true, d, i, l, t));
        app.MapPost("/admin/members/{name}/enable", (HttpContext c, string name,
            IMemberDirectory d, IIdentityStore i, IAuditLog l, CancellationToken t) =>
            SetMemberDisabledAsync(c, name, false, d, i, l, t));

        app.MapGet("/admin/services", ListSystemServicesAsync);
        app.MapPut("/admin/services/{name}/sharing", SetServiceSharingAsync);

        // <b>A system service can be stopped, since 2026-08-17.</b> The owner asked why the
        // geometry service had no start and no stop, and the answer was that nothing had given
        // it one — <c>system_service</c> carried sharing and nothing else. Two routes rather
        // than a PUT with a body, matching the layer pair, so an operator learns one shape.
        app.MapPost("/admin/services/{name}/start", (HttpContext c, string name,
            PostgresSystemServices y, IAuditLog l, CancellationToken t) =>
            SetSystemStatusAsync(c, name, ServiceStatus.Started, y, l, t));

        app.MapPost("/admin/services/{name}/stop", (HttpContext c, string name,
            PostgresSystemServices y, IAuditLog l, CancellationToken t) =>
            SetSystemStatusAsync(c, name, ServiceStatus.Stopped, y, l, t));

        // <b>The bounds, readable and writable.</b> The owner asked why they could not define the
        // timeout — *"iyi de neden yok. yani ben neden max timeout süresi tanımlayamıyorum?"* —
        // and the honest answer was that nobody had built it, not that it was hard. GET beside PUT
        // because ADR-031 condition 3 asks that configuration be readable through the same API
        // that writes it, and because a screen that cannot read the current value draws its
        // controls from nothing (which shipped once already).
        app.MapGet("/admin/services/{name}/limits", GetSystemLimitsAsync);
        app.MapPut("/admin/services/{name}/limits", SetSystemLimitsAsync);
        // <b>GET as well as PUT, and its absence was a shipped fault.</b> A screen
        // for narrowing what a service offers has to show what it offers now, and
        // there was no route that could say — so the console asked, got 405, and drew
        // its controls from nothing. ADR-031 condition 3 asks that configuration be
        // readable through the same API that writes it.
        app.MapGet("/admin/services/{name}/capabilities", GetServiceCapabilitiesAsync);
        app.MapPut("/admin/services/{name}/capabilities", SetServiceCapabilitiesAsync);

        // <b>Roles, and the privilege that has had nothing behind it since it was written.</b>
        // ADR-035: `admin:manageRoles` existed, was granted to the administrator, and no endpoint
        // required it — D-56's complaint about `admin:manageMembers`, in the privilege this decision
        // is about. These four give it something to guard.
        app.MapGet("/admin/roles", ListRolesAsync);
        app.MapPost("/admin/roles", CreateRoleAsync);
        app.MapPut("/admin/roles/{name}/privileges", SetRolePrivilegesAsync);
        app.MapDelete("/admin/roles/{name}", DeleteRoleAsync);

        // <b>Groups — ADR-036, and they are Studio's by ADR-034 §6.</b> The paths sit under
        // `/admin` because that is where every administrative surface of this server lives; which
        // *screen* they belong to is the console's decision and it puts them in Studio.
        app.MapGet("/admin/groups", ListGroupsAsync);
        app.MapPost("/admin/groups", CreateGroupAsync);
        app.MapDelete("/admin/groups/{name}", DeleteGroupAsync);
        app.MapPut("/admin/groups/{name}/settings", SetGroupSettingsAsync);
        app.MapGet("/admin/groups/{name}", DescribeGroupAsync);
        // <b>Who could be added, for the picker.</b> Names only, and only to somebody who already
        // manages the group — the alternative was granting `admin:manageMembers` so a dropdown could
        // be filled, which is a large privilege for a small need.
        app.MapGet("/admin/groups/{name}/candidates", ListGroupCandidatesAsync);

        app.MapPut("/admin/groups/{name}/members/{member}", SetGroupMemberAsync);
        app.MapDelete("/admin/groups/{name}/members/{member}", RemoveGroupMemberAsync);
        app.MapPut("/admin/groups/{name}/items/{service}", ShareWithGroupAsync);
        app.MapDelete("/admin/groups/{name}/items/{service}", UnshareFromGroupAsync);

        // Group layers. Owner request 2026-08-15: "enable group layers also."
        app.MapGet("/admin/routes", ListRoutesAsync);

        // <b>Listing and deleting a feature service, added 2026-08-17 — D-48.</b> A
        // service could be created and never removed, and worse, never seen: publishing
        // creates one implicitly and unpublishing the last layer leaves it behind, while
        // /admin/layers lists layers and /admin/services lists the system services,
        // which are a different table. So the ordinary residue of a day's work was
        // invisible, and the missing delete had nothing to be missing from.
        // <b>Folders, added 2026-08-17 — owner request.</b> *"örneğin turkiye folderi"*, and
        // then, from their reference's folder list: *"hosted da bir folder."* Both need a
        // folder to be a thing you can list and make, rather than a string that exists only
        // while something is in it.
        app.MapGet("/admin/folders", ListFoldersAsync);
        app.MapPost("/admin/folders", CreateFolderAsync);
        app.MapDelete("/admin/folders/{name}", DeleteFolderAsync);

        app.MapGet("/admin/featureservices", ListServicesAsync);
        app.MapPost("/admin/featureservices", CreateServiceAsync);
        app.MapDelete("/admin/featureservices/{name}", DeleteServiceAsync);

        // D-54: the empty containers a publish-and-unpublish cycle leaves behind. A
        // sweep an operator asks for, because nothing records which services were
        // created deliberately.
        app.MapGet("/admin/featureservices/empty", ListEmptyServicesAsync);
        app.MapPost("/admin/featureservices/sweep", SweepEmptyServicesAsync);
        app.MapPost("/admin/services/{name}/groups", CreateGroupLayerAsync);
        app.MapDelete("/admin/services/{name}/groups/{index:int}", DeleteGroupLayerAsync);

        // <b>The style, which is the one thing about a map this server cannot
        // guess.</b> ADR-028. GET is here rather than only on the public
        // resource so that an author can read back exactly what they stored,
        // including a null meaning "still the generated one".
        app.MapGet("/admin/services/{name}/style", GetStyleAsync);
        app.MapPut("/admin/services/{name}/style", SetStyleAsync);
        app.MapDelete("/admin/services/{name}/style", DeleteStyleAsync);

        // <b>Symbology is per layer, and the style above stays per service.</b>
        // ADR-033 §5d: a style names source layers and orders them, which is a
        // service-level fact; a symbol is a fact about one layer's features. Both
        // exist, and when a service style is stored it wins for the tile face.
        app.MapGet("/admin/layers/{name}/symbology", GetSymbologyAsync);
        app.MapPut("/admin/layers/{name}/symbology", SetSymbologyAsync);
        app.MapDelete("/admin/layers/{name}/symbology", DeleteSymbologyAsync);
    }

    /// <summary>
    /// Every route, and what governs who may reach it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-28, and it exists because an absence has nothing for a reviewer to
    /// look at.</b> The geometry service answered anonymously from the day it
    /// shipped, and no amount of reading the sharing code would have found it —
    /// the sharing code was correct. What was missing was a place for something
    /// that is not content. This turns "is anything ungoverned?" from a question
    /// somebody has to think of into a list they can read, and a test can assert
    /// is empty.
    /// </para>
    /// <para>
    /// <b>Administrative, because it enumerates every route including the ones
    /// a caller may not reach.</b> That is the opposite of what the directory
    /// does, and the reason it is not on the directory.
    /// </para>
    /// </remarks>
    private static async Task ListRoutesAsync(
        HttpContext context, EndpointDataSource endpoints, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        List<object> routes = [];

        foreach (Endpoint endpoint in endpoints.Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            string pattern = route.RoutePattern.RawText ?? string.Empty;

            // Only the data surface. /admin is governed by privileges, which is
            // a different mechanism with its own tests, and /healthz is
            // deliberately open.
            //
            // <b>The data surface stopped being one prefix on 2026-08-19.</b> WFS
            // (ADR-039) serves features from outside /rest/services, and this
            // filter did not know that — so `/wfs` carried its markers and nothing
            // read them, and the endpoint reported *0 ungoverned* about a surface
            // it was not looking at. That is worse than no check: it is the
            // reassurance without the audit, which is the exact failure
            // `SharingGoverned`'s own remark warns about. Found by asking the
            // running server which routes it had audited.
            if (!Data(pattern))
            {
                continue;
            }

            SharingGoverned? governed = endpoint.Metadata.GetMetadata<SharingGoverned>();

            routes.Add(new
            {
                pattern = pattern.StartsWith('/') ? pattern : "/" + pattern,
                methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    ?? (IReadOnlyList<string>)["*"],
                governed = governed is not null,
                by = governed?.Source,
            });
        }

        await Results.Json(new
        {
            routes = routes.OrderBy(r => r.GetType().GetProperty("pattern")!.GetValue(r) as string,
                StringComparer.Ordinal),

            // Stated rather than left for the reader to count. A non-zero number
            // here is ADR-018 condition 5 failing.
            ungoverned = routes.Count(r =>
                !(bool)r.GetType().GetProperty("governed")!.GetValue(r)!),

            // <b>What this audit looked at, said out loud.</b> A reader could see
            // *ungoverned: 0* and not what the zero was about — and on 2026-08-20 it
            // was about a set that excluded three whole protocol surfaces. An audit
            // that does not publish its own scope cannot be checked against the
            // server it audits. [D-119](../../docs/architecture-debt.md).
            filteredOn = Served,
        }).ExecuteAsync(context).ConfigureAwait(false);

        // <b>Every prefix that serves somebody's data.</b> Adding a protocol
        // surface means adding it here, and the conformance suite asserts that
        // each named family actually appears — so a surface forgotten here shows
        // up as a missing family rather than as silence.
        //
        // <b>That safety net did not fire, and the reason is worth keeping.</b>
        // [D-119](../../docs/architecture-debt.md) closed on 2026-08-19 saying
        // exactly the sentence above, and then WMS, MapServer and OGC API Features
        // were built without being added here — so `/admin/routes` went on reporting
        // *ungoverned: 0* about a set that excluded eight routes, which is D-119's own
        // defect with more surfaces behind it. The net could not catch it because the
        // suite's family list named only the surfaces that already existed. **A list
        // that has to be edited twice for one surface gets edited once.**
        static bool Data(string pattern)
        {
            string path = pattern.StartsWith('/') ? pattern : "/" + pattern;

            foreach (string prefix in Served)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The layers this caller may see, and which of them are theirs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No privilege beyond being signed in, and that is the point.</b> Everything else on
    /// this surface asks for one; this asks *who are you* and answers accordingly. A reader
    /// with no content of their own gets the public and organisation-shared layers, which is
    /// exactly what they can already reach through the services directory — so this discloses
    /// nothing new. It reshapes what the directory says into what a content screen needs.
    /// </para>
    /// <para>
    /// <b>Mine and shared-with-me are reported apart</b>, because a publisher acts differently
    /// on each: one they may restyle and unpublish, the other they may only look at. Returning
    /// one flat list and letting the browser guess from an owner name is how a UI ends up
    /// offering a delete that will be refused.
    /// </para>
    /// <para>
    /// <b>The same evaluator the serving path uses</b> — <see cref="LayerAccess"/> — rather
    /// than a second reading of the sharing rules. ADR-018's refusal is deliberately identical
    /// for absent and not-shared, and a listing that computed visibility its own way would
    /// eventually disagree with the endpoint that enforces it. That disagreement is
    /// [D-45](../../docs/architecture-debt.md) in a different shape: two documents, one
    /// question, and a client that cannot act on either.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Everything this caller may see, as items, with why they may see each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked for by the owner, 2026-08-18:</b> *"content can be my own, from my groups, or shared in
    /// organization. I think we need a public section as well to get publicly shared items."* Four
    /// sections, and the interesting part is that **they are not four queries**. <see
    /// cref="LayerAccess.Evaluate"/> already returns exactly this as its <c>Reason</c> — Owner, Group,
    /// Organization, Public — because ADR-018 condition 3 wanted an auditable answer to *why could they
    /// see this* rather than a boolean. The console has been receiving that value on every row of
    /// <c>/content/layers</c> since it was written and throwing it into two buckets called <c>mine</c>
    /// and <c>shared</c>.
    /// </para>
    /// <para>
    /// <b>Items are services, where <c>/content/layers</c> returns layers.</b> A service is the thing
    /// that is shared, owned, named in a directory and drawable; a layer is a member of one. The old
    /// listing is per layer because it was written to feed a map, and it stays for that — this is not a
    /// rename of it. A screen that lets somebody *choose* something to share has to offer the unit
    /// sharing operates on, which is the defect this endpoint exists to fix: the group page offered a
    /// list of qualified service names in a select box, and the owner pointed out that at their scale —
    /// 512 items — a list of names is not something a person can choose from.
    /// </para>
    /// <para>
    /// <b>The whole set, unpaged, and that is a decision with a stated limit.</b> The scale target is
    /// 100–1,000 services (§60), the evaluation is per service in memory because sharing depends on
    /// group membership, and paging in SQL would mean paging *before* the filter that decides
    /// visibility — which cannot produce a correct page count. So the caller receives everything it may
    /// see, with per-scope counts, and pages in the client. **What makes this honest rather than lazy is
    /// that it is measured when it stops being true**: the catalogue read has already been the cost once
    /// in this repository, and the repair was to stop reading every service to answer about one.
    /// </para>
    /// <para>
    /// <b><see cref="LayerAccess.Reason.AdministrativeOverride"/> is reported as its own scope and not
    /// folded into any other.</b> An administrator reading somebody else's private service is
    /// legitimate and must be distinguishable — ADR-018 condition 3 — and a listing that quietly mixed
    /// it into *organization* would be the console misreporting the sharing model to the one person who
    /// can change it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Jobs this caller may see, newest first.
    /// </summary>
    /// <remarks>
    /// <b>Their own, or everybody's for an administrator</b> — the same two axes every other listing
    /// here uses. `?running=true` narrows to what is queued or running, which is the question a screen
    /// asks when it is deciding whether to keep polling.
    /// </remarks>
    private static async Task ListJobsAsync(
        HttpContext context, IJobStore jobs, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "A job belongs to somebody, so this needs to know who you are.")
                .ConfigureAwait(false);
            return;
        }

        bool all = current.Authorization.Allows(Privilege.AdminManageAllContent);
        bool running = context.Request.Query["running"] == "true";

        IReadOnlyList<JobRecord> found = await jobs
            .ListAsync(current.Principal.Id, all, running, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            jobs = found.Select(Wire),
            listing = all ? "every job on the server" : "your jobs",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// One job.
    /// </summary>
    /// <remarks>
    /// <b>404 for somebody else's, not 403.</b> ADR-018's reasoning about private content applied to an
    /// id: a 403 confirms the job exists, which turns this into a way to learn what other people are
    /// doing. The store puts the ownership test in its `where` clause so the two cases are one answer.
    /// </remarks>
    private static async Task DescribeJobAsync(
        HttpContext context, Guid id, IJobStore jobs, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "A job belongs to somebody, so this needs to know who you are.")
                .ConfigureAwait(false);
            return;
        }

        bool all = current.Authorization.Allows(Privilege.AdminManageAllContent);

        JobRecord? found = await jobs
            .FindAsync(id, current.Principal.Id, all, cancellation).ConfigureAwait(false);

        if (found is null)
        {
            await Refuse(context, 404, $"No job '{id}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(Wire(found)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One job on the wire.</summary>
    /// <remarks>
    /// <b>`detail` is passed through as a string rather than re-parsed.</b> It is JSON the worker or the
    /// endpoint wrote and nothing here reads inside it; parsing it to re-serialise it would be two
    /// chances to change what it says and no chance to improve it.
    /// </remarks>
    private static object Wire(JobRecord job) => new
    {
        id = job.Id,
        kind = job.Kind switch
        {
            JobKind.GeodatabaseInspect => "geodatabase.inspect",
            JobKind.GeodatabaseImport => "geodatabase.import",
            _ => "unknown",
        },
        status = job.Status.ToString().ToLowerInvariant(),
        progress = job.Progress,
        subject = job.Subject,
        detail = job.Detail,
        failure = job.Failure,
        created = job.Created,
        started = job.Started,
        finished = job.Finished,
    };

    private static async Task ListContentItemsAsync(
        HttpContext context,
        IAdminCatalog catalog,
        PostgresLayerCatalog layers,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401,
                "This lists the content you can see, so it needs to know who you are. Sign in at "
                + "/rest/auth/login. What is public is in the services directory at /rest/services, "
                + "which needs no credential.").ConfigureAwait(false);
            return;
        }

        // <b>Two listings joined on the qualified name, because neither alone can answer this.</b>
        // `layers.ListServicesAsync` carries `SharedWith` — the groups a service is shared with, which
        // the evaluator needs — and `catalog.ListServicesAsync` carries the owner's *name*, the cover
        // to draw and the two dates. Joining them here beats widening either: one is the runtime's view
        // of what is served and the other is the administrative view of what exists.
        IReadOnlyList<PublishedService> served =
            await layers.ListServicesAsync(cancellation).ConfigureAwait(false);

        IReadOnlyList<AdminService> known =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        Dictionary<string, AdminService> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (AdminService one in known)
        {
            byName[one.Qualified] = one;
        }

        // Which groups this caller is in, so a `group`-scoped service can say *which* group put it
        // here — the fact that makes the scope actionable rather than just a label.
        Dictionary<string, string> groupTitles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<Guid, string> groupNames = [];
        Dictionary<Guid, string> groupKeys = [];

        foreach (GroupSummary g in await groups
            .ListAsync(current.Principal.Id, false, cancellation).ConfigureAwait(false))
        {
            groupTitles[g.Name] = g.Title ?? g.Name;
            groupNames[g.Id] = g.Title ?? g.Name;

            // <b>The key as well as the title, because the two are used for different things.</b> A
            // title is what a reader sees; the *name* is what `/admin/groups/{name}/items/{service}`
            // takes, and the Share dialog has to be able to unshare what it lists (§5l).
            groupKeys[g.Id] = g.Name;
        }

        List<object> items = [];
        List<string> overridden = [];
        SortedSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> counts = new(StringComparer.Ordinal)
        {
            ["mine"] = 0,
            ["group"] = 0,
            ["organization"] = 0,
            ["public"] = 0,
            ["administrative"] = 0,
        };

        foreach (PublishedService service in served)
        {
            LayerAccess.Reason reason = LayerAccess.Evaluate(
                service.Sharing, service.Owner, current.Principal, current.Authorization,
                service.SharedWith);

            if (!reason.IsAllowed())
            {
                continue;
            }

            // <b>Ownership first, and the evaluator deliberately does not do it that way.</b>
            // `Evaluate` checks `Public` before `Owner` because it answers *why are you allowed* and
            // the cheapest sufficient reason is the right answer to that question — a public service is
            // readable whoever asks. **A content scope answers a different question**: *whose is this,
            // and how did it reach me.* Using the reason as the label put ten of this server's eleven
            // services under *public* for the person who owns them, so *My content* would have shown
            // one item to an operator who published all eleven. Measured before it reached a screen.
            //
            // Recorded rather than fixed in `Evaluate`: changing that order would make an audit line
            // say *they read it because they own it* where *because it is public to everybody* is the
            // stronger and more useful fact.
            bool owned = service.Owner is { } who && who == current.Principal.Id;

            string scope = owned
                ? "mine"
                : reason switch
                {
                    LayerAccess.Reason.Group => "group",
                    LayerAccess.Reason.Organization => "organization",
                    LayerAccess.Reason.Public => "public",
                    LayerAccess.Reason.AdministrativeOverride => "administrative",

                    // enum-default-is-deliberate: an unrecognised allowed reason that is not ownership
                    // is reported as the narrowest scope rather than the widest, for the reason
                    // `ReadVisibility` gives — a scope this build does not understand must not read as
                    // *public*.
                    _ => "organization",
                };

            counts[scope]++;
            folders.Add(service.Folder ?? string.Empty);

            if (scope == "administrative")
            {
                overridden.Add(service.QualifiedName);
            }

            byName.TryGetValue(service.QualifiedName, out AdminService admin);

            items.Add(new
            {
                name = service.QualifiedName,
                bare = service.Name,
                folder = service.Folder,
                kind = service.Kind,
                description = service.Description,
                owner = admin.OwnerName,
                sharing = PostgresSharing(service.Sharing),
                status = Wire(service.Status),
                layers = service.Layers.Count,
                scope,

                // <b>Which group, not just *a* group.</b> `Evaluate` iterates the item's groups
                // against the caller's and returns on the first hit, so the answer is in hand at the
                // moment of the decision and was being discarded — leaving a screen able to group rows
                // under *shared with a group you are in* and unable to say which one, which is the
                // weaker sentence and the less actionable one. Empty for every other scope.
                throughGroups = scope == "group"
                    ? service.SharedWith
                        .Where(g => current.Authorization.Groups.Contains(g))
                        .Select(g => groupNames.TryGetValue(g, out string? had) ? had : null)
                        .Where(n => n is not null)
                        .ToList()
                    : [],

                // <b>Every group it is shared with, and only for somebody who may change that.</b>
                // `throughGroups` above answers *how this reached you* and is empty for any other
                // scope, which is what the content sections need. The Share dialog needs a different
                // fact — [ADR-034](../../docs/adr/ADR-034-server-and-studio.md) §5l — and that fact is
                // more sensitive: the set of groups an item is in names other people's teams to
                // anybody who can see the item, including groups they are not in.
                //
                // <b>So it is the owner's answer, or an administrator's.</b> ADR-018's reasoning one
                // level down: a scope is public information about an item, and the set of groups is
                // information about the groups. Absent rather than empty for everybody else, so a
                // reader cannot tell *shared with nothing* from *not yours to know*.
                sharedWith = owned || current.Authorization.Allows(Privilege.AdminManageAllContent)
                    ? service.SharedWith
                        .Select(g => new
                        {
                            // <b>Null when the caller is not in it, which an owner can be.</b> An
                            // administrator may share somebody's item into a group its owner does not
                            // belong to; the owner then sees that it is shared and cannot name the
                            // group, which is the honest answer rather than an invented one.
                            name = groupKeys.TryGetValue(g, out string? keyed) ? keyed : null,
                            title = groupNames.TryGetValue(g, out string? titled)
                                ? titled
                                : "a group you are not in",
                        })
                        .ToList<object>()
                    : null,

                // <b>Why the caller may read it, beside whose it is.</b> The two differ for an owner's
                // public service — theirs, and readable by anybody — and both facts are worth showing:
                // one says which section it belongs in, the other says who else can see it.
                because = reason.ToString().ToLowerInvariant(),

                // <b>What to draw, and it is a layer because a service holds no geometry.</b> The same
                // `cover` the Services screen draws with, so a picker and a listing show the same
                // picture of the same thing rather than two idioms for one fact.
                cover = admin.Cover is null
                    ? null
                    : new
                    {
                        layer = admin.Cover.Value.Name,
                        index = admin.Cover.Value.LayerIndex,
                        url = $"/rest/services/{service.QualifiedName}"
                            + $"/FeatureServer/{admin.Cover.Value.LayerIndex}",
                    },

                // <b>Empty is said rather than left to be inferred from a null cover.</b> A service
                // with no layers has nothing to share into a group and nothing to draw; a picker that
                // offered it would offer a row whose thumbnail can only be hatching and whose add
                // achieves nothing. The listing keeps it — it is real, and its owner needs to see the
                // residue publishing leaves — and says which it is.
                empty = service.Layers.Count == 0,

                updated = admin.Updated == default ? (DateTimeOffset?)null : admin.Updated,
                created = admin.Created == default ? (DateTimeOffset?)null : admin.Created,
            });
        }

        // <b>ADR-018 condition 3, and it was not being kept for a listing.</b> *"An administrator
        // reading a private layer is legitimate and must leave a record, or the sharing model is
        // decorative."* Every per-layer read audits; **the listings did not**, so an administrator
        // could enumerate every private service on the server and leave nothing behind. Found by a
        // design review asking whether the sentence a screen was about to show — *every one of these
        // reads is recorded against your name* — was true. It was not. It is now.
        //
        // <b>One row per listing, naming the count and the services.</b> Not one per item: a listing
        // is one act, and four hundred rows for one screen would make the log unreadable, which is the
        // way an audit trail actually dies.
        if (overridden.Count > 0)
        {
            await AuditAsync(
                context, audit, "content.override", null,
                Detail(new { count = overridden.Count, services = overridden }),
                succeeded: true, cancellation).ConfigureAwait(false);
        }

        await Results.Json(new
        {
            items,
            counts,

            // <b>The folders present in what this caller can see, rather than every folder on the
            // server.</b> A rail offering a folder whose every service is invisible to you is a dead
            // entry, and `/admin/folders` — which lists them all — needs `admin:manageServer`, so a
            // publisher could not fill a rail from it anyway. The root is the empty string, which is
            // how `AdminFolder` reports it too.
            folders,

            groups = groupTitles,

            note = counts["mine"] == 0
                ? "You have published nothing yet. Everything here belongs to somebody else and is "
                  + "listed under why you can see it."
                : null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ListMyContentAsync(
        HttpContext context,
        IAdminCatalog catalog,
        PostgresLayerCatalog layers,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        // <b>`IsAnonymous`, not an empty id — the first version of this check was dead
        // code.</b> An anonymous request carries a real `Principal.Anonymous` with its own
        // identity, because ADR-015 §2a made anonymous a principal whose grants are looked up
        // like anybody's. Comparing its id to `Guid.Empty` therefore never matched, and an
        // unauthenticated caller received a 200 listing the public layers under a heading that
        // says *mine*. Measured, not reasoned: the endpoint answered 200 with no credential.
        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401,
                "This lists your own content, so it needs to know who you are. Sign in at "
                + "/rest/auth/login. The public layers are in the services directory at "
                + "/rest/services, which needs no credential.").ConfigureAwait(false);
            return;
        }

        // The services, because a layer's address is its service and its index — the fact
        // /admin/layers does not carry and D-45 records. A content screen has to be able to
        // build a URL.
        IReadOnlyList<PublishedService> services =
            await layers.ListServicesAsync(cancellation).ConfigureAwait(false);

        List<object> mine = [];
        List<object> shared = [];
        List<object> notShared = [];

        foreach (PublishedService service in services)
        {
            LayerAccess.Reason reason = LayerAccess.Evaluate(
                service.Sharing, service.Owner, current.Principal, current.Authorization,
                service.SharedWith);

            if (!reason.IsAllowed())
            {
                continue;
            }

            bool owned = service.Owner == current.Principal.Id;

            foreach (PublishedLayer layer in service.Layers)
            {
                object entry = new
                {
                    name = layer.Definition.Name,
                    service = service.QualifiedName,
                    folder = service.Folder,
                    layerId = layer.LayerIndex,

                    // The address, built here rather than left to be guessed. This is the
                    // whole of D-45's complaint answered for this listing.
                    url = $"/rest/services/{service.QualifiedName}/FeatureServer/{layer.LayerIndex}",

                    sharing = PostgresSharing(service.Sharing),
                    status = Wire(service.Status),
                    hosted = layer.Definition.IsHosted,
                    geometry = layer.GeometryType.ToString(),

                    // Why it is visible, in the evaluator's own words: owner, organisation, or
                    // public. A publisher reading *organization* knows why they cannot restyle
                    // it before they try.
                    because = reason.ToString(),
                };

                // <b>Three lists, because two of them made a false statement.</b> Everything not
                // owned went into `shared`, under a heading this console renders as *visible to you
                // and owned by somebody else* — including the private services an administrator sees
                // through `admin:viewAllContent`. Nobody shared those. It was invisible on the
                // development server only because one account owns every service there, which is the
                // kind of accident that ships a defect. Found in the design review of 2026-08-18.
                (owned
                    ? mine
                    : reason == LayerAccess.Reason.AdministrativeOverride ? notShared : shared)
                    .Add(entry);
            }
        }

        await Results.Json(new
        {
            mine,
            shared,

            // Private to their owners, and visible only through `admin:viewAllContent`. Its own list
            // rather than folded into `shared`, and the read is recorded — see `/content/items`.
            notShared,

            // Said rather than counted by the caller, because "you have published nothing
            // yet" and "nothing is shared with you" are different first-run screens.
            note = mine.Count == 0
                ? "You have not published anything yet. Everything listed under 'shared' is "
                  + "visible to you but owned by somebody else."
                : null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Every folder, with what is in it.
    /// </summary>
    /// <remarks>
    /// <b>The root is in the list, as an entry with an empty name.</b> Their reference shows
    /// *Site (root)* at the top of its folder list and the owner pointed at that screen; a
    /// list of folders that omits the place half the services are is a list you cannot
    /// navigate from. It carries the same counts as any other entry.
    /// </remarks>
    private static async Task ListFoldersAsync(
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<AdminFolder> folders =
            await catalog.ListFoldersAsync(cancellation).ConfigureAwait(false);

        IReadOnlyList<AdminService> services =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        int rootServices = services.Count(s => s.Folder is null);
        int rootLayers = services.Where(s => s.Folder is null).Sum(s => s.Layers);

        await Results.Json(new
        {
            root = new
            {
                name = string.Empty,
                services = rootServices,
                layers = rootLayers,

                // The root cannot be created or removed, and saying so here means a console
                // does not have to know it as a special case.
                fixedFolder = true,
            },

            folders = folders.Select(f => new
            {
                f.Name,
                f.Services,
                f.SystemServices,
                f.Layers,
                empty = f.IsEmpty,

                // <b>Registered means the folder exists in its own right.</b> False is a
                // folder that only exists because a service points at it — real enough to
                // serve a URL, and it will disappear when that service goes. Migration 18
                // adds no foreign key, so this is reported rather than assumed away.
                f.Registered,

                // Reserved rather than special-cased in the console: `hosted` is the default
                // home for datastore data and `Utilities` holds the geometry service, so
                // removing either would take a folder out from under a URL that answers.
                reserved = Reserved(f.Name),
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Names a folder may not be given, and may not lose.</summary>
    /// <remarks>
    /// <c>hosted</c> is where the datastore publishes by default and <c>Utilities</c> is
    /// where the geometry service lives. <c>System</c> is not used yet and is held back
    /// because ArcGIS uses it, and a folder somebody creates today would collide with a
    /// system folder tomorrow.
    /// </remarks>
    private static bool Reserved(string name) =>
        name.Equals("hosted", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Utilities", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes a folder, so a publish can name it.
    /// </summary>
    /// <remarks>
    /// <b><c>admin:manageServer</c>, not a content privilege.</b> A folder holds no data and
    /// is part of the address space this server serves — creating one is arranging the
    /// server, not publishing content into it.
    /// </remarks>
    private static async Task CreateFolderAsync(
        HttpContext context,
        FolderRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        string name = (request.Name ?? string.Empty).Trim();

        if (!TryReadFolderName(name, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool created = await catalog
            .CreateFolderAsync(name, current.Principal.Id, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "folder.create", name, Detail(new { created }),
            succeeded: true, cancellation).ConfigureAwait(false);

        // <b>200 rather than 201 when it was already there, and not a 409.</b> Creating a
        // folder is arranging a namespace: asking for one that exists has already achieved
        // what the caller wanted, and a conflict would make every publish-into-a-folder flow
        // handle an error that means *fine*.
        context.Response.StatusCode = created ? 201 : 200;

        await Results.Json(new
        {
            name,
            created,
            url = $"/rest/services/{Uri.EscapeDataString(name)}",
            note = created
                ? "The folder exists and is empty. Publish into it by naming it, and it will "
                  + "appear in the services directory."
                : "That folder already existed, so nothing changed. Folder names are matched "
                  + "without regard to case.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a folder, and refuses while anything is in it.
    /// </summary>
    /// <remarks>
    /// <b>Reserved folders are refused outright</b>, before the occupancy check, because an
    /// empty <c>hosted</c> is still where the next datastore publish goes and an empty
    /// <c>Utilities</c> is still the geometry service's address. The refusal says which,
    /// rather than answering 404 for a folder the directory lists.
    /// </remarks>
    private static async Task DeleteFolderAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        if (Reserved(name))
        {
            await Refuse(context, 409,
                $"'{name}' is a reserved folder. 'hosted' is where datastore layers are "
                + "published by default, 'Utilities' holds the geometry service, and 'System' "
                + "is held back for the same reason ArcGIS uses it.").ConfigureAwait(false);
            return;
        }

        (Removal outcome, int services, int system) = await catalog
            .DeleteFolderAsync(name, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "folder.delete", name,
            Detail(new { outcome = outcome.ToString(), services, systemServices = system }),
            succeeded: outcome == Removal.Removed, cancellation).ConfigureAwait(false);

        if (outcome == Removal.Absent)
        {
            await Refuse(context, 404, $"No folder '{name}'.").ConfigureAwait(false);
            return;
        }

        if (outcome == Removal.Occupied)
        {
            await Refuse(context, 409,
                $"'{name}' still holds {Count(services, "service")}"
                + (system > 0 ? $" and {Count(system, "system service")}" : string.Empty)
                + ". A folder is an address, so removing it would take those services' URLs "
                + "with it — move or delete them first.").ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            name,
            removed = true,
            note = "The folder was empty, so nothing published moved or was removed.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a name can be a folder in a URL.
    /// </summary>
    /// <remarks>
    /// <b>Checked here as well as by the database's constraint</b>, and the two say the same
    /// thing on purpose: the constraint is what stops a value arriving by any other route,
    /// and this is what lets the operator read why. A folder becomes a path segment, so a
    /// slash or a percent in one produces a service nobody can address.
    /// </remarks>
    /// <summary>What cannot appear in one segment of a URL.</summary>
    /// <remarks>
    /// The same set migration 18's check constraint refuses, written out in both places on
    /// purpose: the constraint stops a value arriving by another route, and this is what
    /// lets the operator read why theirs was refused.
    /// </remarks>
    private static readonly System.Buffers.SearchValues<char> NotInAFolderName =
        System.Buffers.SearchValues.Create(@"/\?#%");

    private static bool TryReadFolderName(string name, out string? error)
    {
        error = null;

        if (name.Length == 0)
        {
            error = "A folder needs a name.";
            return false;
        }

        if (name.Length > 128)
        {
            error = "A folder name is at most 128 characters.";
            return false;
        }

        if (name.AsSpan().IndexOfAny(NotInAFolderName) >= 0)
        {
            error = $"'{name}' cannot be a folder: a folder is one segment of a URL, so it may "
                  + "not contain / \\ ? # or %.";
            return false;
        }

        if (Reserved(name))
        {
            error = $"'{name}' already exists as a reserved folder — 'hosted' for datastore "
                  + "layers, 'Utilities' for the geometry service, 'System' held back.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Every feature service, and what each one holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Administrative, and it enumerates regardless of sharing</b> — the same
    /// reasoning as the layer listing. What the services *directory* shows is filtered
    /// by what the caller may read; this is the estate, including the private and the
    /// stopped, which is what an administrator has to be able to see.
    /// </para>
    /// <para>
    /// <b>The counts are the reason it exists.</b> A service holding nothing is the one
    /// an operator wants to remove, and until this route there was no way to find one.
    /// </para>
    /// </remarks>
    private static async Task ListServicesAsync(
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<AdminService> services =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            services = services.Select(s => new
            {
                s.Name,
                s.Folder,
                qualified = s.Qualified,
                s.Kind,
                sharing = PostgresSharing(s.Sharing),
                status = Wire(s.Status),
                s.Description,
                owner = s.OwnerName,
                s.Layers,
                s.Groups,

                // Said rather than left to be derived from two numbers, because it is
                // the only question this listing is asked: may I remove this one?
                empty = s.IsEmpty,

                // One member, for a caller that has to draw the service or change its
                // status — see AdminServiceCover. Null for an empty service, and a client
                // must handle that: an empty service is the ordinary residue of
                // unpublishing the last layer.
                cover = s.Cover is { } cover
                    ? new { name = cover.Name, layerIndex = cover.LayerIndex }
                    : null,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a service, and refuses while it still holds anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>409 rather than a cascade</b>, and the refusal names what is in the way. A
    /// cascade would unpublish layers as a side effect of a call about their container,
    /// and unpublishing is not a bookkeeping change: it purges tiles and forgets a
    /// remembered shape. Somebody who wants that should ask for it per layer, where the
    /// response tells them the source table was not touched.
    /// </para>
    /// <para>
    /// <b>404 and 409 are kept apart.</b> Absent means the name may be wrong; occupied
    /// means the name is right and the order of operations is not. Answering the first
    /// for the second sends an operator looking for a service that is in front of them.
    /// </para>
    /// <para>
    /// <b><c>admin:manageAllContent</c>, the same as unpublishing a layer.</b> It is a
    /// content-destroying act on somebody's published estate, not server administration
    /// — and holding the two to one privilege is what stops a role from being able to
    /// remove the container but not the thing inside it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Which services hold nothing, so an operator can see before deciding.
    /// </summary>
    /// <remarks>
    /// <b>A read before a write, and it is what makes the sweep safe to offer.</b>
    /// D-54 refused automatic removal because a service created by a publish and one
    /// created on purpose are the same row. Naming them first moves the judgement to
    /// the person who knows: a container they meant to keep is kept by not pressing
    /// the button.
    /// </remarks>
    private static async Task ListEmptyServicesAsync(
        HttpContext context,
        IAdminCatalog catalog,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
                .ConfigureAwait(false))
        {
            return;
        }

        // <b>`AdminService.IsEmpty` already exists, and using it is the point.</b> It
        // was added when empty services first became visible (D-48), and a second
        // definition of *empty* written here is how two answers to one question start
        // disagreeing — which is D-46's whole subject.
        IReadOnlyList<AdminService> services =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        var empty = services
            .Where(s => s.IsEmpty)
            .Select(s => new
            {
                name = s.Name,
                folder = s.Folder,
                qualified = s.Qualified,
                sharing = s.Sharing.ToString().ToLowerInvariant(),
                kind = s.Kind,
            })
            .ToArray();

        await Results.Json(new
        {
            empty,
            count = empty.Length,
            note = empty.Length == 0
                ? "Every service holds something."
                : "These hold no layers and no groups. Publishing a layer creates the service "
                    + "that holds it and unpublishing the last one leaves the container, so this "
                    + "is usually the residue of that. Nothing records which services were "
                    + "created deliberately, which is why removing them is a decision rather "
                    + "than a cleanup that happens by itself.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes every service that holds nothing.
    /// </summary>
    /// <remarks>
    /// <b>Audited by name, not by count.</b> Removing four services and recording *4*
    /// leaves nobody able to answer *which*, and this is the operation whose whole risk
    /// is that it took away something somebody wanted — so the names go in the record.
    /// </remarks>
    private static async Task SweepEmptyServicesAsync(
        HttpContext context,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
                .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<string> removed =
            await catalog.SweepEmptyServicesAsync(cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "service.sweep", $"{removed.Count} service(s)",
            Detail(new { removed }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            removed,
            count = removed.Count,
            note = removed.Count == 0
                ? "Nothing to remove: every service holds something."
                : "Removed. Nothing was unpublished — a service holding a layer or a group is "
                    + "never swept, which is also why the geometry service is untouched.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteServiceAsync(
        HttpContext context,
        string name,
        string? folder,
        bool? drop,
        IAdminCatalog catalog,
        PostgresLayerCatalog published,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        // <b>`drop=true` empties the service on the way out — owner instruction, ADR-034 §5k.</b> The
        // copy under the delete button used to read *the tables in the datastore are not dropped*, and
        // the owner's answer was that it should not be true: *"servis hosted sa ve silindiyse, datastore
        // dan silinmesi lazım."* Between the two refusals this server had — a service that will not go
        // while it holds layers, and an unpublish that will not touch the table — there was **no route
        // that dropped a hosted table at all**, so importing a geodatabase and changing your mind left
        // fifty-five tables and a database client.
        //
        // <b>Hosted only, and that line is the one this product is built on.</b> A hosted table was
        // created by `PostGisImporter`, named by us, and belongs to the service. A registered one points
        // at somebody else's database and is never touched — its registration goes and the table stays
        // exactly as it was.
        //
        // <b>Off by default, so nothing but the console's own confirmed button does this.</b> A script
        // calling the endpoint the way it always has still gets the 409, which is the answer it was
        // written against.
        List<object> emptied = [];

        if (drop == true)
        {
            IReadOnlyList<PublishedLayer> inside =
                (await published.ListAsync(cancellation).ConfigureAwait(false))
                .Where(l => string.Equals(l.ServiceName, name, StringComparison.Ordinal)
                    && string.Equals(l.Folder ?? null, at, StringComparison.Ordinal))
                .ToList();

            foreach (PublishedLayer layer in inside)
            {
                string layerName = layer.Definition.Name;
                bool hosted = layer.Definition.IsHosted;
                bool dropped = false;
                string? failure = null;

                bool unpublished = await catalog
                    .UnpublishLayerAsync(layerName, cancellation).ConfigureAwait(false);

                if (unpublished)
                {
                    contexts.Forget(layer);

                    // ADR-010 §5.1's *wrong* class rather than the stale one, for the reason
                    // `UnpublishAsync` gives: a name republished over a different table would otherwise
                    // serve the old table's pictures until the TTL ran out.
                    tiles.Purge(layer.Id);
                }

                if (unpublished && hosted)
                {
                    try
                    {
                        await importer.DropAsync(
                            layer.Definition.SchemaName,
                            layer.Definition.TableName,
                            cancellation).ConfigureAwait(false);

                        dropped = true;
                    }
                    catch (Exception refused)
                    {
                        // <b>Reported, not swallowed, and not fatal to the rest.</b> One table that will
                        // not drop — a lock, a permission — must not leave the other fifty-four
                        // unpublished and unaccounted for. The response says which.
                        failure = refused.Message;
                    }
                }

                emptied.Add(new
                {
                    layer = layerName,
                    hosted,
                    unpublished,
                    dropped,
                    table = hosted ? $"{layer.Definition.SchemaName}.{layer.Definition.TableName}" : null,
                    failure,
                });
            }

            // <b>The group layers go too, and counting them was where the first version went wrong.</b>
            // I called `DeleteServiceAsync` to learn how many were left — and that call **deletes the
            // service when it can**, so with the layers already unpublished it succeeded, and the real
            // call afterwards answered *no service* while the tables had in fact been dropped. A right
            // outcome reported as a 404. Found by deleting a service I had made for the purpose.
            //
            // So nothing is asked twice: the group layers are removed by walking indices until the walk
            // stops removing anything, and the service is deleted exactly once, below.
            for (int index = 0, quiet = 0; index < 256 && quiet < 8; index++)
            {
                (Removal went, _) = await catalog
                    .DeleteGroupLayerAsync(name, at, index, cancellation).ConfigureAwait(false);

                quiet = went == Removal.Removed ? 0 : quiet + 1;
            }
        }

        (Removal outcome, int layers, int groups) = await catalog
            .DeleteServiceAsync(name, at, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "service.delete", name,
            Detail(new { folder = at, outcome = outcome.ToString(), layers, groups, dropped = emptied }),
            succeeded: outcome == Removal.Removed, cancellation).ConfigureAwait(false);

        if (outcome == Removal.Absent)
        {
            await Refuse(context, 404,
                $"No service '{name}'" + (at is null ? " at the root." : $" in folder '{at}'."))
                .ConfigureAwait(false);
            return;
        }

        if (outcome == Removal.Occupied)
        {
            await Refuse(context, 409,
                $"'{name}' still holds {Count(layers, "layer")} and {Count(groups, "group layer")}. "
                + "Deleting a service does not delete what is in it — unpublish the layers first, or "
                + "ask for it with drop=true, which unpublishes them and drops the tables of the "
                + "hosted ones. A registered layer's table is never touched.")
                .ConfigureAwait(false);
            return;
        }

        int droppedTables = emptied.Count(e =>
            e.GetType().GetProperty("dropped")?.GetValue(e) is true);

        int leftAlone = emptied.Count(e =>
            e.GetType().GetProperty("hosted")?.GetValue(e) is false);

        await Results.Json(new
        {
            name,
            folder = at,
            removed = true,

            // <b>Per layer, because a service holding one hosted and one registered layer does two
            // different things.</b> A single *deleted* would hide the half that was left alone, which is
            // the half somebody else's database is on the other end of.
            layers = emptied,

            note = emptied.Count == 0
                ? "The service held no layers, so nothing was published through it and no data was "
                  + "removed with it."
                : $"Unpublished {Count(emptied.Count, "layer")}"
                  + (droppedTables > 0 ? $", dropped {Count(droppedTables, "table")}" : "")
                  + (leftAlone > 0
                      ? $", and left {Count(leftAlone, "registered table")} untouched — a registered "
                        + "layer points at a database that is not ours"
                      : "")
                  + ".",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a group layer, and refuses while anything is nested under it.
    /// </summary>
    /// <remarks>
    /// <b>Children are not reparented.</b> Moving a layer to the root as a side effect
    /// of removing the group above it changes where a saved web map finds it — silently,
    /// and for everybody who has one. The refusal counts the children so the operator
    /// can move them deliberately, and the index is never reused afterwards, which is
    /// the same promise the layer indices already make.
    /// </remarks>
    private static async Task DeleteGroupLayerAsync(
        HttpContext context,
        string name,
        int index,
        string? folder,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        (Removal outcome, int children) = await catalog
            .DeleteGroupLayerAsync(name, at, index, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "group.delete", $"{name}/{index}",
            Detail(new { folder = at, index, outcome = outcome.ToString(), children }),
            succeeded: outcome == Removal.Removed, cancellation).ConfigureAwait(false);

        if (outcome == Removal.Absent)
        {
            await Refuse(context, 404,
                $"No group layer {index} in '{name}'"
                + (at is null ? " at the root." : $" in folder '{at}'."))
                .ConfigureAwait(false);
            return;
        }

        if (outcome == Removal.Occupied)
        {
            await Refuse(context, 409,
                $"Group layer {index} still has {Count(children, "child")}. Move them to another "
                + "group or to the top of the service first — they are not reparented for you, "
                + "because that would move them in every saved map that points at them.")
                .ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            service = name,
            folder = at,
            index,
            removed = true,
            note = $"Group layer {index} is gone. Its number is not reused, so a saved map that "
                 + "pointed at it gets a 404 rather than somebody else's layer.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Counts a thing in words, because "1 layers" reads as a bug.</summary>
    private static string Count(int howMany, string what) =>
        howMany == 1 ? $"1 {what}" : $"{howMany} {what}s";

    /// <summary>
    /// Creates an empty service, ready for groups and layers.
    /// </summary>
    /// <remarks>
    /// <b>At <c>/admin/featureservices</c>, not <c>/admin/services</c>.</b> That
    /// path already lists the system services — the geometry service and
    /// whatever joins it — and those are a different kind of thing: they have no
    /// layers, are not published by anybody, and cannot be created. One path
    /// covering both would make <c>GET</c> and <c>POST</c> on the same URL
    /// return and accept unrelated shapes.
    /// </remarks>
    private static async Task CreateServiceAsync(
        HttpContext context,
        ServiceRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await Refuse(context, 400, "'name' is required.").ConfigureAwait(false);
            return;
        }

        SharingScope scope = SharingScope.Private;

        if (request.Sharing is not null
            && !TryReadScope(request.Sharing, out scope, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        // Opening a service to the public is the same act whether it is done at
        // creation or afterwards, so it takes the same privilege.
        if (scope != SharingScope.Private)
        {
            Privilege needed = scope == SharingScope.Public
                ? Privilege.SharingShareToPublic
                : Privilege.SharingShareToOrganization;

            if (!await Authorize.RequireAsync(context, needed).ConfigureAwait(false))
            {
                return;
            }
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        string? folder = string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim();
        string name = request.Name.Trim();

        Guid? id = await catalog.CreateServiceAsync(
            name, folder, request.Description, scope, current.Principal.Id, cancellation)
            .ConfigureAwait(false);

        if (id is not { } created)
        {
            await AuditAsync(
                context, audit, "service.create", name,
                Detail(new { folder }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 409,
                $"A service named '{name}' already exists"
                + (folder is null ? " at the root." : $" in folder '{folder}'."))
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.create", name,
            Detail(new { folder, sharing = PostgresSharing(scope) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(
            new
            {
                id = created,
                name,
                folder,
                sharing = PostgresSharing(scope),
                url = folder is null
                    ? $"/rest/services/{name}/FeatureServer"
                    : $"/rest/services/{folder}/{name}/FeatureServer",
                note = "The service has no layers yet. Add group layers with "
                     + $"POST /admin/services/{name}/groups, and layers by publishing with "
                     + "serviceName set to this service.",
            },
            statusCode: StatusCodes.Status201Created)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a group layer inside a service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Publishing, not administration.</b> Adding a group is arranging your
    /// own content, which is what <c>content:publishFeatures</c> covers — the
    /// same privilege that put the layers there. Requiring an administrative
    /// privilege to tidy them into folders would mean the person who published
    /// three layers cannot group them.
    /// </para>
    /// <para>
    /// <b>A bad parent is refused by the database, and the message says so
    /// plainly.</b> The foreign key requires the parent index to name a group in
    /// the same service, so naming a feature layer as a parent — which no client
    /// knows how to draw — cannot be written at all.
    /// </para>
    /// </remarks>
    private static async Task CreateGroupLayerAsync(
        HttpContext context,
        string name,
        GroupLayerRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await Refuse(context, 400, "'name' is required.").ConfigureAwait(false);
            return;
        }

        try
        {
            GroupLayerAddress? created = await catalog.CreateGroupLayerAsync(
                string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim(),
                name,
                request.Name.Trim(),
                request.ParentLayerId,
                cancellation).ConfigureAwait(false);

            if (created is not { } address)
            {
                await Refuse(context, 404,
                    $"No service '{name}'"
                    + (string.IsNullOrWhiteSpace(request.Folder)
                        ? " at the root. Pass 'folder' if it is in one, e.g. \"hosted\"."
                        : $" in folder '{request.Folder}'.")).ConfigureAwait(false);
                return;
            }

            await AuditAsync(
                context, audit, "service.group.create", $"{name}/{address.LayerIndex}",
                Detail(new { group = request.Name, parent = request.ParentLayerId }),
                succeeded: true, cancellation).ConfigureAwait(false);

            await Results.Json(
                new
                {
                    id = address.Id,
                    name = request.Name,
                    layerId = address.LayerIndex,
                    parentLayerId = request.ParentLayerId ?? -1,
                    type = "Group Layer",
                },
                statusCode: StatusCodes.Status201Created)
                .ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState is "23503" or "23514")
        {
            await AuditAsync(
                context, audit, "service.group.create", name,
                Detail(new { sqlState = e.SqlState }), succeeded: false, cancellation)
                .ConfigureAwait(false);

            await Refuse(context, 400,
                e.SqlState == "23503"
                    ? $"Layer {request.ParentLayerId} of '{name}' is not a group layer, or does "
                      + "not exist. A group can only be nested inside another group — a feature "
                      + "layer cannot contain anything, and no client would know how to draw it."
                    : "A group layer cannot be its own parent.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets a layer's tile cache lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-25, and the number is domain knowledge rather than tuning.</b>
    /// [ADR-010](../../docs/adr/ADR-010-caching.md) §5.3 says volatility is a
    /// per-layer property set by the administrator, and A-028 records why: a
    /// cadastral layer changes twice a year, an incident layer changes every
    /// minute, and nobody but the person who publishes them knows which is
    /// which. Until now it was one global hour, wrong in both directions.
    /// </para>
    /// <para>
    /// <b>Publishing, not administration.</b> Whoever published the layer knows
    /// how often it changes; requiring a server administrator to set it would
    /// put the decision with the person who has the least information.
    /// </para>
    /// </remarks>
    private static async Task SetCacheLifetimeAsync(
        HttpContext context,
        string name,
        CacheLifetimeRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles)
            .ConfigureAwait(false))
        {
            return;
        }

        if (request.Seconds is < 0)
        {
            await Refuse(context, 400,
                "'seconds' cannot be negative. Use 0 for 'never serve a cached tile', or omit it "
                + "to fall back to the server default.").ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetCacheLifetimeAsync(name, request.Seconds, cancellation)
            .ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "layer.cache", name,
            Detail(new { seconds = request.Seconds }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            cacheSeconds = request.Seconds,
            note = request.Seconds switch
            {
                null => "This layer now uses the server's default tile lifetime.",
                0 => "Tiles for this layer are never served from cache, and Cache-Control says "
                     + "no-store so nothing downstream keeps one either.",
                _ => "Tiles for this layer expire after this many seconds, here and in every "
                     + "cache downstream — the same number is sent as Cache-Control max-age. "
                     + "Nothing was purged: changing freshness does not make a cached tile wrong.",
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Every service that is not a layer, and how it is shared.</summary>
    private static async Task ListSystemServicesAsync(
        HttpContext context, PostgresSystemServices services, CancellationToken cancellation)
    {
        // Reading the list is an administrative act: it enumerates services
        // regardless of their sharing, which is exactly what the directory does
        // not do.
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        await Results.Json(new
        {
            services = (await services.ListAsync(cancellation).ConfigureAwait(false))
                .Select(s => new
                {
                    s.Name,
                    s.Kind,
                    s.Folder,
                    sharing = PostgresSharing(s.Sharing),
                    status = Wire(s.Status),
                }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Changes who may use a service that has no layer.</summary>
    /// <remarks>
    /// <b>The same privileges as a layer's sharing, for the same reason.</b>
    /// Opening the geometry service to the public is the same act as opening a
    /// layer to the public — it makes a server resource reachable without an
    /// account — so it takes <c>sharing:shareToPublic</c>, not a separate
    /// administrative privilege that would let one be granted without the other.
    /// </remarks>
    /// <summary>Reads back the stored style, or says there is none.</summary>
    /// <summary>
    /// The canonical symbology document, and both faces derived from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Returns the derived <c>drawingInfo</c> beside the canonical document, and
    /// the losses with it.</b> ADR-033 §5e says the feature face is honest about its
    /// subset; a reader who can see only the canonical document cannot tell what an
    /// ArcGIS client will actually receive, which is the question an operator has.
    /// </para>
    /// <para>
    /// <b>An unstyled layer answers with the generated appearance and says so</b> —
    /// §5b makes that a real answer with a version of 0, rather than an absence.
    /// </para>
    /// </remarks>
    private static async Task GetSymbologyAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindLayerForSymbologyAsync(name, cancellation).ConfigureAwait(false)
            is not { } layer)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        if (layer.Symbology is not { } canonical)
        {
            await Results.Json(new
            {
                name = layer.Name,
                service = layer.ServiceName,
                geometry = layer.Geometry.ToString(),
                stored = false,

                // Zero, exactly as ADR-033 §5b asks: a generated appearance is
                // reported as generated rather than presented as somebody's choice.
                version = 0,
                symbology = (object?)null,
                drawingInfo = FeatureServerMetadataWriter.DrawingInfo(layer.Name, layer.Geometry),
                losses = Array.Empty<string>(),
                note = "This layer has no stored symbology, so both faces draw it in the "
                    + "generated appearance — deterministic from the layer's name, the same "
                    + "colour tomorrow and on another deployment.",
            }).ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        DerivedDrawingInfo derived;

        try
        {
            derived = SymbologyConversion.ToDrawingInfo(canonical, layer.Name, layer.Geometry);
        }
        catch (SymbologyException e)
        {
            // <b>A stored document that cannot be derived is reported, not hidden.</b>
            // It can only happen if a document written by an older build is read by a
            // newer one, and a reader who sees the canonical form and no drawingInfo
            // with no explanation would go looking in the wrong place.
            await Results.Json(new
            {
                name = layer.Name,
                service = layer.ServiceName,
                geometry = layer.Geometry.ToString(),
                stored = true,
                version = 1,
                symbology = System.Text.Json.JsonSerializer.Deserialize<
                    System.Text.Json.JsonElement>(canonical),
                drawingInfo = (object?)null,
                losses = new[] { e.Message },
                note = "The stored document could not be projected onto an Esri renderer, so the "
                    + "feature face falls back to the generated appearance.",
            }).ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Json(new
        {
            name = layer.Name,
            service = layer.ServiceName,
            geometry = layer.Geometry.ToString(),
            stored = true,
            version = 1,
            symbology = System.Text.Json.JsonSerializer.Deserialize<
                System.Text.Json.JsonElement>(canonical),
            drawingInfo = derived.DrawingInfo,
            losses = derived.Losses,
            note = derived.Losses.Count == 0
                ? "Everything in this style survives on both faces."
                : "The tile face draws the canonical document; the list above is what the "
                    + "feature face cannot express.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a symbology document, in either format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-033 §5a: the body may be a MapLibre style or an Esri
    /// <c>drawingInfo</c>.</b> The two are told apart by what they carry rather than
    /// by a flag, so nobody has to declare which they pasted — and a
    /// <c>drawingInfo</c> is converted on the way in, which is the whole of the
    /// migration promise this endpoint exists to make.
    /// </para>
    /// <para>
    /// <b>The losses are in the response, and that is §7's second condition.</b> A
    /// conversion that silently approximates is the failure mode the decision
    /// accepted a risk on; returning the report at the moment somebody writes the
    /// style is the only time they are certainly reading.
    /// </para>
    /// <para>
    /// <b><c>content:publishFeatures</c>, like the service style.</b> Choosing what a
    /// layer looks like is a publisher's job — the person who published it is the
    /// person who knows what colour it should be.
    /// </para>
    /// </remarks>
    private static async Task SetSymbologyAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindLayerForSymbologyAsync(name, cancellation).ConfigureAwait(false)
            is not { } layer)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        string body;

        // Bounded before it is read, for the reason SetStyleAsync gives: reading an
        // unbounded body and then measuring it is an accounting exercise, because the
        // memory is already spent. One char past the limit so that a document exactly
        // at it is accepted and one over is refused.
        using (System.IO.StreamReader reader = new(context.Request.Body))
        {
            char[] buffer = new char[SymbologyConversion.MaximumCharacters + 1];
            int read = 0;

            while (read < buffer.Length)
            {
                int got = await reader
                    .ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellation)
                    .ConfigureAwait(false);

                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            body = new string(buffer, 0, read);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await Refuse(
                context, 400,
                "The body is empty. Send a MapLibre style or an Esri drawingInfo, or DELETE this "
                + "resource to go back to the generated appearance.").ConfigureAwait(false);

            return;
        }

        SymbologyWrite written;

        try
        {
            written = SymbologyConversion.Read(body, layer.Geometry);
        }
        catch (SymbologyException e)
        {
            await Refuse(context, 400, e.Message).ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetSymbologyAsync(name, written.Canonical, cancellation)
                .ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        DerivedDrawingInfo derived =
            SymbologyConversion.ToDrawingInfo(written.Canonical, layer.Name, layer.Geometry);

        // <b>Both sets of losses, kept apart.</b> One list is what did not survive
        // being read; the other is what the feature face cannot express. They are
        // different questions — the first is about the document that was sent and the
        // second about every client that will read it — and merging them would leave
        // an operator unable to tell which they can fix.
        string[] losses = [.. written.Losses, .. derived.Losses];

        // The document is not in the audit record: it is readable through this API
        // anyway, and an audit log that copies its subject is a second place to keep
        // the same thing correct.
        await AuditAsync(
            context, audit, "layer.symbology", name,
            Detail(new
            {
                bytes = written.Canonical.Length,
                from = written.Source,
                replaced = layer.Symbology is not null,
                losses = losses.Length,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = layer.Name,
            service = layer.ServiceName,
            geometry = layer.Geometry.ToString(),
            from = written.Source,
            bytes = written.Canonical.Length,
            replaced = layer.Symbology is not null,
            symbology = System.Text.Json.JsonSerializer.Deserialize<
                System.Text.Json.JsonElement>(written.Canonical),
            drawingInfo = derived.DrawingInfo,
            losses,
            note = losses.Length == 0
                ? "Nothing was lost: both faces draw what you sent."
                : "Stored. The list above is what did not survive — read it now rather than "
                    + "from a client's rendering later.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears a layer's symbology, which puts back the generated appearance.
    /// </summary>
    /// <remarks>
    /// <b>Not a deletion of appearance.</b> §5b: a layer with no stored document has
    /// a generated one, deterministic from its name, and that is a real answer rather
    /// than a blank. The response says which colour it went back to, because *back to
    /// the default* means nothing to somebody looking at a map.
    /// </remarks>
    private static async Task DeleteSymbologyAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindLayerForSymbologyAsync(name, cancellation).ConfigureAwait(false)
            is not { } layer)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        bool had = layer.Symbology is not null;

        if (!await catalog.SetSymbologyAsync(name, null, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "layer.symbology.clear", name,
            Detail(new { had }), succeeded: true, cancellation).ConfigureAwait(false);

        Appearance generated = GeneratedSymbology.For(layer.Name, layer.Geometry);

        await Results.Json(new
        {
            name = layer.Name,
            cleared = had,
            colour = generated.Colour,
            note = had
                ? $"Back to the generated appearance, which for this layer is {generated.Colour}."
                : "This layer had no stored symbology; nothing changed.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task GetStyleAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindServiceForStyleAsync(name, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        if (service.Style is null)
        {
            await Results.Json(new
            {
                name = service.Name,
                stored = false,
                sourceLayers = service.SourceLayers,
                // <b>This sentence said "one colour per geometry type" until 2026-08-17</b>,
                // which was true of the generated style for as long as it was two constants.
                // ADR-033 §5b gave each layer its own colour, so the description outlived the
                // thing it described by exactly one commit — and operator-facing text about
                // what the server will do is the worst place for that.
                note = "This service has no stored style, so it serves a generated one: a "
                     + "deterministic colour per layer, in publication order, with no labels. "
                     + "A style written here replaces it for the tile face only — an ArcGIS "
                     + "client reads drawingInfo from the layer document, which is still "
                     + "generated until ADR-033's canonical per-layer symbology is built.",
            }).ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        // The document as it was stored, byte for byte. An author diffing this
        // against their file should see nothing.
        await Results.Content(service.Style, "application/json; charset=utf-8")
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a style, having checked it against the service it is for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read as text, not bound as a model.</b> A style is an open-ended
    /// document — the specification allows properties this server has never
    /// heard of, and a client that understands them should get them back. Binding
    /// it to a type would silently drop everything the type does not know, which
    /// is the worst possible failure for a document somebody hand-wrote.
    /// </para>
    /// <para>
    /// <b>content:publishFeatures, not an administrator privilege.</b> Styling
    /// a service is a publisher's job, and the person who published a layer is
    /// the person who knows what colour it should be.
    /// </para>
    /// </remarks>
    private static async Task SetStyleAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindServiceForStyleAsync(name, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        string body;

        // <b>Bounded before it is read, not after.</b> Reading an unbounded body
        // and then measuring it is an accounting exercise: the memory is already
        // spent. The cap is one more byte than the limit so that a document
        // exactly at the limit is accepted and one over is refused.
        using (System.IO.StreamReader reader = new(context.Request.Body))
        {
            char[] buffer = new char[StyleDocument.MaximumBytes + 1];
            int read = 0;

            while (read < buffer.Length)
            {
                int got = await reader
                    .ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellation)
                    .ConfigureAwait(false);

                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            body = new string(buffer, 0, read);
        }

        if (!StyleDocument.TryValidate(body, service.SourceLayers, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetStyleAsync(name, body, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        // The document is not in the audit record. It can be a megabyte, it is
        // readable through the API anyway, and an audit log that copies its
        // subject is a second place to keep the same thing correct.
        await AuditAsync(
            context, audit, "service.style", name,
            Detail(new { bytes = body.Length, replaced = service.Style is not null }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            stored = true,
            bytes = body.Length,
            replaced = service.Style is not null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Drops the stored style, returning the service to the generated one.</summary>
    private static async Task DeleteStyleAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindServiceForStyleAsync(name, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        await catalog.SetStyleAsync(name, null, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "service.style.clear", name,
            Detail(new { had = service.Style is not null }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            stored = false,
            had = service.Style is not null,
            note = "Back to the generated style.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <remarks>
    /// <para>
    /// <b>Every service, not only a system service — corrected 2026-08-18.</b> This handler took
    /// only <see cref="PostgresSystemServices"/>, so a path spelled <c>/admin/services/…</c>
    /// answered 404 for every ordinary service on the server. The owner said *"server tarafında
    /// sharing mekanizması çok işlemiyor"*, and this was most of the reason.
    /// </para>
    /// <para>
    /// <b>The consequence was worse than a missing route.</b> A service's scope was reachable only
    /// through <c>PUT /admin/layers/{name}/sharing</c> — which writes the *owning service's*
    /// column, correctly, since migration 11. So a service with three layers had three routes to
    /// one setting ([D-61](../../docs/architecture-debt.md)'s exact shape), and an **empty** service
    /// had none: Server creates empty services, and an empty service is created `private`, so
    /// Server could create a thing whose sharing nothing on the server could change. Measured by
    /// doing it — both candidate routes answered 404.
    /// </para>
    /// <para>
    /// <b>System services keep their own path through this handler.</b> They live in a different
    /// table and are not content; the branch is on which kind of service the name resolves to, and
    /// a system service is tried first because its names are fixed and few.
    /// </para>
    /// </remarks>
    private static async Task SetServiceSharingAsync(
        HttpContext context,
        string name,
        string? folder,
        SharingRequest request,
        PostgresSystemServices services,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!TryReadScope(request.Sharing, out SharingScope scope, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        Privilege needed = scope == SharingScope.Public
            ? Privilege.SharingShareToPublic
            : Privilege.SharingShareToOrganization;

        if (!await Authorize.RequireAsync(context, needed).ConfigureAwait(false))
        {
            return;
        }

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        SystemService? before = await services.FindAsync(name, cancellation).ConfigureAwait(false);

        if (before is null)
        {
            // An ordinary service. One statement, and it reports the scope it replaced.
            SharingScope? had = await catalog
                .SetServiceSharingAsync(name, at, scope, cancellation).ConfigureAwait(false);

            if (had is null)
            {
                await Refuse(context, 404,
                    $"No service '{name}'" + (at is null ? " at the root." : $" in folder '{at}'."))
                    .ConfigureAwait(false);
                return;
            }

            await AuditAsync(
                context, audit, "service.share", name,
                Detail(new { folder = at, from = PostgresSharing(had.Value), to = PostgresSharing(scope) }),
                succeeded: true, cancellation).ConfigureAwait(false);

            await Results.Json(new
            {
                name,
                folder = at,
                from = PostgresSharing(had.Value),
                to = PostgresSharing(scope),
            }).ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        if (!await services.SetSharingAsync(name, scope, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.share", name,
            Detail(new
            {
                from = before is { } b ? PostgresSharing(b.Sharing) : null,
                to = PostgresSharing(scope),
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { name, sharing = PostgresSharing(scope) })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores what a service offers — a ceiling, never a grant (ADR-031).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The privilege is <c>admin:manageServer</c>, not a sharing one.</b>
    /// Narrowing what a service does is server administration; it cannot widen who
    /// may read — that is sharing, and ADR-031 §2b keeps the two apart so there are
    /// not two controls over one fact.
    /// </para>
    /// <para>
    /// <b>The refusals are the domain's own words.</b> An unknown capability name
    /// and a non-positive timeout are both refused by
    /// <see cref="ServiceCapabilityLimits"/>, which explains why — an unrecognised
    /// name would be dropped by the intersection, and a zero timeout is how
    /// PostgreSQL spells *no limit*. Catching its exception and returning its
    /// message keeps one explanation rather than a second one written here that can
    /// drift from it.
    /// </para>
    /// </remarks>
    private static async Task SetServiceCapabilitiesAsync(
        HttpContext context,
        string name,
        ServiceCapabilitiesRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        // <b>Refused rather than rounded, because rounding here would raise it.</b> The bound is
        // enforced as Npgsql's command timeout, which is whole seconds, so 500 ms would be applied
        // as 1,000 — a service asking for less time being given more, which is the one direction
        // §2a forbids. Adjusting an operator's number upward without saying so is worse than
        // refusing it, and no service anywhere has a sub-second value set (checked, not assumed),
        // so nothing is being taken away.
        if (request.StatementTimeoutMilliseconds is { } asked and > 0 and < 1000)
        {
            await Refuse(context, 400,
                $"A statement timeout of {asked} ms cannot be honoured exactly: it is enforced in "
                + "whole seconds, so this would be applied as 1000 ms — more time than you asked "
                + "for. Use 1000 or more, or leave it unset for the data source's own bound.")
                .ConfigureAwait(false);
            return;
        }

        ServiceCapabilityLimits limits;

        try
        {
            limits = new ServiceCapabilityLimits(
                request.ServesFeatures,
                request.ServesTiles,
                request.Capabilities,
                request.StatementTimeoutMilliseconds is { } ms
                    ? TimeSpan.FromMilliseconds(ms)
                    : null)
                .With(new ServiceCostCeilings(
                    request.MaxRecordCount,
                    request.DefaultRecordCount,
                    request.MaxResponseBytes,
                    request.MaxRequestBytes,
                    request.MaxEditsPerTransaction,
                    request.RequestDeadlineSeconds is { } seconds
                        ? TimeSpan.FromSeconds(seconds)
                        : null));
        }
        catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException)
        {
            await Refuse(context, 400, e.Message).ConfigureAwait(false);
            return;
        }

        string? folder = string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim();

        if (!await catalog
            .SetServiceCapabilitiesAsync(name, folder, limits, cancellation)
            .ConfigureAwait(false))
        {
            await AuditAsync(
                context, audit, "service.capabilities", name,
                Detail(new { folder }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 404,
                $"No service '{name}'" + (folder is null ? " at the root." : $" in folder '{folder}'."))
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.capabilities", name,
            Detail(new
            {
                folder,
                servesFeatures = limits.ServesFeatures,
                servesTiles = limits.ServesTiles,
                capabilities = limits.Ceiling,
                statementTimeoutMs = limits.StatementTimeout?.TotalMilliseconds,
                requestDeadlineSeconds = limits.Cost.RequestDeadline?.TotalSeconds,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            folder,
            servesFeatures = limits.ServesFeatures,
            servesTiles = limits.ServesTiles,
            capabilities = limits.Ceiling,
            statementTimeoutMs = limits.StatementTimeout is { } t ? (int?)t.TotalMilliseconds : null,
            maxRecordCount = limits.Cost.MaximumRecordCount,
            defaultRecordCount = limits.Cost.DefaultRecordCount,
            maxResponseBytes = limits.Cost.MaximumResponseBytes,
            maxRequestBytes = limits.Cost.MaximumRequestBytes,
            maxEditsPerTransaction = limits.Cost.MaximumEditsPerTransaction,
            requestDeadlineSeconds = limits.Cost.RequestDeadline is { } deadline
                ? (int?)deadline.TotalSeconds
                : null,

            // <b>Said back, because a ceiling is easy to misread as a grant.</b> An
            // operator who ticks Update on a service whose readers lack the
            // privilege will see no change in behaviour, and this is where that is
            // explained rather than in a document they are not reading.
            note = limits.IsUnset
                ? "Nothing is configured, so this service offers whatever its data supports and "
                  + "its callers' privileges allow."
                : "These are limits, not grants: a caller still needs the privilege. What is "
                  + "served is the intersection of the data, this configuration, and the caller.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Says what a service is configured to offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Same shape as the write, deliberately.</b> The console reads this into the
    /// same controls it later PUTs back, so a difference between the two documents
    /// would show up as a field that silently resets. The one difference is
    /// <c>configured</c>: a caller cannot otherwise distinguish "nothing set" from
    /// "everything set to the default", and those mean different things the next time
    /// the server's own defaults change.
    /// </para>
    /// <para>
    /// <b>Read, not audited.</b> The write is in the audit log because it changes what
    /// the server does; reading a ceiling changes nothing, and an audit trail that
    /// records every screen opening is one nobody reads.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Which kind of service this is, asked of the one catalogue that can answer.
    /// </summary>
    /// <remarks>
    /// <b>A lookup rather than a column on the limits row, because the limits row is a
    /// feature service's.</b> An ImageServer has no capability ceiling and never will;
    /// what the console needs from here is one fact to stop it drawing five operations
    /// the service does not answer.
    /// </remarks>
    /// <summary>Who may read a service, for a page that shows one service.</summary>
    /// <param name="catalog">The admin catalogue.</param>
    /// <param name="coverages">The coverage catalogue.</param>
    /// <param name="name">The service.</param>
    /// <param name="folder">Its folder, or null.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The scope, lower-cased, or null when the service is not found.</returns>
    /// <remarks>
    /// <para>
    /// <b>Two catalogues, because a coverage is not in the feature-service listing.</b> An
    /// ImageServer holds a coverage and is a row in `coverage`; asking only
    /// `ListServicesAsync` would report every image service as *not found* and the page would
    /// show nothing — which is the same class of omission
    /// [D-136](../../docs/architecture-debt.md) records, arriving through a different door.
    /// </para>
    /// <para>
    /// <b>A listing rather than a lookup, because there is no lookup.</b>
    /// `IAdminCatalog` answers *every service* and nothing narrower; adding a
    /// single-service read for one field on one page would be a port change for a caption.
    /// The listing is already what the services screen reads on every visit.
    /// </para>
    /// </remarks>
    private static async Task<string?> SharingOfAsync(
        IAdminCatalog catalog,
        ICoverageCatalog coverages,
        string name,
        string? folder,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coverages);

        if (await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false)
            is { } coverage)
        {
            return coverage.Sharing.ToString().ToLowerInvariant();
        }

        foreach (AdminService service in
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false))
        {
            if (string.Equals(service.Name, name, StringComparison.Ordinal)
                && string.Equals(service.Folder, folder, StringComparison.Ordinal))
            {
                return service.Sharing.ToString().ToLowerInvariant();
            }
        }

        return null;
    }

    private static async Task<string> KindOfAsync(
        ICoverageCatalog coverages,
        string name,
        string? folder,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(coverages);

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        return await coverages.FindAsync(at, name, cancellation).ConfigureAwait(false) is not null
            ? "ImageServer"
            : "FeatureServer";
    }

    private static async Task GetServiceCapabilitiesAsync(
        HttpContext context,
        string name,
        string? folder,
        IAdminCatalog catalog,
        ICoverageCatalog coverages,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        ServiceCapabilityLimits? limits = await catalog
            .FindServiceCapabilitiesAsync(name, at, cancellation).ConfigureAwait(false);

        if (limits is null)
        {
            await Refuse(context, 404,
                $"No service '{name}'" + (at is null ? " at the root." : $" in folder '{at}'."))
                .ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            name,
            folder = at,
            configured = !limits.IsUnset,

            // <b>What the server itself allows, so the console can show it as the placeholder.</b>
            // This file's own rule about controls: a box displaying a figure it did not read is a
            // box that lies the moment somebody changes the setting. `Graticula:RequestDeadline-
            // Seconds` is a deployment's choice, so 600 hard-coded in the console would be wrong
            // for any deployment that chose otherwise. Nought means the deployment asked for no
            // bound at all, which the console says in words rather than as a number.
            serverRequestDeadlineSeconds = settings.RequestDeadline > TimeSpan.Zero
                ? (int?)settings.RequestDeadline.TotalSeconds
                : null,

            // <b>The kind, so the console can tell which settings apply.</b> This
            // endpoint answers for any service and every field below describes a
            // feature service; an ImageServer holds a coverage and has none of them,
            // so the console needs one fact to know not to draw them. Reading it from
            // the row rather than inferring it from null capabilities: a feature
            // service that has never been configured has nulls too.
            kind = await KindOfAsync(coverages, name, at, cancellation)
                .ConfigureAwait(false),

            // <b>And who may read it, because the page that shows a service's settings said
            // nothing about that.</b> [D-141](../../docs/architecture-debt.md): sharing is a
            // pill on the services list, and an operator who arrived from a bookmark or a
            // shared link — the path that was broken for a different reason the same day —
            // read a whole settings page with no sign of whether the thing was private.
            // *You can see it on the other screen* is not an answer when the other screen is
            // not the one they are on.
            sharing = await SharingOfAsync(catalog, coverages, name, at, cancellation)
                .ConfigureAwait(false),

            servesFeatures = limits.ServesFeatures,
            servesTiles = limits.ServesTiles,
            capabilities = limits.Ceiling,
            statementTimeoutMs = limits.StatementTimeout is { } t ? (int?)t.TotalMilliseconds : null,
            maxRecordCount = limits.Cost.MaximumRecordCount,
            defaultRecordCount = limits.Cost.DefaultRecordCount,
            maxResponseBytes = limits.Cost.MaximumResponseBytes,
            maxRequestBytes = limits.Cost.MaximumRequestBytes,
            maxEditsPerTransaction = limits.Cost.MaximumEditsPerTransaction,
            requestDeadlineSeconds = limits.Cost.RequestDeadline is { } deadline
                ? (int?)deadline.TotalSeconds
                : null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Every role, what it grants, and the catalogue it is drawn from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The catalogue travels with the answer, and that is what makes the screen possible.</b> A
    /// console that knew the privilege list would carry a second copy of it, and the two would
    /// disagree the first time one moved — the failure ADR-035 §2 spends three bullets on. So the
    /// response says which privileges exist, which section each belongs to, what each requires and
    /// what each already contains.
    /// </para>
    /// <para>
    /// <b>The administrator is reported with its rows and marked as unchangeable.</b> §4b: the
    /// authorization check never reads them, so they are there for the screen to show. Saying
    /// *"cannot be edited"* in the document is what lets the console disable the controls rather than
    /// letting somebody try.
    /// </para>
    /// </remarks>
    private static async Task ListRolesAsync(
        HttpContext context,
        IRoleDirectory roles,
        IRoleGrants grants,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageRoles).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<RoleGrant> held = await roles.ListAsync(cancellation).ConfigureAwait(false);

        // <b>What the running server is actually using, beside what the store says.</b> They are the
        // same set unless something is wrong, and *unless something is wrong* is not a thing an
        // operator can check without being shown both. This exists because they disagreed during
        // development and no screen could have told anybody.
        ImmutableDictionary<string, ImmutableHashSet<Privilege>> inForce = grants.All();

        await Results.Json(new
        {
            roles = held.Select(r => new
            {
                name = r.Name,
                description = r.Description,
                builtIn = r.BuiltIn,
                members = r.Members,
                privileges = r.Privileges.Select(Roles.NameOf).OrderBy(n => n, StringComparer.Ordinal),

                // The administrator, and only the administrator. A built-in role's *privileges* are
                // editable — the owner's rule is about the administrator, not about being built in.
                editable = !string.Equals(r.Name, Roles.Administrator, StringComparison.Ordinal),

                // Built-in roles cannot be deleted, because the migration seed would recreate them
                // on a fresh store and the two would disagree about what exists.
                removable = !r.BuiltIn,

                // What this process is enforcing right now. Equal to `privileges` unless a refresh
                // has been missed, which is worth being able to see rather than deduce.
                inForce = (inForce.TryGetValue(r.Name, out ImmutableHashSet<Privilege>? live)
                        ? live
                        : [])
                    .Select(Roles.NameOf).OrderBy(n => n, StringComparer.Ordinal),
            }),

            catalogue = Roles.AllPrivileges.Select(p => new
            {
                name = Roles.NameOf(p),

                // <b>What it lets somebody do, D-100.</b> The screen an administrator reads to
                // decide who can do what carried eighteen bare identifiers and no meaning. The
                // sentence lives beside the enum, so a privilege cannot be added without one.
                description = Roles.DescriptionOf(p),

                administrative = Roles.IsAdministrative(p),
                requires = Roles.Prerequisites.TryGetValue(p, out var needs)
                    ? needs.Select(Roles.NameOf)
                    : [],
                includes = Roles.Implies.TryGetValue(p, out var narrower)
                    ? narrower.Select(Roles.NameOf)
                    : [],
            }),

            note = "An administrator passes every privilege check without consulting these rows "
                + "(ADR-035 §4b), so its grants are shown and cannot be changed. Everything else "
                + "is a ceiling narrowed further by each member's user type.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Creates a role.</summary>
    private static async Task CreateRoleAsync(
        HttpContext context,
        NewRoleRequest request,
        IRoleDirectory roles,
        IRoleGrants grants,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageRoles).ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await Refuse(context, 400, "'name' is required.").ConfigureAwait(false);
            return;
        }

        (RoleChange outcome, string? detail) = await roles.CreateAsync(
            request.Name.Trim(),
            request.Description?.Trim() ?? string.Empty,
            request.Privileges ?? [],
            cancellation).ConfigureAwait(false);

        if (outcome != RoleChange.Done)
        {
            await RefuseRoleChangeAsync(context, request.Name, outcome, detail).ConfigureAwait(false);
            return;
        }

        await grants.RefreshAsync(cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "role.create", request.Name,
            Detail(new { privileges = request.Privileges ?? [] }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = request.Name.Trim(),
            privileges = request.Privileges ?? [],
            note = "It grants nothing to anybody until a member is given it.",
        }, statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Replaces what a role grants.</summary>
    private static async Task SetRolePrivilegesAsync(
        HttpContext context,
        string name,
        RolePrivilegesRequest request,
        IRoleDirectory roles,
        IRoleGrants grants,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageRoles).ConfigureAwait(false))
        {
            return;
        }

        if (request.Privileges is null)
        {
            await Refuse(context, 400,
                "'privileges' is required, and an empty array is how a role is emptied. Omitting it "
                + "would make *grant nothing* and *leave unchanged* the same request.")
                .ConfigureAwait(false);
            return;
        }

        (RoleChange outcome, string? detail) = await roles
            .SetPrivilegesAsync(name, request.Privileges, cancellation).ConfigureAwait(false);

        if (outcome != RoleChange.Done)
        {
            await RefuseRoleChangeAsync(context, name, outcome, detail).ConfigureAwait(false);
            return;
        }

        // <b>Before the response, not after.</b> An administrator who has just revoked a privilege
        // and is told it worked must not have the next request answered from a stale set — ADR-031
        // §2b's argument for sharing, and a revocation is the direction where staleness is unsafe.
        await grants.RefreshAsync(cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "role.privileges", name,
            Detail(new { privileges = request.Privileges }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            privileges = request.Privileges,
            note = "In force now. Each member's user type still narrows what this confers on them.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Removes a role nobody holds.</summary>
    private static async Task DeleteRoleAsync(
        HttpContext context,
        string name,
        IRoleDirectory roles,
        IRoleGrants grants,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageRoles).ConfigureAwait(false))
        {
            return;
        }

        RoleChange outcome = await roles.RemoveAsync(name, cancellation).ConfigureAwait(false);

        if (outcome != RoleChange.Done)
        {
            await RefuseRoleChangeAsync(context, name, outcome, null).ConfigureAwait(false);
            return;
        }

        await grants.RefreshAsync(cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "role.delete", name, Detail(new { removed = name }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { name, removed = true })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// One refusal per outcome, each saying what to do instead.
    /// </summary>
    /// <remarks>
    /// <b>In one place because the three handlers share every outcome.</b> A refusal written per
    /// handler is how two of them come to disagree about what *"still held"* means.
    /// </remarks>
    private static Task RefuseRoleChangeAsync(
        HttpContext context, string name, RoleChange outcome, string? detail) => outcome switch
    {
        RoleChange.Absent => Refuse(context, 404, $"No role '{name}'."),

        RoleChange.Exists => Refuse(context, 409,
            $"A role called '{name}' already exists. Edit its privileges instead, or choose "
            + "another name."),

        RoleChange.Administrator => Refuse(context, 409,
            "The administrator role cannot be changed or removed. Its authority is a property of "
            + "the server rather than a set of rows — it passes every privilege check without "
            + "consulting them (ADR-035 §4b) — so an accepted edit would change nothing while "
            + "appearing to. To take administrative power away from somebody, change their role "
            + "rather than the role's privileges."),

        RoleChange.BuiltIn => Refuse(context, 409,
            $"'{name}' is one of the five roles this server ships with, and it cannot be removed: "
            + "the schema seed recreates it on a fresh store, so removing it here would leave two "
            + "answers about which roles exist. Its privileges are editable — PUT "
            + $"/admin/roles/{name}/privileges — and a role you no longer want can be emptied."),

        RoleChange.MissingPrerequisite => Refuse(context, 400,
            $"{detail}. That privilege is useless without the one it requires, so it is refused "
            + "rather than quietly granted alongside it: adding a privilege you did not tick would "
            + "widen the role without saying so. Tick the prerequisite, or untick this."),

        RoleChange.UnknownPrivilege => Refuse(context, 400,
            $"'{detail}' is not a privilege this server has. GET /admin/roles lists every one, "
            + "with what each requires."),

        RoleChange.StillHeld => Refuse(context, 409,
            $"Members still hold '{name}', so removing it would quietly reduce what they may do. "
            + "Change their roles first — GET /admin/members says who holds what."),

        _ => Refuse(context, 500, "Unhandled role outcome."),
    };

    /// <summary>
    /// Whether this caller may act on the administrator role, and the refusal if not.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="what">What they were trying to do, for the sentence.</param>
    /// <returns>True to proceed. If false, the response has been written.</returns>
    /// <remarks>
    /// <para>
    /// <b>ADR-035 §4g and condition 7: reserved by role, not by privilege.</b> Three acts are the
    /// administrator's alone — changing a role to or from administrator, deleting an administrator,
    /// and resetting an administrator's password. All three used to require
    /// <c>admin:manageMembers</c>, and since ADR-035 a deployment can grant that to any role it
    /// likes. So a role holding it could make its own holder an administrator, and §4a's editability
    /// contained its own defeat: one privilege away from every other.
    /// </para>
    /// <para>
    /// <b>Asked of <see cref="Authorization.IsAdministrator"/>, which is not a set of rows.</b> That
    /// flag comes from holding the role, and the role's authority is a property of the code — so no
    /// edit to <c>role_privilege</c> and no <c>UPDATE</c> against the store can produce it.
    /// </para>
    /// <para>
    /// <b>403 rather than 404, unlike the group refusals.</b> Hiding the existence of the
    /// administrator role from somebody who can already list the roles would be hiding nothing; what
    /// this refusal must do is explain that no privilege grants this, or the reader spends their
    /// afternoon editing a role that will never work.
    /// </para>
    /// </remarks>
    private static async Task<bool> AdministratorOnlyAsync(HttpContext context, string what)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Authorization.IsAdministrator)
        {
            return true;
        }

        await Refuse(context, 403,
            $"{what} is reserved to the administrator role, and no privilege grants it. Editing a "
            + "role cannot produce this — the administrator's authority is a property of the server "
            + "rather than a set of grants (ADR-035 §4b), and the acts that create or remove an "
            + "administrator are held to the same rule so that no other role can reach it. Ask an "
            + "administrator.").ConfigureAwait(false);

        return false;
    }

    /// <summary>
    /// Groups this caller can see, and where they stand in each.
    /// </summary>
    /// <remarks>
    /// <b>Membership decides what is listed, and <c>admin:manageAllContent</c> widens it.</b> A
    /// group somebody is not in is not theirs to know about — the same reasoning ADR-018 §3b gives
    /// for a private item. An administrator sees all of them because a group whose owner has left
    /// must be administrable, which is ADR-015 §6c's problem in a new place.
    /// </remarks>
    private static async Task ListGroupsAsync(
        HttpContext context,
        IGroupDirectory groups,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Groups are for members. Sign in.").ConfigureAwait(false);
            return;
        }

        bool all = current.Authorization.Allows(Privilege.AdminManageAllContent);

        IReadOnlyList<GroupSummary> mine = await groups
            .ListAsync(current.Principal.Id, all, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            groups = mine.Select(g => new
            {
                name = g.Name,
                title = g.Title,
                description = g.Description,
                owner = g.Owner,
                itemUpdate = Wire(g.ItemUpdate),
                summary = g.Summary,
                visibility = Wire(g.Visibility),
                joinPolicy = Wire(g.JoinPolicy),
                contribute = Wire(g.Contribute),
                deleteLocked = g.DeleteLocked,
                createdAt = g.CreatedAt,
                members = g.Members,
                items = g.Items,
                standing = g.Standing.ToString().ToLowerInvariant(),

                // What this caller may do to this group, so the screen does not have to work it out
                // from `standing` and a privilege list and get it wrong differently from the server.
                mayManage = all
                    || g.Standing is GroupStanding.Owner or GroupStanding.Manager,
                mayDelete = all || g.Standing == GroupStanding.Owner,
            }),

            listing = all ? "every group on the server" : "groups you own or belong to",

            note = "A group confers reading. What is shared with it is readable by its members and "
                + "by nobody else — editing is still features:edit (ADR-036 §4a).",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One group, its members and what is shared with it.</summary>
    private static async Task DescribeGroupAsync(
        HttpContext context,
        string name,
        IGroupDirectory groups,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Groups are for members. Sign in.").ConfigureAwait(false);
            return;
        }

        bool all = current.Authorization.Allows(Privilege.AdminManageAllContent);

        IReadOnlyList<GroupSummary> visible = await groups
            .ListAsync(current.Principal.Id, all, cancellation).ConfigureAwait(false);

        GroupSummary? found = visible
            .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

        if (found is null)
        {
            // <b>404 rather than 403, and the reason is ADR-018's.</b> A 403 on a named group
            // confirms it exists, which turns this endpoint into a directory of every group on the
            // server for anybody who can guess names.
            await Refuse(context, 404, $"No group '{name}'.").ConfigureAwait(false);
            return;
        }

        // <b>An outsider reached this group through its visibility, and gets its identity and not its
        // contents.</b> ADR-036 §4g: being able to see that a group exists is not being able to read
        // what is in it. So the member list and the item list are withheld on standing rather than
        // filtered — a filtered list of nine members that renders as zero reads as an empty group,
        // which is a different false statement rather than none.
        //
        // <b>An administrator is not an outsider here even when they are outside the group</b>, which
        // is the same exception every other group act makes: a group whose owner has left still has to
        // be administrable.
        bool inside = all || found.Standing != GroupStanding.Outside;

        await Results.Json(new
        {
            name = found.Name,
            title = found.Title,
            summary = found.Summary,
            description = found.Description,
            owner = found.Owner,
            itemUpdate = Wire(found.ItemUpdate),

            // <b>These two were in the listing and not here, and the group's page reads them seven
            // times.</b> So `undefined` was falsy for every caller: the owner of a group, and an
            // unrestricted administrator, were shown a Settings tab saying *"these are the owner's and
            // its managers' to set"* and no controls at all, while a plain member's view was
            // accidentally correct. **The page's whole write surface was invisible to exactly the
            // people it was built for.**
            //
            // <b>Third time in this project that a control has been present and unseeable</b> — after
            // the groups screen's first-run form and *New group* writing into a hidden container — and
            // the lesson from those is the cheap one: assert `offsetParent`, not presence. Found by a
            // design review that could not review the page until it worked around this.
            mayManage = all || found.Standing is GroupStanding.Owner or GroupStanding.Manager,
            mayDelete = all || found.Standing == GroupStanding.Owner,

            visibility = Wire(found.Visibility),
            joinPolicy = Wire(found.JoinPolicy),
            contribute = Wire(found.Contribute),
            deleteLocked = found.DeleteLocked,
            createdAt = found.CreatedAt,
            standing = found.Standing.ToString().ToLowerInvariant(),

            members = inside
                ? (await groups.MembersAsync(found.Name, cancellation).ConfigureAwait(false))
                    .Select(m => new
                    {
                        name = m.Name,
                        displayName = m.DisplayName,
                        standing = m.Standing.ToString().ToLowerInvariant(),
                        joined = m.Joined,
                        addedBy = m.AddedBy,
                    })
                : null,

            items = inside
                ? (await groups.ItemsAsync(found.Name, cancellation).ConfigureAwait(false))
                    .Select(i => new
                    {
                        name = i.Name,
                        sharing = i.Sharing,
                        kind = i.Kind,
                        shared = i.Shared,
                        sharedBy = i.SharedBy,

                        // What a picture of it is drawn from — a service holds no geometry, so the
                        // cover is a layer. Reported here so the Content tab does not have to join
                        // two listings in the browser to show a thumbnail.
                        cover = i.CoverLayer is null
                            ? null
                            : new
                            {
                                layer = i.CoverLayer,
                                index = i.CoverIndex,
                                url = $"/rest/services/{i.Name}/FeatureServer/{i.CoverIndex}",
                            },
                    })
                : null,

            // <b>Said rather than implied by an absence.</b> A reader outside the group needs to know
            // that the lists are withheld and not empty, and so does a script.
            inside,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Members who could be added to this group.</summary>
    /// <remarks>
    /// <b>Guarded by managing the group rather than by a privilege over members.</b> The membership
    /// axis is the authority here (ADR-036 §3): somebody who neither owns nor manages the group has
    /// no business enumerating who could join it, and somebody who does needs exactly this.
    /// </remarks>
    private static async Task ListGroupCandidatesAsync(
        HttpContext context,
        string name,
        IGroupDirectory groups,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in.").ConfigureAwait(false);
            return;
        }

        bool all = current.Authorization.Allows(Privilege.AdminManageAllContent);

        GroupSummary? found = (await groups
                .ListAsync(current.Principal.Id, all, cancellation).ConfigureAwait(false))
            .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

        // 404 rather than 403, for the reason DescribeGroupAsync gives.
        if (found is null)
        {
            await Refuse(context, 404, $"No group '{name}'.").ConfigureAwait(false);
            return;
        }

        if (!all && found.Standing is not (GroupStanding.Owner or GroupStanding.Manager))
        {
            await RefuseGroupChangeAsync(context, name, GroupChange.NotYours, null)
                .ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            group = found.Name,
            candidates = await groups.CandidatesAsync(found.Name, cancellation)
                .ConfigureAwait(false),
            note = "Enabled members who are not in this group yet.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Creates a group.</summary>
    private static async Task CreateGroupAsync(
        HttpContext context,
        NewGroupRequest request,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.GroupsCreate).ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await Refuse(context, 400, "'name' is required.").ConfigureAwait(false);
            return;
        }

        if (!TryReadItemUpdate(request.ItemUpdate, out GroupItemUpdate itemUpdate, out string? bad))
        {
            await Refuse(context, 400, bad!).ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        (GroupChange outcome, Guid id) = await groups.CreateAsync(
            current.Principal.Id,
            request.Name.Trim(),
            request.Title?.Trim(),
            request.Description?.Trim(),
            itemUpdate,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            await RefuseGroupChangeAsync(context, request.Name, outcome, null).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "group.create", request.Name,
            Detail(new { itemUpdate = Wire(itemUpdate) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            id,
            name = request.Name.Trim(),
            itemUpdate = Wire(itemUpdate),
            note = "You own it and manage it. Nothing is shared with it yet, and a service reaches "
                + "its members only once its own sharing scope is 'group'.",
        }, statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Removes a group.</summary>
    private static async Task DeleteGroupAsync(
        HttpContext context,
        string name,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in.").ConfigureAwait(false);
            return;
        }

        GroupChange outcome = await groups.RemoveAsync(
            current.Principal.Id,
            current.Authorization.Allows(Privilege.AdminManageAllContent),
            name,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            await RefuseGroupChangeAsync(context, name, outcome, null).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "group.delete", name, Detail(new { removed = name }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            removed = true,
            note = "The services that were shared with it still exist and are read under their own "
                + "sharing scope. Deleting a group never unpublishes anybody's data.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Changes a group's editable policies.</summary>
    /// <remarks>
    /// <b>Guarded by managing the group, not by a privilege.</b> ADR-036 §3's second axis: these are
    /// settings on one object, and the person who owns or manages it is the one who decides. An
    /// administrator passes through, as everywhere else, because a group whose owner has left still
    /// has to be administrable.
    /// </remarks>
    private static async Task SetGroupSettingsAsync(
        HttpContext context,
        string name,
        GroupSettingsRequest request,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in.").ConfigureAwait(false);
            return;
        }

        if (!TryReadVisibility(request.Visibility, out GroupVisibility visibility, out string? bad)
            || !TryReadJoinPolicy(request.JoinPolicy, out GroupJoinPolicy join, out bad)
            || !TryReadContribute(request.Contribute, out GroupContribute contribute, out bad))
        {
            await Refuse(context, 400, bad!).ConfigureAwait(false);
            return;
        }

        GroupChange outcome = await groups.SetSettingsAsync(
            current.Principal.Id,
            current.Authorization.Allows(Privilege.AdminManageAllContent),
            name,
            request.Title?.Trim(),
            request.Summary?.Trim(),
            request.Description?.Trim(),
            visibility,
            join,
            contribute,
            request.DeleteLocked,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            // Which of the two unbuilt settings was asked for, so the refusal names the right control.
            string? offender = outcome == GroupChange.NotBuilt && visibility == GroupVisibility.Public
                ? "public"
                : request.JoinPolicy;

            await RefuseGroupChangeAsync(context, name, outcome, offender).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "group.settings", name,
            Detail(new
            {
                visibility = request.Visibility,
                joinPolicy = request.JoinPolicy,
                contribute = request.Contribute,
                deleteLocked = request.DeleteLocked,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            group = name,
            visibility = Wire(visibility),
            joinPolicy = Wire(join),
            contribute = Wire(contribute),
            deleteLocked = request.DeleteLocked,
            // <b>The note said 'it can now be found by anybody' while nothing read the column.</b>
            // `public` is refused above, so the only widening this can report is `organization`, and
            // it is now a claim the listing actually keeps: the disjunct is in the `where`, and the
            // reader who arrives through it is `Outside` and gets no member or item list.
            note = visibility == GroupVisibility.Members
                ? "Only its members can find it. What is shared with it was already readable by them "
                  + "and by nobody else, and that has not changed."
                : "Any signed-in member can now find it. What is shared with it is still readable "
                  + "only by its members — being able to see that a group exists is not being able "
                  + "to read what is in it, and the member and item lists are withheld from anybody "
                  + "outside it.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static string Wire(GroupVisibility visibility) => visibility switch
    {
        GroupVisibility.Organization => "organization",
        GroupVisibility.Public => "public",
        _ => "members",
    };

    private static string Wire(GroupJoinPolicy policy) => policy switch
    {
        GroupJoinPolicy.Request => "request",
        GroupJoinPolicy.Self => "self",
        _ => "invitation",
    };

    private static string Wire(GroupContribute contribute) =>
        contribute == GroupContribute.Members ? "members" : "managers";

    private static bool TryReadVisibility(
        string? said, out GroupVisibility visibility, out string? error)
    {
        visibility = GroupVisibility.Members;
        error = null;

        switch (said?.Trim())
        {
            case null or "" or "members": return true;
            case "organization": visibility = GroupVisibility.Organization; return true;
            case "public": visibility = GroupVisibility.Public; return true;

            default:
                error = $"'{said}' is not a group visibility. Use 'members' (the default), "
                    + "'organization' or 'public'. It says who may find the group, not who may read "
                    + "what is shared with it — that is always its members and nobody else.";
                return false;
        }
    }

    private static bool TryReadJoinPolicy(
        string? said, out GroupJoinPolicy policy, out string? error)
    {
        policy = GroupJoinPolicy.Invitation;
        error = null;

        switch (said?.Trim())
        {
            case null or "" or "invitation": return true;
            case "self": policy = GroupJoinPolicy.Self; return true;

            // Accepted here and refused by the store, which is where the reason lives.
            case "request": policy = GroupJoinPolicy.Request; return true;

            default:
                error = $"'{said}' is not a join policy. Use 'invitation' (the default) or 'self'.";
                return false;
        }
    }

    private static bool TryReadContribute(
        string? said, out GroupContribute contribute, out string? error)
    {
        contribute = GroupContribute.Managers;
        error = null;

        switch (said?.Trim())
        {
            case null or "" or "managers": return true;
            case "members": contribute = GroupContribute.Members; return true;

            default:
                error = $"'{said}' is not a contributor policy. Use 'managers' (the default) or "
                    + "'members'. It says who may share a service with the group; what a share lets "
                    + "them do with it afterwards is the group's item-update capability, which was "
                    + "fixed when the group was created.";
                return false;
        }
    }

    /// <summary>Adds a member, or makes one a manager.</summary>
    private static async Task SetGroupMemberAsync(
        HttpContext context,
        string name,
        string member,
        GroupMemberRequest request,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.GroupsManageMembers)
            .ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        GroupChange outcome = await groups.SetMemberAsync(
            current.Principal.Id,
            current.Authorization.Allows(Privilege.AdminManageAllContent),
            name,
            member,
            request.Manager,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            await RefuseGroupChangeAsync(context, name, outcome, member).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "group.member", name,
            Detail(new { member, manager = request.Manager }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            group = name,
            member,
            manager = request.Manager,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Removes a member.</summary>
    private static async Task RemoveGroupMemberAsync(
        HttpContext context,
        string name,
        string member,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.GroupsManageMembers)
            .ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        GroupChange outcome = await groups.RemoveMemberAsync(
            current.Principal.Id,
            current.Authorization.Allows(Privilege.AdminManageAllContent),
            name,
            member,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            await RefuseGroupChangeAsync(context, name, outcome, member).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "group.member.remove", name, Detail(new { member }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { group = name, member, removed = true })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Shares a service with a group.</summary>
    private static Task ShareWithGroupAsync(
        HttpContext context,
        string name,
        string service,
        string? folder,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation) =>
        ShareAsync(context, name, service, folder, groups, audit, true, cancellation);

    /// <summary>Stops sharing a service with a group.</summary>
    private static Task UnshareFromGroupAsync(
        HttpContext context,
        string name,
        string service,
        string? folder,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation) =>
        ShareAsync(context, name, service, folder, groups, audit, false, cancellation);

    private static async Task ShareAsync(
        HttpContext context,
        string name,
        string service,
        string? folder,
        IGroupDirectory groups,
        IAuditLog audit,
        bool wanted,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.GroupsShareTo).ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        GroupChange outcome = await groups.ShareAsync(
            current.Principal.Id,
            current.Authorization.Allows(Privilege.AdminManageAllContent),
            name,
            service,
            string.IsNullOrWhiteSpace(folder) ? null : folder.Trim(),
            wanted,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            await RefuseGroupChangeAsync(context, name, outcome, service).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, wanted ? "group.share" : "group.unshare", name,
            Detail(new { service, folder }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            group = name,
            service,
            folder,
            shared = wanted,

            // <b>Said every time, because it is the step people miss.</b> Sharing into a group and
            // setting the service's scope to `group` are two acts; either alone is a state that
            // reads as done and is not.
            // <b>And said without a route in it.</b> This reaches an operator as a toast; an HTTP
            // method and a path template there is the server explaining its own API to somebody who
            // pressed a button. A design review read it cold and flagged exactly that.
            note = wanted
                ? "Its own sharing scope must be 'group' as well before the group's members can read "
                  + "it — that is set on the service, not here."
                : "Its scope is unchanged. If no group is left, a 'group'-scoped service is readable "
                  + "by its owner and administrators only.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One refusal per outcome.</summary>
    private static Task RefuseGroupChangeAsync(
        HttpContext context, string name, GroupChange outcome, string? target) => outcome switch
    {
        GroupChange.Absent => Refuse(context, 404, $"No group '{name}'."),

        GroupChange.Exists => Refuse(context, 409,
            $"A group called '{name}' already exists. Names are compared without regard to case."),

        GroupChange.NotYours => Refuse(context, 403,
            $"You neither own nor manage '{name}'. The privilege to manage a group's members is not "
            + "the privilege to manage every group's members (ADR-036 §3) — ask its owner to make "
            + "you a manager."),

        GroupChange.OwnerOnly => Refuse(context, 403,
            $"Only the owner of '{name}' may do that. A manager may add members and share items; "
            + "deleting and transferring stay with the owner, which is the difference between "
            + "delegating work and delegating control."),

        GroupChange.NoSuchTarget => Refuse(context, 404,
            $"'{target}' is not something this server has, or is disabled."),

        GroupChange.Locked => Refuse(context, 409,
            $"'{name}' is locked against deletion. Turn that off on its Settings tab first — the lock "
            + "exists because a confirmation is dismissed by habit, and it binds an administrator too: "
            + "a protection the most privileged caller passes through is a protection against typing "
            + "rather than against deleting."),

        // <b>Two settings share this outcome and the message says which.</b> A refusal naming the
        // wrong one of them is worse than a generic refusal: it sends the operator to change a control
        // that was not the problem.
        GroupChange.NotBuilt => Refuse(context, 400, target == "public"
            ? "'public' is a group visibility this server stores and does not honour yet. It would "
              + "mean 'discoverable by anybody, including an anonymous caller', and there is nowhere "
              + "for that to happen: the group listing requires a signed-in caller, so 'public' and "
              + "'organization' would be enforced identically while the setting claimed otherwise. "
              + "Accepting it would report a discovery this server does not perform (D-67, Q-119). "
              + "Use 'members' or 'organization' — 'organization' already makes the group findable by "
              + "every signed-in member without letting any of them read what is in it."
            : $"'{target}' is a join policy this server stores and does not honour yet. Joining by "
              + "request needs a queue of pending requests — a table, a screen, and a decision about "
              + "who reviews them — and none of that is built. Accepting the setting anyway would make "
              + "it a policy reported and unenforced, which this project has already had to correct "
              + "once (D-67). Use 'invitation' or 'self'."),

        GroupChange.Immutable => Refuse(context, 409,
            $"What '{name}' confers on its items was fixed when it was created and cannot be "
            + "changed. Widening it would make every item already shared with the group editable by "
            + "every member, retroactively (ADR-036 §4c). Make a new group and move the shares."),

        _ => Refuse(context, 500, "Unhandled group outcome."),
    };

    private static string Wire(GroupItemUpdate update) => update switch
    {
        GroupItemUpdate.OwnItems => "ownItems",
        GroupItemUpdate.AllItems => "allItems",
        _ => "none",
    };

    private static bool TryReadItemUpdate(
        string? said, out GroupItemUpdate update, out string? error)
    {
        update = GroupItemUpdate.None;
        error = null;

        switch (said?.Trim())
        {
            case null or "" or "none": return true;
            case "ownItems": update = GroupItemUpdate.OwnItems; return true;
            case "allItems": update = GroupItemUpdate.AllItems; return true;

            default:
                error = $"'{said}' is not a group item-update capability. Use 'none' (the default), "
                    + "'ownItems' or 'allItems'. It is fixed at creation and cannot be changed "
                    + "afterwards, so it is worth getting right now (ADR-036 §4c).";
                return false;
        }
    }

    /// <summary>
    /// Health, and it must answer when the platform store does not.
    /// </summary>
    /// <remarks>
    /// ADR-017 §6, and ADR-019 §4 made it the test of whether the seam between
    /// the catalogue and the runtime is real. <b>A 500 from the admin API during
    /// an outage is the worst possible response</b>, because it removes the only
    /// tool the administrator has left.
    /// </remarks>
    /// <summary>
    /// Issues a token in the shape the ArcGIS Server administrative API uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same token as everywhere else.</b> This server has one kind of
    /// session and one place that checks a password; what an ArcGIS client calls
    /// an *administrative* token is, here, the token of whatever account signed
    /// in. Its privileges come from the account (ADR-018), not from the door it
    /// came through, so nothing is elevated by asking at this path rather than at
    /// <c>/rest/generateToken</c>.
    /// </para>
    /// <para>
    /// <b>The error shape is the admin API's, which is not the REST one.</b>
    /// <c>{"status":"error","messages":[...],"code":N}</c> rather than
    /// <c>{"error":{...}}</c>. Answering the REST shape here would be a document
    /// the client cannot read, which is the same failure as answering nothing.
    /// </para>
    /// <para>
    /// <b>A credential-less probe is answered rather than refused blankly.</b> Pro
    /// asks with no username at all, to find out whether this endpoint exists. It
    /// gets a 401 with a message saying what is missing — which is true, is
    /// readable, and is not a 404.
    /// </para>
    /// </remarks>
    private static async Task AdminTokenAsync(
        HttpContext context,
        LoginService login,
        CancellationToken cancellation)
    {
        string? name = null;
        string? password = null;

        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation)
                .ConfigureAwait(false);

            name = form["username"].ToString();
            password = form["password"].ToString();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = context.Request.Query["username"].ToString();
        }

        if (string.IsNullOrEmpty(password))
        {
            password = context.Request.Query["password"].ToString();
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
        {
            // <b>200, and this is the one place this server answers a failure with
            // a success code.</b> A request carrying no credentials at all is not a
            // rejected sign-in; it is ArcGIS Pro asking whether this endpoint
            // exists, which is the first thing it does when adding a server. Esri's
            // own administrative API answers that question with 200 and an error
            // object, and a client that reads 401 as *no such server* stops there —
            // which it did, measured, with the 401 this used to send.
            //
            // <b>The distinction it rests on is real rather than convenient:</b> a
            // probe with no username is a different event from a wrong password,
            // and the wrong password still gets 401 below. The status code stays
            // truthful about authentication and stops being wrong about existence.
            await AdminTokenError(
                    context,
                    StatusCodes.Status200OK,
                    StatusCodes.Status400BadRequest,
                    "Token not generated. 'username' and 'password' are required.")
                .ConfigureAwait(false);

            return;
        }

        LoginResult result = await login
            .AuthenticateAsync(name, password, context.Connection.RemoteIpAddress, cancellation)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // One message for wrong-name, wrong-password and disabled, for the
            // reason the other doors give: distinguishing them is an account
            // enumeration oracle. A throttle is still told apart, because a
            // locked-out administrator cannot learn that any other way.
            (int status, string message) = result.Failure switch
            {
                LoginFailure.AddressThrottled or LoginFailure.AccountThrottled => (
                    StatusCodes.Status429TooManyRequests,
                    "Token not generated. Too many failed sign-in attempts. Wait and try again."),
                _ => (
                    StatusCodes.Status401Unauthorized,
                    "Token not generated. The name or password is incorrect."),
            };

            await AdminTokenError(context, status, status, message).ConfigureAwait(false);
            return;
        }

        AuthenticatedSession session = result.Session!.Value;

        await Results.Json(new
        {
            token = result.Token!,
            expires = session.ExpiresAt.ToUnixTimeMilliseconds(),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>An administrative-API error, whose two codes are not the same thing.</summary>
    /// <remarks>
    /// <b>The transport says whether the endpoint answered; the body says what went
    /// wrong.</b> They agree everywhere except the credential-less probe, where the
    /// endpoint exists and answers — 200 — about a request that was incomplete —
    /// 400. Collapsing them would mean either telling a prober there is no server
    /// here, or telling a monitor that a failure was a success.
    /// </remarks>
    private static Task AdminTokenError(
        HttpContext context, int status, int code, string message) =>
        Results.Json(
            new
            {
                status = "error",
                messages = new[] { message },
                code,
            },
            statusCode: status).ExecuteAsync(context);

    private static async Task HealthAsync(
        HttpContext context,
        IAdminCatalog catalog,
        ServiceContexts contexts,
        ITileCache tiles,
        TileSingleFlight flight,
        ConnectionBudget budget,
        CancellationToken cancellation)
    {
        string? storeError = null;
        int layers = 0;

        try
        {
            layers = (await catalog.ListLayersAsync(cancellation).ConfigureAwait(false)).Count;
        }
        catch (NpgsqlException e)
        {
            storeError = e.Message;
        }

        // <b>Reachable without authentication, and therefore redacted.</b> It
        // has to be anonymous: sessions live in the platform store, so during
        // the outage this endpoint exists for, nobody can authenticate. That
        // makes the raw error a disclosure to anyone who can reach the port —
        // it named the store's host and port until this was noticed — so the
        // detail is shown only to a caller who has proved they operate the
        // server, and during an outage that is nobody. D-03's rule, and the
        // reason ADR-017 §6's break-glass path is still owed (A-051).
        //
        // <b>The redaction covered the error and nothing else, until the §66
        // security gate on 2026-08-15.</b> An anonymous caller was told there
        // are 26 layers while the catalogue showed them 2 services — so this
        // endpoint published the existence of private content, on a server that
        // answers 404 for a private layer precisely so that nobody can learn it
        // is there. Every other surface refuses to confirm; this one counted.
        //
        // So an anonymous caller now gets what the endpoint is for and nothing
        // else: is it alive, and is the store reachable. Inventory, cache state
        // and our own version are operational detail, and operational detail is
        // for operators.
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool maySeeDetail = current.Authorization.Allows(Privilege.AdminManageServer);

        Dictionary<string, object?> health = new()
        {
            ["status"] = storeError is null ? "ok" : "degraded",

            ["platformStore"] = new
            {
                reachable = storeError is null,
                error = maySeeDetail ? storeError : storeError is null ? null : "redacted",
            },
        };

        if (!maySeeDetail)
        {
            health["note"] =
                "Liveness only. Counts, cache state and the server version are shown to a caller "
                + "holding admin:manageServer, because an inventory is a disclosure about content "
                + "this server otherwise refuses to confirm exists.";

            await Results.Json(health).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        health["version"] =
            typeof(AdminEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown";

        // <b>The counters a load run needs to tell an allocation ceiling from a
        // connection-pool limit.</b> Four benchmark rounds concluded that this
        // system's cost is memory traffic and that it is invisible at
        // concurrency 1; the §66 performance gate then failed on F1 because the
        // feature path had never been measured, and a black-box probe could say
        // only that throughput plateaued. Throughput alone cannot distinguish a
        // server that is out of memory bandwidth from one that is out of
        // connections from one sharing a host with something else. Allocation
        // rate, GC pause share and CPU can.
        //
        // <b>Here rather than at /metrics, and that is the decision.</b> The
        // experiment harness had a public /metrics; this route already exists,
        // already resolves a principal, and already redacts everything below
        // this line from a caller without admin:manageServer. Adding a second
        // surface would mean a second authorization story for the same
        // disclosure — and D-03 records over-disclosure to unauthenticated
        // callers as open debt, so a new anonymous endpoint reporting heap size
        // and uptime is the wrong direction.
        //
        // <b>Sampled either side of a run and subtracted</b>, which is why they
        // are cumulative totals rather than rates. A rate computed here would be
        // a rate over the server's whole life, which is not a measurement of
        // anything a caller is doing.
        Process self = Process.GetCurrentProcess();

        health["runtime"] = new
        {
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
            heapBytes = GC.GetTotalMemory(forceFullCollection: false),

            // Not a GC "count" per se: these are cumulative collection counts by
            // generation, so a difference over a run is how many happened during
            // it.
            gen0 = GC.CollectionCount(0),
            gen1 = GC.CollectionCount(1),
            gen2 = GC.CollectionCount(2),

            // <b>The number four rounds kept coming back to.</b> One measured
            // 80.9% of wall time in GC pause at 18% CPU: a server doing almost
            // nothing and stopped most of the time.
            gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,

            cpuMilliseconds = self.TotalProcessorTime.TotalMilliseconds,
            uptimeMilliseconds = (DateTimeOffset.UtcNow - self.StartTime.ToUniversalTime())
                .TotalMilliseconds,
            cores = Environment.ProcessorCount,
            serverGc = System.Runtime.GCSettings.IsServerGC,
        };

        health["platformStore"] = new
        {
            reachable = storeError is null,
            layers,
            error = storeError,
        };

        // Not a vanity statistic. This is the one piece of state the request
        // path carries between requests, and a cache nobody can see is a
        // cache nobody suspects when the answers go stale. The lifetime is
        // reported alongside the count so an operator reading a wrong field
        // list knows exactly how long to wait, or that /refresh exists.
        // ADR-010 §6b: cache state must be readable. An operator asking
        // "is this seeded" or "why is the disk full" has no other way to
        // find out, and a cache nobody can see is one nobody suspects when
        // the datastore starts working harder than it should.
        /*
          <b>What admission control is actually doing, which was unobservable.</b>
          [ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md):
          the budget refuses when too many callers are already waiting, and until this was
          reported there was no way to tell a server that is shedding load from one that is
          quietly queueing. `SemaphoreSlim` exposes free permits and not waiters, so the count
          is the budget's own.

          <b>Operational detail, so it is behind the privilege like everything else here.</b>
          How close a deployment is to its bound is a capacity fact about the server, which is
          exactly the class of thing the §66 security gate took out of the anonymous answer.
        */
        health["admissionControl"] = new
        {
            perSource = budget.PerSource,
            worker = budget.Worker,
            queueDepth = budget.QueueDepth,
            waitSeconds = budget.Wait.TotalSeconds,
            waitingForWorker = budget.WaitingForWorker,
            waitingForSource = budget.WaitingForSource,
        };

        health["tileCache"] = new
        {
            entries = tiles.Report(null).Entries,
            megabytes = Math.Round(tiles.Report(null).Bytes / 1048576.0, 1),

            // Builds in flight. A number that stays high is a datastore that
            // cannot keep up with cold tiles, which looks like a slow server and
            // is actually a slow query.
            building = flight.InFlight,
            note = "Tiles held on disk. A miss is datastore load (ADR-021), so this is the "
                 + "number that says how much of the tile traffic the datastore is actually "
                 + "seeing. X-Tile-Cache on a tile response says HIT or MISS.",
        };

        health["describedShapes"] = new
        {
            count = contexts.Count,
            lifetimeSeconds = (int)ServiceContexts.Lifetime.TotalSeconds,

            // <b>Two numbers, because they answer different questions during an
            // outage.</b> D-127: `count` is how much is fresh and `servableWhileBlind` is
            // how much can still be answered from memory. The gap between them used to be
            // the whole of ADR-026's fifteen minutes turning into thirty seconds, and an
            // administrator reading this during an outage is the person who needs to know
            // which number they are looking at.
            servableWhileBlind = contexts.KnownCount,
            note = "Table shapes remembered from the data source (D-17). `count` expires "
                 + "after lifetimeSeconds so an ALTER TABLE is noticed; "
                 + "`servableWhileBlind` does not expire and is used only when the source "
                 + "cannot be reached at all, so a document that needs a field list still "
                 + "has one for as long as the catalogue memory lasts (ADR-026). Sharing "
                 + "and started/stopped are deliberately NOT cached and are read per "
                 + "request. POST /admin/layers/{name}/refresh forgets both immediately.",
        };

        // Said explicitly in the degraded case, because an administrator
        // reading this during an outage should not have to infer which half
        // of the answer they are allowed to trust.
        health["note"] = storeError is null
            ? "The platform store is reachable, so everything below is current."
            : "The platform store is unreachable. Serving continues for public layers already "
              + "seen (ADR-026), but the catalogue, identity and audit are unavailable, so most "
              + "of this API will refuse. This is the failure ADR-019 accepted when it fused the "
              + "catalogue and the runtime into one deployable.";

        await Results.Json(health).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-017 §3.3's dry run: connect, check rights, list what is publishable,
    /// and create nothing.
    /// </summary>
    private static async Task TestDataSourceAsync(
        HttpContext context,
        DataSourceRequest request,
        IDataSourceProbe probe,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            await Refuse(context, 400, "connectionString is required.").ConfigureAwait(false);
            return;
        }

        ProbeResult result =
            await probe.ProbeAsync(request.ConnectionString, cancellation).ConfigureAwait(false);

        // 200 even when the source is unusable. The request succeeded — the
        // answer is "no", and it is the answer that was asked for. A 4xx here
        // would make a working diagnostic look like a broken call.
        await Results.Json(Describe(result)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a registered source that nothing is published on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other half of being able to correct a mistake.</b> A source could be registered and,
    /// from 2026-08-19, corrected — and still not removed, so a typo in a name or a database that
    /// turned out to be the wrong one stayed in the list for ever. The same afternoon the owner asked
    /// for the update, which is what made the gap visible: they are the same gap.
    /// </para>
    /// <para>
    /// <b>Refused while it holds layers, and the count is in the refusal.</b> Cascading would unpublish
    /// somebody's services as a side effect of tidying a list — the reasoning
    /// <c>DELETE /admin/featureservices</c> already uses for a service that holds layers.
    /// </para>
    /// <para>
    /// <b>And the datastore cannot be removed at all.</b> It is not a registration an operator made;
    /// it is where hosted data lives, created at setup and depended on by every import. Removing its
    /// row would leave the server unable to publish anything hosted, with no way back through the API.
    /// </para>
    /// </remarks>
    private static async Task RemoveDataSourceAsync(
        HttpContext context,
        Guid id,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        RegisteredDataSource? found = null;

        foreach (RegisteredDataSource one in
                 await catalog.ListDataSourcesAsync(cancellation).ConfigureAwait(false))
        {
            if (one.Id == id)
            {
                found = one;
                break;
            }
        }

        if (found is null)
        {
            await Refuse(context, 404, $"No data source '{id}'.").ConfigureAwait(false);
            return;
        }

        // <b>The datastore is recognised by its name, which is the one place that name is load
        // bearing.</b> `HostedDataEndpoints` finds it the same way; the alternative is a column, and
        // ADR-019 gave the datastore a reserved name precisely so that no schema change is needed to
        // know which source it is.
        if (string.Equals(
                found.Value.Name,
                PostgresAdminCatalog.DatastoreName,
                StringComparison.Ordinal))
        {
            await Refuse(
                context, 409,
                "That is the datastore, and it cannot be removed. It is not a registration somebody "
                + "made — it is where hosted data lives, and without its row this server cannot "
                + "publish or import anything hosted.").ConfigureAwait(false);
            return;
        }

        if (found.Value.LayerCount > 0)
        {
            await Refuse(
                context, 409,
                $"'{found.Value.Name}' still has {Count(found.Value.LayerCount, "layer")} published on "
                + "it. Unpublish them first: removing the source would take their services with it, and "
                + "unpublishing has consequences of its own — it purges tiles and forgets a remembered "
                + "shape — which should not happen as a side effect of tidying a list.")
                .ConfigureAwait(false);
            return;
        }

        if (!await catalog.RemoveDataSourceAsync(id, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No data source '{id}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "datasource.remove", found.Value.Name,
            Detail(new { id }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            id,
            name = found.Value.Name,
            removed = true,
            note = "Nothing was published on it, so nothing moved or stopped serving.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a registered source's connection string, after checking the new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Probed before written, like registration — and then checked further, because this is worse
    /// than registration when it is wrong.</b> A bad registration leaves a source nobody has published
    /// on. A bad *update* points every layer already on that source at a database that does not hold
    /// their tables, and the symptom arrives later as 503s on services that were working.
    /// </para>
    /// <para>
    /// <b>So the tables are checked too, and a mismatch is refused rather than warned about.</b>
    /// Every layer on the source names a schema and a table; the new connection is asked whether they
    /// are there. If any are missing the update is refused and they are named — the common case for
    /// this endpoint is *the same database at a new address*, where nothing is missing, so a refusal
    /// here means the operator has pointed it somewhere else. <c>force=true</c> proceeds anyway,
    /// because *somewhere else on purpose* is a real intention: a restored replica, a renamed schema, a
    /// staging copy being promoted.
    /// </para>
    /// <para>
    /// <b>The datastore is not exempt.</b> Moving the hosted datastore is a legitimate operation and
    /// the same check protects it — pointing it at an empty database is exactly the mistake that would
    /// otherwise be discovered one query at a time.
    /// </para>
    /// </remarks>
    private static async Task UpdateDataSourceAsync(
        HttpContext context,
        Guid id,
        DataSourceRequest request,
        bool? force,
        IAdminCatalog catalog,
        IDataSourceProbe probe,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request?.ConnectionString))
        {
            await Refuse(
                context, 400,
                "connectionString is required. Send the whole connection, not the part that changed — "
                + "the stored one is sealed and this server does not read it back to merge into it.")
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<RegisteredDataSource> sources =
            await catalog.ListDataSourcesAsync(cancellation).ConfigureAwait(false);

        RegisteredDataSource? existing = null;

        foreach (RegisteredDataSource one in sources)
        {
            if (one.Id == id)
            {
                existing = one;
                break;
            }
        }

        if (existing is null)
        {
            await Refuse(context, 404, $"No data source '{id}'.").ConfigureAwait(false);
            return;
        }

        ProbeResult result =
            await probe.ProbeAsync(request.ConnectionString, cancellation).ConfigureAwait(false);

        if (result.Outcome == ProbeOutcome.CannotConnect)
        {
            await AuditAsync(
                context, audit, "datasource.update", existing.Value.Name,
                Detail(new { id, outcome = result.Outcome.ToString(), reason = result.Message }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, result.Message).ConfigureAwait(false);
            return;
        }

        // <b>What the layers on this source need, against what the new connection offers.</b> The
        // probe already lists the publishable tables there, so this is a set comparison rather than a
        // second round of queries.
        HashSet<string> offered = new(StringComparer.OrdinalIgnoreCase);

        foreach (SourceTable table in result.Tables)
        {
            offered.Add($"{table.SchemaName}.{table.TableName}");
        }

        List<string> missing = [];

        foreach (LayerTable needed in
                 await catalog.TablesOnAsync(id, cancellation).ConfigureAwait(false))
        {
            if (!offered.Contains(needed.Qualified))
            {
                missing.Add($"{needed.Layer} ({needed.Qualified})");
            }
        }

        if (missing.Count > 0 && force != true)
        {
            await AuditAsync(
                context, audit, "datasource.update", existing.Value.Name,
                Detail(new { id, outcome = "tablesMissing", missing = missing.Count }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(
                context, 409,
                $"The new connection reaches a database, and {Count(missing.Count, "layer")} on this "
                + "source would stop working there because their tables are not in it: "
                + $"{string.Join(", ", missing.Count > 6 ? [.. missing[..6], "…"] : missing)}. "
                + "The usual reason for this endpoint is the same database at a new address, where "
                + "nothing is missing — so this looks like the wrong database. Send force=true if it "
                + "is deliberate.").ConfigureAwait(false);
            return;
        }

        if (!await catalog.UpdateDataSourceAsync(
                id, request.Name, request.ConnectionString, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No data source '{id}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "datasource.update", request.Name ?? existing.Value.Name,
            Detail(new
            {
                id,
                summary = Summarise(request.ConnectionString),
                outcome = result.Outcome.ToString(),
                publishable = result.Tables.Count,
                missing = missing.Count,
                forced = missing.Count > 0,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            id,
            name = request.Name ?? existing.Value.Name,
            summary = Summarise(request.ConnectionString),
            publishable = result.Tables.Count,
            missing,
            note = "Layers on this source use the new connection from their next query. Pools are "
                + "per data source and cached by connection string, so the old pool is left to drain "
                + "and prune rather than being closed under a query that is still reading.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RegisterDataSourceAsync(
        HttpContext context,
        DataSourceRequest request,
        IAdminCatalog catalog,
        IDataSourceProbe probe,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            await Refuse(context, 400, "name and connectionString are required.").ConfigureAwait(false);
            return;
        }

        ProbeResult result =
            await probe.ProbeAsync(request.ConnectionString, cancellation).ConfigureAwait(false);

        // Probed before writing. ADR-017 §3.3's point is that registration
        // should not leave a broken row behind for somebody to find later — and
        // a source we cannot even connect to is broken by any reading.
        if (result.Outcome == ProbeOutcome.CannotConnect)
        {
            await AuditAsync(
                context, audit, "datasource.register", request.Name,
                Detail(new { outcome = result.Outcome.ToString(), reason = result.Message }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, result.Message).ConfigureAwait(false);
            return;
        }

        Guid id;

        try
        {
            id = await catalog
                .RegisterDataSourceAsync(request.Name, "postgis", request.ConnectionString, cancellation)
                .ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            await AuditAsync(
                context, audit, "datasource.register", request.Name,
                Detail(new { outcome = "duplicate" }), succeeded: false, cancellation)
                .ConfigureAwait(false);

            await Refuse(context, 409, $"A data source named '{request.Name}' already exists.")
                .ConfigureAwait(false);
            return;
        }

        // The detail records host and database and never the credential. An
        // audit log that leaks what it audits is a new place to steal from.
        await AuditAsync(
            context, audit, "datasource.register", request.Name,
            Detail(new
            {
                id,
                summary = Summarise(request.ConnectionString),
                outcome = result.Outcome.ToString(),
                publishable = result.Tables.Count,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(
            new
            {
                id,
                name = request.Name,
                probe = Describe(result),
            },
            statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ListDataSourcesAsync(
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<RegisteredDataSource> sources =
            await catalog.ListDataSourcesAsync(cancellation).ConfigureAwait(false);

        // <b>The host and database are in the listing now, and the credential still is not.</b>
        // `ListDataSourcesAsync` leaves `Summary` empty on purpose — it does not hold the protector —
        // with the note that decrypting every credential *to render a list would be a lot of key use
        // for a cosmetic column*. It stopped being cosmetic on 2026-08-19, when the connection string
        // became editable: an operator about to change one has to be able to see what it currently
        // points at, and a screen that shows only a name cannot tell `kurum-postgis` on the old host
        // from `kurum-postgis` on the new one. So this endpoint, which does hold the protector, opens
        // each one and reports `Summarise` — host, port and database, never the password.
        //
        // The cost is one AES-GCM open per source on a page an administrator opened deliberately.
        // At the 100–1,000 services CLAUDE.md §7 targets that is a handful of sources, not a
        // per-request cost, and it is the same decrypt `capability` already does for one.
        List<object> listed = [];

        foreach (RegisteredDataSource source in sources)
        {
            string? connection = null;

            try
            {
                connection = await catalog
                    .ConnectionStringOfAsync(source.Id, cancellation).ConfigureAwait(false);
            }
            catch (CryptographicException)
            {
                // <b>A source sealed with a key this build no longer holds is still a row worth
                // listing.</b> Failing the whole page would hide every working source behind one that
                // needs its secret re-entered — and *the summary is unavailable* is exactly the fact
                // an operator needs in order to know which one that is.
            }

            listed.Add(new
            {
                source.Id,
                source.Name,
                source.Kind,
                source.LayerCount,
                summary = connection is null ? null : Summarise(connection),
                sealedWithAnotherKey = connection is null,
            });
        }

        await Results.Json(new { dataSources = listed }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-017 §3.3 step 4: the honest list of what we could do with this
    /// source, given the rights actually granted.
    /// </summary>
    private static async Task CapabilityAsync(
        HttpContext context,
        Guid id,
        IAdminCatalog catalog,
        IDataSourceProbe probe,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        string? connectionString =
            await catalog.ConnectionStringOfAsync(id, cancellation).ConfigureAwait(false);

        if (connectionString is null)
        {
            await Refuse(context, 404, $"No data source '{id}'.").ConfigureAwait(false);
            return;
        }

        ProbeResult result = await probe.ProbeAsync(connectionString, cancellation).ConfigureAwait(false);

        await Results.Json(Describe(result)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the source's own database what it makes of the geometry to be published.
    /// </summary>
    /// <remarks>
    /// <b>Over the registered credential, not over the platform store's.</b> The table
    /// being published may live in somebody else's database entirely — that is what a
    /// registered source is — so the only connection that can see it is the one the
    /// operator registered. Reading it through the platform store's pool would answer
    /// about a table of the same name in the wrong place, which is a worse failure than
    /// not asking.
    /// </remarks>
    private static async Task<GeometryValidity?> ValidityOfSourceAsync(
        IAdminCatalog catalog, LayerPublication publication, CancellationToken cancellation)
    {
        try
        {
            if (await catalog.ConnectionStringOfAsync(publication.DataSourceId, cancellation)
                    .ConfigureAwait(false) is not { Length: > 0 } connection)
            {
                return null;
            }

            NpgsqlDataSourceBuilder builder = new(connection);

            // <b>Its own bound, because a validity scan is a sequential pass.</b> A
            // publish is interactive: somebody is waiting on this response, and a scan of
            // a hundred-million-row table is not worth the wait. Thirty seconds either
            // answers or reports itself as unmeasured, which is a real answer.
            builder.ConnectionStringBuilder.CommandTimeout = 30;

            await using NpgsqlDataSource source = builder.Build();

            return await GeometryValidity.MeasureAsync(
                source,
                publication.SchemaName,
                publication.TableName,
                publication.GeometryColumn,
                cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NpgsqlException or ArgumentException
            or InvalidOperationException or TimeoutException)
        {
            // Unmeasured, and the caller says so rather than guessing.
            return null;
        }
    }

    private static async Task PublishAsync(
        HttpContext context,
        PublishRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (!TryReadPublication(request, out LayerPublication? publication, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        // <b>A folder named here is created if it does not exist</b> — owner request — so it is
        // checked with the same rule `POST /admin/folders` applies. A publish that invented an
        // unaddressable folder would leave a service nobody can reach at a URL nobody can
        // type, and the reserved names would be silently duplicated in a different case.
        if (publication!.Folder is { Length: > 0 } named
            && !named.Equals(FeatureServerMetadataWriter.HostedFolder, StringComparison.OrdinalIgnoreCase)
            && !TryReadFolderName(named, out string? folderError))
        {
            await Refuse(context, 400, folderError!).ConfigureAwait(false);
            return;
        }

        // Publishing straight to public needs the privilege that puts data on
        // the internet, separately from the one that publishes at all.
        if (publication!.Sharing == SharingScope.Public
            && !await Authorize.RequireAsync(context, Privilege.SharingShareToPublic)
                .ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        // <b>D-53: ask PostGIS what it makes of the source before recording it.</b> Publish
        // recorded a table it never looked at, so `hosted.tr_ilce_511f6767` was serving 18
        // invalid geometries out of 25,280 and the way that came to light was another
        // server refusing to publish the same table. Asking is cheap to write and the
        // answer is worth having whichever way it comes back.
        //
        // <b>Before the write, because a refusal has to leave nothing behind.</b> If the
        // caller asked for validity and the source has none, no row is created — a
        // half-publish that has to be undone is the shape D-48 was about.
        //
        // <b>Never fails the publish by itself.</b> A source this server cannot scan — no
        // permission on the table, a timeout on a very large one — is still a source it
        // can serve, and answering 500 because a *report* could not be produced would
        // refuse work over a diagnostic. `null` means unmeasured and says so.
        GeometryValidity? validity = await ValidityOfSourceAsync(
            catalog, publication, cancellation).ConfigureAwait(false);

        if (request.RequireValidGeometry && validity is not null && !validity.AllValid)
        {
            await Refuse(
                context, 422,
                $"'{publication.SchemaName}.{publication.TableName}' was not published because "
                + $"requireValidGeometry was asked for and {validity.Explanation} Publish without "
                + "that flag to serve it as it is — the count is reported either way — or repair "
                + "the source. This server does not repair somebody else's data.")
                .ConfigureAwait(false);

            return;
        }

        if (request.RequireValidGeometry && validity is null)
        {
            await Refuse(
                context, 422,
                $"'{publication.SchemaName}.{publication.TableName}' was not published because "
                + "requireValidGeometry was asked for and the geometry could not be scanned — so "
                + "this server cannot tell you whether the condition holds. Check that the "
                + "registered credential can read the table.")
                .ConfigureAwait(false);

            return;
        }

        try
        {
            PublishedLayerAddress published = await catalog
                .PublishLayerAsync(publication, current.Principal.Id, cancellation)
                .ConfigureAwait(false);

            await AuditAsync(
                context, audit, "layer.publish", publication.Name,
                Detail(new
                {
                    id = published.Id,
                    service = published.ServiceName,
                    layer = published.LayerIndex,
                    table = $"{publication.SchemaName}.{publication.TableName}",
                    sharing = PostgresSharing(publication.Sharing),
                    arcGisServable = publication.ObjectIdColumn is not null,
                    invalidGeometries = validity?.Invalid,
                }),
                succeeded: true, cancellation).ConfigureAwait(false);

            await Results.Json(
                new
                {
                    id = published.Id,
                    name = publication.Name,

                    // Where it actually is, which is no longer derivable from
                    // its name: a layer added to an existing service is an index
                    // inside that service.
                    service = published.ServiceName,
                    layerId = published.LayerIndex,

                    // <b>Where it actually is, which may not be where it was asked to go.</b>
                    // Hosted data lands in `hosted` whatever the request said (owner rule
                    // 2026-08-17), so a caller building a URL from the service name alone would
                    // get a 404 and read it as a failed publish.
                    folder = published.Folder,
                    url = published.Folder is { Length: > 0 } inFolder
                        ? $"/rest/services/{inFolder}/{published.ServiceName}/FeatureServer/{published.LayerIndex}"
                        : $"/rest/services/{published.ServiceName}/FeatureServer/{published.LayerIndex}",
                    sharing = PostgresSharing(publication.Sharing),
                    arcGisServable = publication.ObjectIdColumn is not null,

                    // <b>What PostGIS makes of the source (D-53).</b> Reported on every
                    // publish, including the ones that did not ask, because the whole
                    // defect was that nobody was told.
                    geometry = validity is null
                        ? new
                        {
                            valid = (bool?)null,
                            invalid = (long?)null,
                            reasons = Array.Empty<string>(),
                            note = "The geometry could not be scanned, so this says nothing "
                                 + "about it. The layer is published.",
                        }
                        : new
                        {
                            valid = (bool?)validity.AllValid,
                            invalid = (long?)validity.Invalid,
                            reasons = validity.Reasons.ToArray(),
                            note = validity.Explanation,
                        },

                    // ADR-013 §2a, said at publish time rather than discovered
                    // at the first query by somebody who cannot fix it.
                    note = publication.ObjectIdColumn is null
                        ? "No integer object-id column was given, so this layer is not servable "
                          + "through the ArcGIS surface. It remains servable natively."
                        : null,
                },
                statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState is "23505" or "23503" or "23514")
        {
            await AuditAsync(
                context, audit, "layer.publish", publication.Name,
                Detail(new { sqlState = e.SqlState }), succeeded: false, cancellation)
                .ConfigureAwait(false);

            await Refuse(context, 409, Conflict(e, publication)).ConfigureAwait(false);
        }
    }

    private static async Task ListLayersAsync(
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminViewAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<AdminLayer> layers =
            await catalog.ListLayersAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            layers = layers.Select(l => new
            {
                l.Id,
                l.Name,
                dataSource = l.DataSourceName,
                table = l.Qualified,
                sharing = PostgresSharing(l.Sharing),
                status = Wire(l.Status),
                owner = l.OwnerName,
                l.ArcGisServable,

                // So the console can offer tiles only where they exist. Showing
                // the control everywhere and letting it 400 teaches people that
                // the button sometimes does not work, which is worse than not
                // having it.
                l.Hosted,

                // <b>Where it is, which ADR-020 §2 asked for and this listing did not give.</b>
                // A caller that has to work out a layer's URL from its name walks the services
                // directory, and a stopped service is absent from that walk — see AdminLayer's
                // own remark for the three defects that came of it.
                l.Service,
                l.Folder,
                l.LayerIndex,
                url = l.Address,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <remarks>
    /// <para>
    /// <b>This does not purge the tile cache, and ADR-010 §5.1 says it should.</b>
    /// That row — *permissions changed → purge, and this one is a security
    /// matter* — was written before §4 settled how tile keys work, and for tiles
    /// the two do not agree. §4's rule is that authorization for a tile is
    /// <em>uniform</em>: a layer is readable or it is not, the check runs
    /// <em>before</em> the cache lookup, and every authorized caller shares one
    /// entry. So a sharing change cannot make a single cached byte wrong. It
    /// changes who reaches the cache, not what the cache holds.
    /// </para>
    /// <para>
    /// Purging anyway would throw away a whole seeded pyramid every time
    /// somebody moved a layer from organisation to public — the most ordinary
    /// administrative act there is — for no correctness gain at all.
    /// </para>
    /// <para>
    /// <b>What would change this:</b> row-level or field-level filtering, where
    /// the effective grant alters the output. §4 already answers that case with
    /// a grant fingerprint in the key, which makes the invalidation structural
    /// rather than a sweep. Neither exists yet. When one does, this comment is
    /// wrong and the fingerprint is the fix, not a purge here.
    /// </para>
    /// </remarks>
    private static async Task SetSharingAsync(
        HttpContext context,
        string name,
        SharingRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!TryReadScope(request.Sharing, out SharingScope scope, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        Privilege needed = scope == SharingScope.Public
            ? Privilege.SharingShareToPublic
            : Privilege.SharingShareToOrganization;

        if (!await Authorize.RequireAsync(context, needed).ConfigureAwait(false))
        {
            return;
        }

        // Read first, so the audit record can say what it was as well as what it
        // became. ADR-017 §5d asks for before and after, and after alone answers
        // "what is it now" — which anybody can see — rather than "what changed".
        AdminLayer? before = (await catalog.ListLayersAsync(cancellation).ConfigureAwait(false))
            .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal)) is
            { Name: not null } found ? found : null;

        AdminLayer? after = await catalog
            .SetSharingAsync(name, scope, cancellation).ConfigureAwait(false);

        if (after is null)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "layer.share", name,
            Detail(new
            {
                from = before is { } b ? PostgresSharing(b.Sharing) : null,
                to = PostgresSharing(scope),
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            from = before is { } was ? PostgresSharing(was.Sharing) : null,
            to = PostgresSharing(scope),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Everybody with an account, and what they hold.</summary>
    /// <remarks>
    /// <b><c>admin:manageMembers</c>, and reading is the same privilege as writing here.</b> A
    /// list of accounts is not neutral: it is the shape of an organisation and a target list for
    /// somebody guessing passwords. ADR-018 has no separate *view members* privilege, and
    /// inventing one to make the listing softer would be a privilege whose only purpose is to be
    /// granted by mistake.
    /// </remarks>
    private static async Task ListMembersAsync(
        HttpContext context,
        IMemberDirectory directory,
        IRoleDirectory roles,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
            .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<Member> members =
            await directory.ListMembersAsync(cancellation).ConfigureAwait(false);

        IReadOnlyList<RoleGrant> defined = await roles.ListAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            members = members.Select(m => new
            {
                m.Name,
                displayName = m.DisplayName,
                m.Roles,
                userType = m.UserType,
                disabled = m.IsDisabled,
                createdAt = m.CreatedAt,

                // Why there is no delete on this surface, said per row rather than in a paragraph
                // somebody has to find.
                ownsServices = m.OwnsServices,
            }),
            // <b>Every role, not only the five built-in ones — corrected 2026-08-18.</b> A
            // deployment can define its own roles now (ADR-035), and a picker listing `Roles.All`
            // would offer five choices on a server that has seven.
            roles = defined.Select(r => r.Name),
            userTypes = UserTypes.All,

            // <b>What each role can do, from the store rather than from the compiled table.</b> A
            // console drawing a role picker needs to say what the choice means, and the alternative
            // is a copy of ADR-018 §2a in JavaScript — the copy nobody updates.
            //
            // <b>This read `Roles.PrivilegesOf` until 2026-08-18, and from the moment role grants
            // became editable that was a lie.</b> It happened to agree with the store because
            // nothing had edited a role yet, which is the worst kind of agreement: it survives every
            // test and breaks the first time the feature is used. Found while measuring ADR-035 —
            // the screen reported `user` without a privilege the store had just been given.
            grants = defined.ToDictionary(
                r => r.Name,
                r => r.Privileges.Select(Roles.NameOf).OrderBy(p => p, StringComparer.Ordinal)),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Creates a member with a role and a first password.</summary>
    /// <remarks>
    /// <para>
    /// <b>The endpoint <see href="../../docs/architecture-debt.md">D-56</see> asked for.</b> Until
    /// this existed a deployment had one account for ever, so
    /// <see href="../../docs/adr/ADR-034-server-and-studio.md">ADR-034</see>'s Studio had no
    /// possible occupant and its condition 1 — *no screen appears that its reader cannot use* —
    /// could not be tested, because the test needs somebody without <c>admin:manageServer</c>.
    /// </para>
    /// <para>
    /// <b>The password is set here rather than invited.</b> An invitation flow needs mail, a token
    /// table and an expiry policy, and this server has no way to send a message — so the
    /// alternative to an administrator typing a first password is no member at all. Recorded as the
    /// compromise it is: the password crosses the wire, which is why every write on this surface
    /// requires HTTPS, and the response does not echo it back.
    /// </para>
    /// <para>
    /// <b>The role is required and there is no default.</b> A member created with no role holds
    /// nothing and reads as broken; a member defaulted to <c>publisher</c> is an authorization
    /// decision made by whoever wrote the default. Naming it is one field.
    /// </para>
    /// </remarks>
    private static async Task CreateMemberAsync(
        HttpContext context,
        MemberRequest request,
        IMemberDirectory directory,
        IRoleDirectory roles,
        IPasswordHasher hasher,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
            .ConfigureAwait(false))
        {
            return;
        }

        string name = (request.Name ?? "").Trim();

        if (name.Length == 0)
        {
            await Refuse(context, 400, "A member needs a name to sign in with.")
                .ConfigureAwait(false);
            return;
        }

        string role = (request.Role ?? "").Trim();

        // <b>Every role the deployment has, not the five this build ships with.</b> The second place
        // this was wrong: `Roles.All` here meant a role a deployment had just defined could not be
        // given to a new member either — so the feature was half-built in two handlers, and they
        // failed differently enough that fixing one looked like fixing it.
        IReadOnlyList<RoleGrant> defined = await roles.ListAsync(cancellation).ConfigureAwait(false);

        if (!defined.Any(r => string.Equals(r.Name, role, StringComparison.Ordinal)))
        {
            await Refuse(
                context, 400,
                $"'{role}' is not a role on this server. It has "
                + $"{string.Join(", ", defined.Select(r => r.Name))}, and one is required: a member "
                + "with none holds nothing, and a default would be an authorization decision made by "
                + "whoever wrote it.").ConfigureAwait(false);
            return;
        }

        // <b>ADR-035 §4g: creating an administrator is the administrator's act too.</b> Not in that
        // section's list, and it follows from it — a role that could create a member and hand them the
        // administrator role would reach it in two calls instead of one, which is the same escalation
        // with an extra step.
        if (string.Equals(role, Roles.Administrator, StringComparison.Ordinal)
            && !await AdministratorOnlyAsync(context, "Creating an administrator")
                .ConfigureAwait(false))
        {
            return;
        }

        string userType = (request.UserType ?? UserTypes.Unrestricted).Trim();

        if (!UserTypes.All.Contains(userType, StringComparer.Ordinal))
        {
            await Refuse(
                context, 400,
                $"'{userType}' is not a user type. They are {string.Join(", ", UserTypes.All)}, and "
                + "a type is a ceiling: it caps whatever the role grants (ADR-018 §3).")
                .ConfigureAwait(false);
            return;
        }

        // <b>The server chooses it.</b> Owner rule 2026-08-17: an administrator does not get to
        // pick somebody else's password. It is dirty from the moment it exists — the store sets
        // must_change on every credential it writes — so this is a one-use secret rather than an
        // account password that happens to be known to two people.
        string password = IssuedPassword.Issue();

        Principal? created = await directory.CreateMemberAsync(
            name,
            string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            hasher.Hash(password),
            role,
            userType,
            cancellation).ConfigureAwait(false);

        if (created is null)
        {
            await Refuse(context, 409, $"There is already a member called '{name}'.")
                .ConfigureAwait(false);
            return;
        }

        // <b>Audited without the password and without a hash of it.</b> An audit row is read by
        // more people than the credential table and lives longer.
        await AuditAsync(
            context, audit, "member.create", name,
            Detail(new { role, userType }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status201Created;

        await Results.Json(new
        {
            name = created.Name,
            role,
            userType,
            // <b>Returned once, and this is the only time it exists in the clear.</b> It is not
            // stored — only its Argon2id hash is — so an administrator who loses it issues another
            // rather than looking it up.
            password,
            mustChange = true,
            note = $"Give this password to '{name}'. It works once: the server marks it as needing "
                 + "replacement, so their first act after signing in has to be setting their own — "
                 + "nothing else answers until they do. It is not stored in the clear and is not "
                 + "shown again; if it is lost, issue another.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Replaces the role a member holds.</summary>
    /// <remarks>
    /// <b>It refuses to remove the last administrator, and that refusal is the whole reason this
    /// is not one <c>update</c>.</b> A server whose only administrator has been demoted cannot be
    /// administered by anybody, and there is no recovery path short of SQL against the platform
    /// store — the same class of unrecoverable state the first-run setup is careful about at the
    /// other end of the account's life.
    /// </remarks>
    private static async Task SetMemberRoleAsync(
        HttpContext context,
        string name,
        MemberRoleRequest request,
        IMemberDirectory directory,
        IIdentityStore identity,
        IRoleDirectory roles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roles);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
            .ConfigureAwait(false))
        {
            return;
        }

        string? role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim();

        // <b>Every role the deployment has, not the five this build ships with.</b> This read
        // `Roles.All`, so from the moment ADR-035 let a deployment define a role, it could define one
        // and not assign it — the feature was half-built and the refusal said *"'x' is not a role"*
        // about a role that plainly was. D-74's pattern for the fourth time in one day: a reader of a
        // set that stopped being fixed.
        if (role is not null)
        {
            IReadOnlyList<RoleGrant> defined =
                await roles.ListAsync(cancellation).ConfigureAwait(false);

            if (!defined.Any(r => string.Equals(r.Name, role, StringComparison.Ordinal)))
            {
                await Refuse(context, 400,
                    $"'{role}' is not a role on this server. GET /admin/roles lists them.")
                    .ConfigureAwait(false);
                return;
            }
        }

        // <b>ADR-035 §4g: to or from administrator is the administrator's act.</b> Both directions —
        // granting it is the escalation, and removing it is how somebody would clear the way.
        bool touchesAdministrator =
            string.Equals(role, Roles.Administrator, StringComparison.Ordinal)
            || await HoldsAdministratorAsync(directory, name, cancellation).ConfigureAwait(false);

        if (touchesAdministrator
            && !await AdministratorOnlyAsync(
                context, "Changing a role to or from administrator").ConfigureAwait(false))
        {
            return;
        }

        if (role != Roles.Administrator
            && !await SomebodyElseAdministersAsync(directory, name, cancellation)
                .ConfigureAwait(false))
        {
            await Refuse(
                context, 409,
                $"'{name}' is the only administrator. Taking that role away would leave a server "
                + "nobody can administer, and there is no way back except SQL against the platform "
                + "store. Make somebody else an administrator first.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<string>? before =
            await directory.SetRoleAsync(name, role, cancellation).ConfigureAwait(false);

        if (before is null)
        {
            await Refuse(context, 404, $"There is no member called '{name}'.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "member.role", name,
            Detail(new { from = before, to = role }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            from = before,
            to = role,

            // <b>Said because it is not obvious and it is a security answer.</b> Sessions carry a
            // principal, not a privilege set, and the privileges are resolved per request — so a
            // demotion takes effect on the member's next request rather than on their next sign-in.
            note = "Their existing sessions are unaffected as sessions and immediately affected as "
                 + "privileges: what a role grants is resolved on every request, not stamped into "
                 + "the token.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Whether an administrator other than this member exists.</summary>
    /// <remarks>
    /// Asked of the listing rather than with a count query, because the listing already reports
    /// roles and a second SQL path for the same fact is a second place to get it wrong.
    /// </remarks>
    /// <summary>Whether a named member holds the administrator role.</summary>
    /// <remarks>
    /// <b>From the listing, like its neighbour, and for the same reason.</b> `IIdentityStore` answers
    /// by principal id and every caller here has a name; the member listing is one statement and is
    /// already read on this path to count administrators.
    /// </remarks>
    private static async Task<bool> HoldsAdministratorAsync(
        IMemberDirectory directory, string name, CancellationToken cancellation) =>
        (await directory.ListMembersAsync(cancellation).ConfigureAwait(false))
            .Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                && m.Roles.Contains(Roles.Administrator, StringComparer.Ordinal));

    /// <summary>Why the last administrator cannot be removed.</summary>
    /// <remarks>
    /// <b>One sentence, two callers.</b> [D-101](../../docs/architecture-debt.md) added a check
    /// before the dispositions run, and the transactional one after them stayed; two spellings of
    /// the same refusal is the recurring debt D-46 names, and here it would mean the same request
    /// explaining itself differently depending on which of the two caught it.
    /// </remarks>
    /// <param name="name">The member.</param>
    /// <returns>The refusal.</returns>
    private static string OnlyAdministrator(string name) =>
        $"'{name}' is the only administrator who can still sign in. A server with no "
        + "administrator cannot be recovered without editing the database by hand, so "
        + "this is refused whichever disposition was asked for. Make another "
        + "administrator first.";

    private static async Task<bool> SomebodyElseAdministersAsync(
        IMemberDirectory directory, string name, CancellationToken cancellation) =>
        (await directory.ListMembersAsync(cancellation).ConfigureAwait(false))
            .Any(m => !string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                && !m.IsDisabled
                && m.Roles.Contains(Roles.Administrator, StringComparer.Ordinal));

    /// <summary>Disables or re-enables a member.</summary>
    /// <remarks>
    /// <para>
    /// <b>There is no delete, and the listing's <c>ownsServices</c> is why.</b> A member owns
    /// content: removing the row would orphan every service whose owner points at it, and the
    /// sharing evaluator reads that column to decide who may read what. Disabling stops the
    /// sign-in and leaves the ownership standing, which is the reversible half of the same intent.
    /// </para>
    /// <para>
    /// <b>Their sessions are revoked, which disabling alone would not do.</b> A disabled account
    /// with a live session is an account that keeps working until the session expires — the
    /// opposite of what an administrator revoking access believes they have done.
    /// </para>
    /// </remarks>
    private static async Task SetMemberDisabledAsync(
        HttpContext context,
        string name,
        bool disabled,
        IMemberDirectory directory,
        IIdentityStore identity,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
            .ConfigureAwait(false))
        {
            return;
        }

        if (disabled
            && !await SomebodyElseAdministersAsync(directory, name, cancellation)
                .ConfigureAwait(false))
        {
            await Refuse(
                context, 409,
                $"'{name}' is the only administrator, so disabling them would leave a server "
                + "nobody can administer.").ConfigureAwait(false);
            return;
        }

        bool? was = await directory
            .SetDisabledAsync(name, disabled, cancellation).ConfigureAwait(false);

        if (was is null)
        {
            await Refuse(context, 404, $"There is no member called '{name}'.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, disabled ? "member.disable" : "member.enable", name,
            Detail(new { wasDisabled = was }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            wasDisabled = was,
            disabled,
            note = disabled
                ? "They cannot sign in, and what they own is untouched — which is why there is no "
                  + "delete here: removing the row would orphan every service that names them as "
                  + "its owner."
                : "They can sign in again with the password they had.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// What a member owns, so an operator can decide before removing them.
    /// </summary>
    /// <remarks>
    /// <b>A read before a destructive write, the same shape as the empty-service sweep.</b>
    /// ADR-015 §6c puts the judgement with the operator, and a judgement needs the names — *3
    /// services* does not say whether transferring them is right.
    /// </remarks>
    private static async Task GetMemberHoldingsAsync(
        HttpContext context,
        string name,
        IMemberDirectory members,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await members.HoldingsOfAsync(name, cancellation).ConfigureAwait(false)
            is not { } holdings)
        {
            await Refuse(context, 404, $"No member '{name}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            name,
            services = holdings.Services,
            folders = holdings.Folders,
            groups = holdings.Groups,
            owns = holdings.Any,
            note = holdings.Any
                ? $"This member owns {holdings.Explanation}. Removing them needs a decision: "
                    + "transfer these to another member, and nothing stops serving; or delete them, "
                    + "which unpublishes the layers and removes the services and folders with the "
                    + "account."
                : "This member owns nothing, so removing them takes nothing with it.",

            // <b>Named even at zero.</b> Groups are ADR-018's deferred sharing scope and have no
            // table yet; a caller that reads this field today keeps working on the day they arrive,
            // which is the whole reason the owner asked for them to be in the decision.
            groupsNote = "Groups do not exist yet. When they do, they are a third owned thing and "
                + "both dispositions cover them without a new shape here.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves everything one member owns to another, without removing anybody.
    /// </summary>
    /// <remarks>
    /// <b>Separate from the removal because it is a separate intent.</b> Somebody changing teams
    /// hands over their content and keeps their account; folding that into the delete would make
    /// the only way to reassign content the destruction of an account.
    /// </remarks>
    private static async Task TransferMemberContentAsync(
        HttpContext context,
        string name,
        TransferRequest request,
        IMemberDirectory members,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
                .ConfigureAwait(false))
        {
            return;
        }

        if (request?.To is not { Length: > 0 } receiver)
        {
            await Refuse(context, 400, "'to' names the member who receives the content.")
                .ConfigureAwait(false);

            return;
        }

        if (string.Equals(name, receiver, StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(context, 400, "A member cannot transfer their content to themselves.")
                .ConfigureAwait(false);

            return;
        }

        if (await members.HoldingsOfAsync(name, cancellation).ConfigureAwait(false) is null)
        {
            await Refuse(context, 404, $"No member '{name}'.").ConfigureAwait(false);
            return;
        }

        if (await members.HoldingsOfAsync(receiver, cancellation).ConfigureAwait(false) is null)
        {
            await Refuse(context, 404, $"No member '{receiver}' to transfer to.")
                .ConfigureAwait(false);

            return;
        }

        int moved = await members.TransferOwnershipAsync(name, receiver, cancellation)
            .ConfigureAwait(false);

        await AuditAsync(
            context, audit, "member.transfer", name,
            Detail(new { to = receiver, moved }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new
        {
            from = name,
            to = receiver,
            moved,
            note = moved == 0
                ? $"'{name}' owned nothing, so nothing moved."
                : $"{moved} thing(s) now belong to '{receiver}'. Nothing was unpublished and every "
                    + "URL a client holds still works.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a member, disposing of what they own the way the caller said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-015 §6c.</b> No disposition and nothing owned: removed. No disposition and something
    /// owned: **409 naming what is attached and the two choices** — the server does not pick.
    /// <c>?transferTo=x</c> moves it; <c>?deleteOwned=true</c> takes it along.
    /// </para>
    /// <para>
    /// <b>The delete disposition goes through the unpublish path, not through SQL.</b> That path
    /// purges a layer's tiles (a republished layer would otherwise serve the previous one's
    /// pyramid) and it is the only place that knows to. Reaching for `delete from layer` here would
    /// be the same behaviour in a second place, which is what D-46 is about.
    /// </para>
    /// </remarks>
    private static async Task RemoveMemberAsync(
        HttpContext context,
        string name,
        IMemberDirectory members,
        IAdminCatalog catalog,
        PostgresLayerCatalog layers,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
                .ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (string.Equals(current.Principal.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(
                context, 409,
                "A member cannot remove themselves: the session doing the work would be revoked "
                + "halfway through it. Ask another administrator.").ConfigureAwait(false);

            return;
        }

        bool administers = await HoldsAdministratorAsync(members, name, cancellation)
            .ConfigureAwait(false);

        // <b>ADR-035 §4g: removing an administrator is the administrator's act.</b> Before the
        // last-administrator refusal, because that one is about the *server's* recoverability and
        // this one is about who may reach the role at all — a role granted `admin:manageMembers`
        // could otherwise delete every administrator but one and then take the role for itself.
        if (administers
            && !await AdministratorOnlyAsync(context, "Removing an administrator")
                .ConfigureAwait(false))
        {
            return;
        }

        if (await members.HoldingsOfAsync(name, cancellation).ConfigureAwait(false)
            is not { } holdings)
        {
            await Refuse(context, 404, $"No member '{name}'.").ConfigureAwait(false);
            return;
        }

        /*
          <b>Asked here, before anything is destroyed, and that is the whole of
          [D-101](../../docs/architecture-debt.md).</b> The refusal below is real and
          transactional — `PostgresMemberDirectory.CheckAsync` runs inside the removal's own
          transaction, so a *transfer* that meets it rolls back and moves nothing. The
          `deleteOwned` branch is not transactional and could not be: it unpublishes layers,
          purges tiles and deletes services and folders through the paths that own those side
          effects, each committing as it goes, and only then asks to remove the member. Refused
          at that point, the layers are already gone and the answer is *this was refused
          whichever disposition was asked for*.

          <b>So the same question is asked first, from what the member listing already knows.</b>
          It costs one read of a list this handler has read anyway, and it means no disposition
          runs on a removal that cannot succeed.

          <b>The check below stays, and is not redundant.</b> This one races: another
          administrator can be disabled between here and there. That is what a transaction and a
          row lock are for, and what this is not.
        */
        if (administers
            && !await SomebodyElseAdministersAsync(members, name, cancellation)
                .ConfigureAwait(false))
        {
            await Refuse(context, 409, OnlyAdministrator(name)).ConfigureAwait(false);
            return;
        }

        string? transferTo = context.Request.Query["transferTo"].FirstOrDefault();

        bool deleteOwned = string.Equals(
            context.Request.Query["deleteOwned"].FirstOrDefault(),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (holdings.Any && transferTo is not { Length: > 0 } && !deleteOwned)
        {
            await Refuse(
                context, 409,
                $"'{name}' owns {holdings.Explanation}, so this request did not say enough to be "
                + "carried out. Either transfer it — DELETE with ?transferTo=<member>, and nothing "
                + "stops serving — or take it along, with ?deleteOwned=true, which unpublishes the "
                + "layers and removes the services and folders.").ConfigureAwait(false);

            return;
        }

        MemberRemoval outcome;
        int unpublished = 0;

        if (transferTo is { Length: > 0 } receiver)
        {
            if (string.Equals(name, receiver, StringComparison.OrdinalIgnoreCase))
            {
                await Refuse(context, 400, "A member cannot transfer their content to themselves.")
                    .ConfigureAwait(false);

                return;
            }

            outcome = await members.TransferAndRemoveAsync(name, receiver, cancellation)
                .ConfigureAwait(false);
        }
        else if (deleteOwned && holdings.Any)
        {
            // <b>Layers first, then services, then folders.</b> `layer.service_id` is `no action`,
            // so a service still holding a layer refuses to go — which is the same refusal
            // DeleteServiceAsync reports as Occupied, and the reason the order is not arbitrary.
            foreach (string qualified in holdings.Services)
            {
                (string? folder, string bare) = SplitQualified(qualified);

                if (await layers.FindServiceAsync(folder, bare, cancellation)
                        .ConfigureAwait(false) is { } service)
                {
                    foreach (PublishedLayer held in service.Layers)
                    {
                        if (await catalog.UnpublishLayerAsync(held.Definition.Name, cancellation)
                                .ConfigureAwait(false))
                        {
                            tiles.Purge(held.Id);
                            unpublished++;
                        }
                    }
                }

                await catalog.DeleteServiceAsync(bare, folder, cancellation).ConfigureAwait(false);
            }

            foreach (string folder in holdings.Folders)
            {
                await catalog.DeleteFolderAsync(folder, cancellation).ConfigureAwait(false);
            }

            outcome = await members.RemoveAsync(name, cancellation).ConfigureAwait(false);
        }
        else
        {
            outcome = await members.RemoveAsync(name, cancellation).ConfigureAwait(false);
        }

        if (outcome != MemberRemoval.Removed)
        {
            (int code, string why) = outcome switch
            {
                MemberRemoval.Absent => (404, $"No member '{name}'."),

                MemberRemoval.LastAdministrator => (409, OnlyAdministrator(name)),

                MemberRemoval.TargetAbsent => (
                    404, $"There is no member '{transferTo}' to transfer to. Nothing was changed."),

                MemberRemoval.TargetDisabled => (
                    409,
                    $"'{transferTo}' is disabled, so nobody could administer what was moved to "
                    + "them. Enable them first, or choose another member. Nothing was changed."),

                _ => (
                    409,
                    $"'{name}' still owns something, so the account was kept rather than left "
                    + "pointing at nothing. This is a fault in the removal itself — see D-66."),
            };

            await Refuse(context, code, why).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "member.remove", name,
            Detail(new
            {
                transferredTo = transferTo,
                deletedOwned = deleteOwned,
                services = holdings.Services.Count,
                folders = holdings.Folders.Count,
                unpublished,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            removed = name,
            transferredTo = transferTo,
            unpublished,
            note = transferTo is { Length: > 0 }
                ? $"'{name}' is gone and everything they owned belongs to '{transferTo}'. Nothing "
                    + "was unpublished."
                : holdings.Any
                    ? $"'{name}' is gone, with {unpublished} layer(s) unpublished and "
                        + $"{holdings.Services.Count} service(s) and {holdings.Folders.Count} "
                        + "folder(s) removed."
                    : $"'{name}' is gone. They owned nothing.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Splits a qualified service name into its folder and its name.</summary>
    private static (string? Folder, string Name) SplitQualified(string qualified)
    {
        int slash = qualified.LastIndexOf('/');

        return slash < 0
            ? (null, qualified)
            : (qualified[..slash], qualified[(slash + 1)..]);
    }

    /// <summary>Sets a member's password without knowing the old one.</summary>
    /// <remarks>
    /// <b>A stronger act than it looks, and separate from the self-service change for that
    /// reason.</b> <c>PUT /rest/auth/password</c> requires the current password; this cannot,
    /// because an administrator resetting a forgotten one does not know it. So it hands somebody a
    /// working credential for an account that owns content, and it is audited under its own name.
    /// </remarks>
    private static async Task SetMemberPasswordAsync(
        HttpContext context,
        string name,
        IMemberDirectory directory,
        IPasswordHasher hasher,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers)
            .ConfigureAwait(false))
        {
            return;
        }

        // <b>ADR-035 §4g: resetting an administrator's password is the administrator's act.</b> It is
        // the quietest of the three — no role changes, nothing deleted — and it is the one that hands
        // somebody an administrator's session outright, because the issued password comes back in the
        // response.
        if (await HoldsAdministratorAsync(directory, name, cancellation).ConfigureAwait(false)
            && !await AdministratorOnlyAsync(
                context, "Resetting an administrator's password").ConfigureAwait(false))
        {
            return;
        }

        // Issued, not accepted — see CreateMemberAsync. There is no body on this request at all,
        // which is the clearest statement available that the caller does not choose it.
        string password = IssuedPassword.Issue();

        if (!await directory.SetPasswordAsync(name, hasher.Hash(password), cancellation)
            .ConfigureAwait(false))
        {
            await Refuse(context, 404, $"There is no member called '{name}'.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "member.password", name, Detail(new { reset = true }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            password,
            mustChange = true,

            // <b>Two facts an administrator would otherwise assume, one of them wrongly.</b> Their
            // old sessions keep working, because a session is not a credential; and the new
            // password reaches nothing but its own replacement, so *keeps working* does not mean
            // *keeps working with this*.
            note = $"Give this password to '{name}'. It needs replacing on first use, so it will "
                 + "not do anything except set their own. Their existing sessions are untouched — a "
                 + "session is not a credential, and revoking them is a separate act.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What one operation on a service with no layers may spend.</summary>
    /// <remarks>
    /// <b>Reports the effective value and the default beside it.</b> An administrator looking at
    /// this needs to know three things and they are three different facts: what is stored (null
    /// means nobody has said), what the server would use if nothing were stored, and therefore what
    /// is in force right now. Reporting only the last of those is how a screen ends up unable to
    /// tell *set to ten* from *defaulting to ten*, which is the state the three-way rule exists to
    /// keep visible.
    /// </remarks>
    private static async Task GetSystemLimitsAsync(
        HttpContext context,
        string name,
        PostgresSystemServices services,
        IGeometryEngine engine,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        SystemService? found = await services.FindAsync(name, cancellation).ConfigureAwait(false);

        if (found is not { } service)
        {
            await Refuse(context, 404, $"No system service '{name}'.").ConfigureAwait(false);
            return;
        }

        GeometryWorkerPool? pool = engine as GeometryWorkerPool;

        double defaultDeadline =
            (pool?.EnforcedDeadline ?? GeometryWorkerPool.Deadline).TotalSeconds;

        long defaultPreflight =
            pool?.EnforcedPreflightPairs ?? GeometryWorkerPool.MaximumCandidatePairs;

        double defaultWait = (pool?.EnforcedWait ?? GeometryWorkerPool.Deadline).TotalSeconds;

        // <b>Read from the pool rather than from the setting, because the request path may have
        // pushed a stored value into it.</b> Reporting the configured number while the pool holds
        // another would be the fault this endpoint exists to remove, one field along.
        double defaultIdle =
            (pool?.IdleBudget ?? GeometryWorkerPool.DefaultIdleBudget).TotalSeconds;

        await Results.Json(new
        {
            name = service.Name,
            kind = service.Kind,

            // What is stored: null means nobody has said.
            deadlineSeconds = service.DeadlineSeconds,
            preflightPairs = service.PreflightPairs,
            waitSeconds = service.WaitSeconds,
            idleSeconds = service.IdleSeconds,

            // What the server would use if nothing were stored — from configuration, not from a
            // constant, so this answer moves when Graticula:OverlayDeadlineSeconds does.
            defaultDeadlineSeconds = defaultDeadline,
            defaultPreflightPairs = defaultPreflight,
            defaultWaitSeconds = defaultWait,
            defaultIdleSeconds = defaultIdle,

            // And therefore what is in force, so a caller does not have to do the arithmetic and
            // get it subtly different from the request path.
            effectiveDeadlineSeconds = service.DeadlineSeconds ?? defaultDeadline,
            effectivePreflightPairs = service.PreflightPairs ?? defaultPreflight,
            effectiveWaitSeconds = service.WaitSeconds ?? defaultWait,
            effectiveIdleSeconds = service.IdleSeconds ?? defaultIdle,

            // <b>Two bounds that are not settings, said here rather than left to be discovered
            // by an administrator who assumed this endpoint covered everything.</b>
            maximumVertices = GeometryServerEndpoints.MaximumVertices,
            workers = settings.OverlayWorkers,
            note = "maximumVertices is fixed: every operation on this surface is one pass over "
                 + "the coordinates, so input size bounds the work exactly and a cap is the right "
                 + "mechanism rather than a preference. The 1 GB worker heap ceiling is fixed for "
                 + "the same reason — total exposure is `workers` times that ceiling. `workers` is "
                 + "one number rather than a minimum and a maximum, so this pool does not grow or "
                 + "shrink: elastic pooling needs a concrete problem before it earns the machinery "
                 + "(§82). The four budgets above are per service; `workers` is per server, from "
                 + "Graticula:OverlayWorkers. ADR-022 §3.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Sets what one operation on a service with no layers may spend.</summary>
    /// <remarks>
    /// <para>
    /// <b>Null clears, which is not the same as omitting.</b> An administrator who wants the
    /// server's default back says <c>null</c> and gets it, rather than looking the default up and
    /// typing a copy that stops tracking it. The three-way rule, the same one a layer's cache TTL
    /// follows.
    /// </para>
    /// <para>
    /// <b>Refused rather than clamped.</b> A deadline of zero, or of a day, is a mistake worth
    /// saying out loud: silently clamping it would leave an administrator believing a number the
    /// server is not using, which is the exact fault this endpoint was written to remove.
    /// </para>
    /// </remarks>
    private static async Task SetSystemLimitsAsync(
        HttpContext context,
        string name,
        SystemLimitsRequest request,
        PostgresSystemServices services,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        if (request.DeadlineSeconds is { } seconds && (seconds < 1 || seconds > 3600))
        {
            await Refuse(
                context, 400,
                $"A deadline of {seconds} seconds is outside 1..3600. Zero would refuse every "
                + "request and an hour is longer than any client will wait — if the intent is no "
                + "deadline at all, there is no such setting, because the deadline is what keeps "
                + "an adversarial input from taking the host down (ADR-022 §2b).")
                .ConfigureAwait(false);
            return;
        }

        if (request.PreflightPairs is { } pairs && pairs < 0)
        {
            await Refuse(
                context, 400,
                "A negative pre-flight threshold has no meaning. Zero is how the pre-flight is "
                + "turned off, and null restores the server's default.").ConfigureAwait(false);
            return;
        }

        if (request.WaitSeconds is { } waiting && (waiting < 1 || waiting > 3600))
        {
            await Refuse(
                context, 400,
                $"A queue wait of {waiting} seconds is outside 1..3600. This is how long a request "
                + "may wait for a free worker, which is a different budget from how long the work "
                + "itself may take — zero would refuse every request that arrives while the pool "
                + "is busy.").ConfigureAwait(false);
            return;
        }

        if (request.IdleSeconds is { } idle && (idle < 0 || idle > 86_400))
        {
            await Refuse(
                context, 400,
                $"An idle budget of {idle} seconds is outside 0..86400. Zero is meaningful here — "
                + "it keeps worker processes for ever, which is what this server did before the "
                + "budget existed — and a day is longer than any deployment gains from.")
                .ConfigureAwait(false);
            return;
        }

        if (!await services
            .SetBoundsAsync(
                name,
                request.DeadlineSeconds,
                request.PreflightPairs,
                request.WaitSeconds,
                request.IdleSeconds,
                cancellation)
            .ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No system service '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.limits", name,
            Detail(new
            {
                deadlineSeconds = request.DeadlineSeconds,
                preflightPairs = request.PreflightPairs,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            deadlineSeconds = request.DeadlineSeconds,
            preflightPairs = request.PreflightPairs,
            waitSeconds = request.WaitSeconds,
            idleSeconds = request.IdleSeconds,
            note = request.DeadlineSeconds is null
                ? "Back to the configured defaults. They apply from the next operation — the "
                  + "per-request budgets are read per request and the idle budget on the reaper's "
                  + "next sweep, so nothing has to be restarted."
                : $"Operations on this service are cut off after {request.DeadlineSeconds} "
                  + "seconds and queue for at most "
                  + (request.WaitSeconds is { } w ? $"{w} seconds" : "the default")
                  + ", starting with the next one. Nothing is restarted.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts or stops a service that has no layers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added because it was missing and the console was pretending otherwise.</b> The owner,
    /// 2026-08-17: *"geometry server'in, startı stop'u, timeout'u vs si yok mu?"* The row showed
    /// a <c>started</c> pill that was a literal in the markup — the server had no status to
    /// report, so the console reported one anyway.
    /// </para>
    /// <para>
    /// <b>Separate from the layer route, and the reason is D-57.</b> That defect was a setter
    /// addressed by one thing writing another's column. A system service is a different table
    /// with no layers in it, so it gets its own setter over its own row rather than a shared
    /// one that has to decide which table it meant.
    /// </para>
    /// <para>
    /// <b>503 when stopped, not 404.</b> A stopped service exists and has been turned off, which
    /// is a different fact from *no such service*, and an operator reading a client's logs needs
    /// to be able to tell them apart. The sharing refusal stays 404, because there the
    /// indistinguishability is the point.
    /// </para>
    /// </remarks>
    private static async Task SetSystemStatusAsync(
        HttpContext context,
        string name,
        ServiceStatus status,
        PostgresSystemServices services,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        ServiceStatus? previous = await services
            .SetStatusAsync(name, status, cancellation).ConfigureAwait(false);

        if (previous is null)
        {
            await Refuse(context, 404, $"No system service '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit,
            status == ServiceStatus.Started ? "service.start" : "service.stop", name,
            Detail(new { from = Wire(previous.Value), to = Wire(status) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            from = Wire(previous.Value),
            to = Wire(status),
            note = status == ServiceStatus.Stopped
                ? "Every operation on this service now answers 503. Its sharing is unchanged, so "
                  + "starting it restores exactly the audience it had."
                : "The service is answering again.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts or stops a service (ADR-020 §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>admin:manageServer</c>, not the publisher privilege.</b> Publishing
    /// is a content act; stopping a running service is an operational one that
    /// affects every consumer of it, including people the publisher has never
    /// met.
    /// </para>
    /// <para>
    /// <b>The path names a layer and the effect is the service's</b>, which is worth saying
    /// out loud because getting it wrong shipped. Status moved onto the service in migration
    /// 11 — a container is started or stopped, not a member — but this route was already
    /// layer-scoped and stayed. Until 2026-08-17 the setter behind it wrote <c>layer.status</c>
    /// to match the path, and every reader took <c>service.status</c>, so a stop answered 200
    /// and changed nothing at all
    /// (<see href="../../docs/architecture-debt.md">D-57</see>). Stopping any member stops
    /// the service; the note in the answer says *service* for that reason.
    /// </para>
    /// </remarks>
    private static async Task SetStatusAsync(
        HttpContext context,
        string name,
        ServiceStatus status,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        ServiceStatus? previous =
            await catalog.SetStatusAsync(name, status, cancellation).ConfigureAwait(false);

        if (previous is null)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, status == ServiceStatus.Started ? "layer.start" : "layer.stop", name,
            Detail(new { from = Wire(previous.Value), to = Wire(status) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            from = Wire(previous.Value),
            to = Wire(status),

            // Said because it is the question an operator asks next, and the
            // answer is not obvious: stopping does not change who the service is
            // shared with, so starting it again restores exactly what it was.
            note = status == ServiceStatus.Stopped
                ? "Requests for this service now answer 503. Its sharing is unchanged, so starting "
                  + "it restores exactly the audience it had."
                : "The service is serving again.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task UnpublishAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        // Read before the delete, because afterwards there is nothing left to
        // derive the cache key from. Nothing here depends on it succeeding: a
        // shape left behind expires on its own, and would only ever be reused by
        // a layer republished onto the very same table.
        PublishedLayer? going = await layers.FindAsync(name, cancellation).ConfigureAwait(false);

        bool removed = await catalog.UnpublishLayerAsync(name, cancellation).ConfigureAwait(false);

        if (removed && going is not null)
        {
            contexts.Forget(going);

            // ADR-010 §5.1's *wrong* class, not the stale one. An unpublished
            // layer's tiles must go rather than expire: republishing the same
            // name over a different table would otherwise serve the old table's
            // pictures until the TTL ran out.
            tiles.Purge(going.Id);
        }

        await AuditAsync(
            context, audit, "layer.unpublish", name, Detail(new { removed }),
            succeeded: removed, cancellation).ConfigureAwait(false);

        if (!removed)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            name,
            removed = true,

            // Said out loud, because "delete" on a layer that reads somebody
            // else's database could reasonably be feared to mean more.
            note = "The registration was removed. The table in the data source was not touched.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets a layer's remembered shape, so the next request re-reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This endpoint exists because the cache lifetime is a guess.</b>
    /// <see cref="ServiceContexts.Lifetime"/> bounds how long a table altered
    /// underneath us goes unnoticed, and the number was argued for rather than
    /// measured. An operator who has just added a column and does not want to
    /// wait it out should not have to restart the server — which is what they
    /// would do instead, and it would take every other layer down with it.
    /// </para>
    /// <para>
    /// <b>It needs <c>admin:manageServer</c>, not a content privilege.</b>
    /// Nothing about the layer changes; this is an operational act on the
    /// process, and the caller is being trusted with a lever over its behaviour.
    /// </para>
    /// <para>
    /// <b>It is local to this process.</b> Two servers over one platform store
    /// means two caches, and this clears the one that answered the call. That is
    /// stated in the response rather than left as a surprise — D-17 fixed the
    /// per-request cost, not the multi-process story, and pretending otherwise
    /// is how an operator comes to believe they have fixed something.
    /// </para>
    /// </remarks>
    private static async Task RefreshAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        PublishedLayer? layer = await layers.FindAsync(name, cancellation).ConfigureAwait(false);

        if (layer is null)
        {
            await AuditAsync(context, audit, "layer.refresh", name, Detail(new { found = false }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        contexts.Forget(layer);

        // <b>Tiles go too, and the shape change alone would not have removed
        // them.</b> A fingerprint change makes entries built from the old
        // columns unreachable, which is structural invalidation and correct —
        // but an operator refreshing after an ALTER that changed *values*
        // rather than columns keeps the same fingerprint and would keep the old
        // pictures. Refresh means *forget what you know about this layer*, so
        // it means both.
        int purged = tiles.Purge(layer.Id);

        await AuditAsync(context, audit, "layer.refresh", name,
            Detail(new { found = true, tilesPurged = purged }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            refreshed = true,
            tilesPurged = purged,
            note = "The next request re-reads this table's columns and extent from the data "
                 + "source, and any cached tiles are gone. This clears the caches of the server "
                 + "that answered the call; another server over the same platform store keeps its "
                 + "own until they expire.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- helpers ----------

    private static object Describe(ProbeResult result) => new
    {
        outcome = result.Outcome.ToString(),
        result.Message,
        canPublish = result.CanPublish,
        serverVersion = result.ServerVersion,
        postgisVersion = result.PostgisVersion,
        tables = result.Tables.Select(t => new
        {
            t.SchemaName,
            t.TableName,
            t.GeometryColumn,
            t.Srid,
            geometryType = t.GeometryType,
            objectIdColumn = t.CandidateObjectIdColumn,
            arcGisServable = t.CandidateObjectIdColumn is not null,
            t.Writable,
        }),
    };

    private static bool TryReadPublication(
        PublishRequest request, out LayerPublication? publication, out string? error)
    {
        publication = null;
        error = null;

        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.SchemaName)
            || string.IsNullOrWhiteSpace(request.TableName)
            || string.IsNullOrWhiteSpace(request.GeometryColumn)
            || string.IsNullOrWhiteSpace(request.IdentityColumn))
        {
            error = "name, schemaName, tableName, geometryColumn and identityColumn are required.";
            return false;
        }

        if (request.Srid <= 0)
        {
            error = "srid must be a positive integer.";
            return false;
        }

        if (!Enum.TryParse(request.GeometryType, ignoreCase: true, out GeometryKind kind))
        {
            error =
                $"geometryType '{request.GeometryType}' is not one of: "
                + string.Join(", ", Enum.GetNames<GeometryKind>()) + ".";
            return false;
        }

        // Private unless asked otherwise. ADR-018 §3b's default, enforced at the
        // one place a layer comes into existence.
        if (!TryReadScope(request.Sharing ?? "private", out SharingScope scope, out error))
        {
            return false;
        }

        // <b>These last three used to be missing, and the record's defaults are why
        // nobody noticed.</b> Until 2026-08-16 this call stopped at `scope`;
        // ServiceName, ParentLayerIndex and CacheSeconds all default to null on
        // LayerPublication, so the shorter call compiled and the endpoint accepted
        // three documented fields and discarded them. The catalogue and the SQL
        // beneath it were correct the whole time — `ServiceName ?? Name`, parent
        // and cache are all handled — and PostgresAdminCatalogTests proved that
        // half. Nothing tested the mapping from the request to the record, so the
        // owner's 2026-08-15 correction that *"a service is a combination of
        // layers"* was unreachable through the admin API while the catalogue could
        // do it, and `POST /admin/featureservices` was answering 201 with a note
        // telling the operator to use a parameter that did nothing. D-47.
        publication = new LayerPublication(
            request.Name!,
            request.DataSourceId,
            request.SchemaName!,
            request.TableName!,
            request.GeometryColumn!,
            request.IdentityColumn!,
            string.IsNullOrWhiteSpace(request.ObjectIdColumn) ? null : request.ObjectIdColumn,
            request.Srid,
            kind,
            scope,
            string.IsNullOrWhiteSpace(request.ServiceName) ? null : request.ServiceName.Trim(),
            request.ParentLayerId,
            request.CacheSeconds,
            string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim());

        return true;
    }

    private static bool TryReadScope(string? value, out SharingScope scope, out string? error)
    {
        scope = SharingScope.Private;
        error = null;

        switch (value?.ToLowerInvariant())
        {
            case "private": scope = SharingScope.Private; return true;
            case "organization": scope = SharingScope.Organization; return true;
            case "public": scope = SharingScope.Public; return true;

            // ADR-036's fourth scope. Which groups is a separate act —
            // PUT /admin/groups/{name}/items/{service} — and the refusal for a group-scoped
            // service shared with nothing says so.
            case "group": scope = SharingScope.Group; return true;
            default:
                error = "sharing must be 'private', 'organization' or 'public'.";
                return false;
        }
    }

    private static string Conflict(PostgresException e, LayerPublication publication) => e.SqlState switch
    {
        "23505" =>
            $"Either a layer named '{publication.Name}' already exists, or this table is already "
            + "published from this data source. A table may be published once per source.",
        "23503" => "That data source id does not exist.",
        _ =>
            $"'{publication.GeometryType}' is not a geometry type the catalogue accepts.",
    };

    /// <summary>
    /// Host and database from a connection string, and nothing else.
    /// </summary>
    /// <remarks>
    /// Built by parsing rather than by trimming, so that a password containing a
    /// semicolon cannot survive into an audit record by accident.
    /// </remarks>
    private static string Summarise(string connectionString)
    {
        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch (ArgumentException)
        {
            return "unparseable";
        }
    }

    private static string Wire(ServiceStatus status) =>
        Platform.Postgres.PostgresAdminCatalog.Wire(status);

    private static string PostgresSharing(SharingScope scope) =>
        Platform.Postgres.PostgresAdminCatalog.Wire(scope);

    private static string Detail(object value) => JsonSerializer.Serialize(value);

    private static Task AuditAsync(
        HttpContext context,
        IAuditLog audit,
        string action,
        string? resource,
        string detail,
        bool succeeded,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                context.Connection.RemoteIpAddress?.ToString(),
                action,
                resource,
                detail,
                succeeded),
            cancellation);
    }

    private static Task Refuse(HttpContext context, int status, string message) =>
        Results.Json(new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context);
}
