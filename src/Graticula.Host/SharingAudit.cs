using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// Records the reads that only an administrator's override made possible.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-018 condition 3.</b> *"<c>admin:viewAllContent</c> is auditable. An administrator
/// reading a private layer is legitimate and must leave a record, or the sharing model is
/// decorative."* The privilege is deliberately wide — somebody has to be able to see what the
/// server holds — and the whole answer to *why is that safe* is that using it is visible
/// afterwards. Without the record the privilege is indistinguishable from having no sharing
/// model at all for whoever holds it.
/// </para>
/// <para>
/// <b>It lives here because the condition was met on exactly one route out of many.</b> Measured
/// 2026-08-27: the record was written in the FeatureServer <c>query</c> handler and nowhere else,
/// so a second administrator reading a private service through its FeatureServer *document* —
/// or its tiles, its map image, its OGC collection, its WFS feature — was answered <c>200</c>
/// and left nothing behind. The probe created a private service and a second administrator, read
/// <c>/rest/services/zz_ovr/FeatureServer</c> as them, and found four audit rows about the
/// fixture and not one about the read.
/// </para>
/// <para>
/// <b>Two call sites, because there are two resolvers.</b> <see cref="ServiceLookup"/> is the
/// choke point for every service face — feature, tile, map, attachment, OGC, WFS — and
/// <c>ImageServerEndpoints</c> resolves coverages from their own catalogue. A third resolver
/// would need a third call, and there is nothing here that would detect one that forgot; that is
/// stated rather than solved, because the alternative is a middleware that would have to
/// re-derive the sharing decision it is trying to observe.
/// </para>
/// <para>
/// <b>What is deliberately not recorded: listings.</b> <c>GET /admin/layers</c> and the console's
/// content screen enumerate everything the privilege reaches, and auditing those would write a
/// row per item per page load. A register that grows by a hundred rows every time somebody opens
/// a screen is one nobody reads, which costs more than the rows are worth. The boundary is
/// therefore *a read of one named item's surface*, and it is a judgement rather than a rule the
/// code enforces.
/// </para>
/// <para>
/// <b>The path, never the query string.</b> <see cref="AuditEvent.Detail"/> may not carry a
/// secret ([ADR-045](../../docs/adr/ADR-045-observability.md) condition 2), and ArcGIS clients
/// put tokens in <c>?token=</c>. <c>Request.Path</c> excludes the query string by construction,
/// which is why it is the half that is stored — the face and the layer number are in it, and
/// those are what an incident actually asks about.
/// </para>
/// </remarks>
internal static class SharingAudit
{
    /// <summary>The verb, spelled once.</summary>
    /// <remarks>
    /// <b>It used to be <c>layer.read.override</c>, and the rename is the point.</b> The record is
    /// now written where a *service* is resolved, before any layer is known, so a name promising a
    /// layer would be wrong on most of the routes that write it. The detail carries the path, and
    /// the path names the layer where there is one.
    /// </remarks>
    public const string Action = "content.read.override";

    /// <summary>
    /// Records that this read happened only because the caller may see everything.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="resource">The item read, qualified by folder where it has one.</param>
    /// <param name="sharing">What the item's sharing scope actually is.</param>
    /// <returns>The write, or a completed task when there is no audit log.</returns>
    public static async Task RecordOverrideAsync(
        HttpContext context, string resource, SharingScope sharing)
    {
        // <b>Absent rather than required.</b> The host registers one; a test host that does not
        // should refuse reads for the reasons it configures, not fail them on a missing sink.
        if (context.RequestServices.GetService<IAuditLog>() is not { } audit)
        {
            return;
        }

        if (context.Features.Get<RequestPrincipal>() is not { } current)
        {
            return;
        }

        await audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                CallerAddress.Of(context)?.ToString(),
                Action,
                resource,
                JsonSerializer.Serialize(
                    new
                    {
                        sharing = sharing.ToString().ToLowerInvariant(),
                        path = context.Request.Path.Value,
                    }),
                Succeeded: true),

            // <b>Not `context.RequestAborted`.</b> A client that disconnects mid-response has
            // still been served the bytes the override allowed, and cancelling the record on the
            // way out would make *hang up quickly* a way to read without being seen.
            System.Threading.CancellationToken.None).ConfigureAwait(false);
    }
}
