using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// What an organisation calls itself — ADR-221 condition 3: its name and description, read by anybody signed in to it
/// and changed by its own administrator, as an ArcGIS Online administrator sets the organisation's name and description.
/// Its host names, suspension and deletion stay the system administrator's (<c>/admin/tenants</c>).
/// </summary>
internal static class OrganizationProfileEndpoints
{
    /// <summary>The longest name accepted.</summary>
    internal const int NameLimit = 100;

    /// <summary>The longest description accepted.</summary>
    internal const int DescriptionLimit = 2000;

    /// <summary>A change: a name, a description, or both.</summary>
    internal sealed record DescribeRequest(string? Name, string? Description);

    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/organization", ReadAsync);
        app.MapPut("/admin/organization", DescribeAsync);
    }

    private static IResult Refused(int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);

    private static object Describe(Tenant tenant, bool mayChange) => new
    {
        key = tenant.Key,
        name = tenant.Name,
        description = tenant.Description,
        hosts = tenant.Hosts,
        isDefault = tenant.IsDefault,
        mayChange,
        note = "The organization's name and description are its administrator's. Its addresses, suspension and removal "
            + "are the system administrator's.",
    };

    private static bool MayChange(RequestPrincipal current) =>
        !current.Principal.IsAnonymous && current.Authorization.IsAdministrator && current.HomeOrganisation is null;

    private static IResult ReadAsync(HttpContext context)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous || current.HomeOrganisation is not null)
        {
            return Refused(current.Principal.IsAnonymous ? 401 : 403, "The organization is read by its own members.");
        }

        return context.Features.Get<TenantFeature>() is { } here
            ? Results.Ok(Describe(here.Tenant, MayChange(current)))
            : Refused(404, "No organization is reached at this address.");
    }

    private static async Task<IResult> DescribeAsync(
        HttpContext context, DescribeRequest request, ITenantStore tenants, TenantDirectory directory, IAuditLog audit,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!MayChange(current))
        {
            return Refused(current.Principal.IsAnonymous ? 401 : 403, "Only this organization's administrator names and describes it.");
        }

        if (context.Features.Get<TenantFeature>() is not { } here)
        {
            return Refused(404, "No organization is reached at this address.");
        }

        string name = (request.Name ?? here.Tenant.Name).Trim();
        string? description = request.Description is null ? here.Tenant.Description : request.Description.Trim();

        if (name.Length == 0 || name.Length > NameLimit)
        {
            return Refused(400, $"An organization's name is 1 to {NameLimit} characters.");
        }

        if (description is { Length: > DescriptionLimit })
        {
            return Refused(400, $"An organization's description is at most {DescriptionLimit} characters.");
        }

        if (await tenants.DescribeAsync(here.Tenant.Key, name, description, cancellation).ConfigureAwait(false) is not { } changed)
        {
            return Refused(404, "No organization is reached at this address.");
        }

        await directory.RefreshAsync(cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(
            context, audit, "organization.describe", changed.Key,
            AdminEndpoints.Detail(new { name = changed.Name, described = changed.Description is not null }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(Describe(changed, mayChange: true));
    }
}
