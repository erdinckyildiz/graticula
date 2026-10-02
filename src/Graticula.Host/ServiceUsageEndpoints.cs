using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The server's administrator reads every service's use, and writes the minute's counts now — ADR-135. Each item's
/// own use reaches its owner with the item, in the content listing and on the portal face.
/// </summary>
internal static class ServiceUsageEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/usage", ReadAsync);
        app.MapPost("/admin/usage/flush", FlushAsync);
    }

    private static async Task ReadAsync(HttpContext context, ServiceUsageCounter usage)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        await Results.Json(new
        {
            services = usage.Snapshot.Values.OrderByDescending(u => u.Last30).Select(u => new
            {
                serviceId = u.ServiceId,
                requests30 = u.Last30,
                requests7 = u.Last7,
                requests = u.Total,
                lastUsed = u.LastDay?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Writes what has been counted and reads it back, rather than waiting for the minute.</summary>
    private static async Task FlushAsync(HttpContext context, ServiceUsageCounter usage, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        await usage.FlushAsync(cancellation).ConfigureAwait(false);
        await Results.Json(new { counted = usage.Snapshot.Count }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
