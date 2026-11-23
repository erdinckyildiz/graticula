using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The platform store's primary and standby, as this node sees them — ADR-319 §5.10: <c>/admin/site/store</c>, and
/// <c>/admin/site/store/promote</c>, ArcGIS Data Store's <c>makePrimary</c> (§5.7). And the question the nodes ask each
/// other before a failover, <c>/healthz/store</c> (§5.4).
/// </summary>
/// <remarks>
/// <b>The system's</b>, as a machine of the site is: under <c>/admin/site</c>, so on the system's organisation only, and
/// behind <see cref="Privilege.AdminManageServer"/>.
/// </remarks>
internal static class StoreEndpoints
{
    /// <summary>Registers the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/admin/site/store", ReadAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapPost("/admin/site/store/promote", PromoteAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapGet(StoreFailover.ViewPath, View);
    }

    /// <summary>
    /// §5.4: another node of this site asks whether this one reaches a host. Answered only to a question proved by the
    /// site's key, and the answer is proved the same way; anyone else is told nothing exists here.
    /// </summary>
    private static IResult View(HttpContext context, StoreFailover failover)
    {
        string? host = context.Request.Query["primary"];
        string? nonce = context.Request.Query["nonce"];

        if (!failover.Active || host is not { Length: > 0 } || nonce is not { Length: > 0 }
            || !failover.Proves("ask:" + host + ":" + nonce, context.Request.Headers[StoreFailover.ProofHeader]))
        {
            return Results.NotFound();
        }

        bool reaches = failover.Reaches(host);
        return Results.Ok(new { reaches, proof = failover.Proof("answer:" + host + ":" + nonce + ":" + (reaches ? "yes" : "no")) });
    }

    private static async Task<IResult> ReadAsync(HttpContext context, StoreFailover failover, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        if (!failover.Active)
        {
            return Results.Ok(new { configured = false, mode = (string?)null, hosts = Array.Empty<object>(), lastFailover = (object?)null });
        }

        StoreFailover.Failover? last = null;

        try
        {
            last = await failover.LastFailoverAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (StoreOutage.Is(e) || e is InvalidOperationException)
        {
            // The report is still worth having while the store is failing over — that is when it is read.
        }

        string? primary = failover.CurrentPrimary();

        return Results.Ok(new
        {
            configured = true,
            mode = failover.Mode,
            primary,
            held = failover.Held,
            epoch = StoreEpoch.All().ToDictionary(e => e.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), e => e.Value),
            hosts = failover.Views.Select(v => new
            {
                address = v.Address,
                role = Role(v, primary),
                reachable = v.Reachable,
                timeline = v.Reachable ? v.Timeline : (int?)null,
                fenced = v.Fenced,
                receiver = v.Recovering ? v.Receiver : null,
                standbys = v.Reachable && !v.Recovering ? v.Standbys : (int?)null,
                lagBytes = v.LagBytes,
                replayLagSeconds = v.ReplayLagSeconds,
                seenAt = v.At == DateTimeOffset.MinValue ? (DateTimeOffset?)null : v.At,
                error = v.Reachable ? null : v.Error,
            }),
            lastFailover = last is null ? null : new
            {
                at = last.At,
                from = last.From,
                to = last.To,
                timeline = last.Timeline,
                decidedBy = last.DecidedBy,
                how = last.How,
                unreachableMs = last.UnreachableMs,
            },
        });
    }

    /// <summary>A host's role as the report and the console name it.</summary>
    internal static string Role(StoreFailover.HostView view, string? primary) =>
        !view.Reachable ? "unreachable"
        : view.Recovering ? "standby"
        : string.Equals(view.Address, primary, StringComparison.OrdinalIgnoreCase) ? "primary"
        : "fenced";

    private static async Task<IResult> PromoteAsync(HttpContext context, StoreFailover failover, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        (StoreFailover.HostView? standby, string? refusal) = failover.ManualCandidate();

        if (standby is null)
        {
            return Results.Json(new { error = new { code = 409, message = refusal } }, statusCode: StatusCodes.Status409Conflict);
        }

        StoreFailover.Failover? done = await failover.PromoteAsync(standby, "manual", cancellation).ConfigureAwait(false);

        return done is null
            ? Results.Json(new { error = new { code = 409, message = "A promotion is already under way on this node." } }, statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(new { promoted = done.To, timeline = done.Timeline, from = done.From });
    }
}
