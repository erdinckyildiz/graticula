using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>A registered database's publisher address — ADR-299, migration 136.</summary>
internal static partial class AdminEndpoints
{
    /// <summary>What a publisher address may be: a host name, an IPv4 address or a bracketed IPv6 one, and a port.</summary>
    private static readonly Regex PublisherAddressShape = new(
        @"^(?<host>[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])(?:,(?<port>\d{1,5}))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The body of <c>PUT /admin/datasources/{id}/publisher-address</c>.</summary>
    /// <param name="Address"><c>host</c> or <c>host,port</c>; null or empty clears it.</param>
    internal sealed record PublisherAddressRequest(string? Address);

    private static void MapPublisherAddress(WebApplication app) =>
        app.MapPut("/admin/datasources/{id:guid}/publisher-address", SetPublisherAddressAsync);

    /// <summary>
    /// Sets the address ArcGIS Pro and other publishers reach a registered database at, when it is not the one this server
    /// uses — ArcGIS's publisher database connection (ADR-299 §5c).
    /// </summary>
    /// <remarks>
    /// <b>The address only, never a credential.</b> Pro matches a layer to a registered database by host, port and
    /// database (ADR-299 §3); the user and password it connected with are its own. Written the way Pro writes
    /// DB_CONNECTION_PROPERTIES — <c>db.example.org</c> or <c>db.example.org,5433</c> — so what an operator types is what
    /// they see in Pro's connection properties.
    /// </remarks>
    private static async Task SetPublisherAddressAsync(
        HttpContext context, Guid id, PublisherAddressRequest? request, IAdminCatalog catalog, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore).ConfigureAwait(false))
        {
            return;
        }

        if (catalog is not PostgresAdminCatalog postgres)
        {
            await Refuse(context, 501, "This catalogue does not keep publisher addresses.").ConfigureAwait(false);
            return;
        }

        string? address = string.IsNullOrWhiteSpace(request?.Address) ? null : request.Address.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

        if (address is not null)
        {
            Match shape = PublisherAddressShape.Match(address);

            if (!shape.Success
                || (shape.Groups["port"].Success
                    && int.Parse(shape.Groups["port"].Value, CultureInfo.InvariantCulture) is < 1 or > 65535))
            {
                await Refuse(
                    context, 400,
                    "A publisher address is a host, or a host and a port after a comma — db.example.org or "
                    + "db.example.org,5433 — the way ArcGIS Pro shows it in a database connection's properties.")
                    .ConfigureAwait(false);
                return;
            }

            // 5432 is PostgreSQL's own; written or not, it is one address, and one spelling keeps matching simple.
            if (address.EndsWith(",5432", StringComparison.Ordinal))
            {
                address = address[..^",5432".Length];
            }
        }

        if (!await postgres.SetPublisherAddressAsync(id, address, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"There is no data source with id {id}.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "datasource.publisher-address", id.ToString(),
            Detail(new { address }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { source = id, publisherAddress = address }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
