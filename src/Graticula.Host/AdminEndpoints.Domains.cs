using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>A shared domain as the admin surface takes it.</summary>
/// <param name="Domain">The ArcGIS domain object.</param>
internal sealed record SharedDomainRequest(JsonElement? Domain);

/// <summary>
/// Shared domains — ADR-087: a named list or range that fields on many layers point at, edited once.
/// </summary>
/// <remarks>
/// <para>
/// <b>By owner decision, 2026-09-23 and 2026-09-24.</b> Its owner and administrators edit it; one in use is
/// not deleted, and the refusal says where it is used; names are unique across the server.
/// </para>
/// <para>
/// <b>An edit is judged against every field that uses it</b>, because it changes all of them at once: a list
/// whose codes no longer fit one column, or a range a subtype's starting value falls outside, is refused with
/// the layer and column named rather than stored and found by whoever edits that layer next.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapSharedDomains(WebApplication app)
    {
        app.MapGet("/admin/domains", ListSharedDomainsAsync);
        app.MapGet("/admin/domains/{id:guid}", GetSharedDomainAsync);
        app.MapPost("/admin/domains", CreateSharedDomainAsync);
        app.MapPut("/admin/domains/{id:guid}", UpdateSharedDomainAsync);
        app.MapDelete("/admin/domains/{id:guid}", DeleteSharedDomainAsync);
    }

    private static async Task ListSharedDomainsAsync(
        HttpContext context,
        IFieldDomainStore domains,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<SharedDomain> all = await domains.ListAsync(cancellation).ConfigureAwait(false);

        // <b>The type of a column that uses each, so a screen can show a date range as dates</b> — design review
        // 2026-09-24 found one printed as milliseconds. One table read per layer that uses any domain, and only
        // on this listing.
        Dictionary<Guid, LayerDescription?> tables = [];
        List<object> described = new(all.Count);

        foreach (SharedDomain d in all)
        {
            string? fieldType = null;

            foreach (DomainUse use in d.Uses)
            {
                if (!tables.TryGetValue(use.LayerId, out LayerDescription? table))
                {
                    table = await layers.FindByIdAsync(use.LayerId, cancellation).ConfigureAwait(false) is { } layer
                        ? (await contexts.TableAsync(layer, cancellation).ConfigureAwait(false)).Item2
                        : null;
                    tables[use.LayerId] = table;
                }

                if (table?.Find(use.Column) is { } column)
                {
                    fieldType = column.Type.ToString();
                    break;
                }
            }

            described.Add(DescribeShared(context, d, fieldType));
        }

        await Results.Json(new { domains = described }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task GetSharedDomainAsync(
        HttpContext context, Guid id, IFieldDomainStore domains, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await domains.FindAsync(id, cancellation).ConfigureAwait(false) is not { } found)
        {
            await Refuse(context, 404, $"No shared domain {id}.").ConfigureAwait(false);
            return;
        }

        await Results.Json(DescribeShared(context, found)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateSharedDomainAsync(
        HttpContext context,
        SharedDomainRequest request,
        IFieldDomainStore domains,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await ReadSharedAsync(context, request) is not { } domain)
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await domains.CreateAsync(domain, current.Principal.Id, cancellation).ConfigureAwait(false)
            is not { } created)
        {
            await Refuse(
                context, 409,
                $"There is already a shared domain named '{domain.Name}'. Names are unique across the server; "
                + "give the column that one, or choose another name.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "domain.create", created.Domain.Name, Detail(new { id = created.Id }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(DescribeShared(context, created), statusCode: StatusCodes.Status201Created)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task UpdateSharedDomainAsync(
        HttpContext context,
        Guid id,
        SharedDomainRequest request,
        IFieldDomainStore domains,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await ChangeableSharedAsync(context, domains, id, "change", cancellation).ConfigureAwait(false)
            is not { } found)
        {
            return;
        }

        if (await ReadSharedAsync(context, request) is not { } domain)
        {
            return;
        }

        domain = domain.WithId(id);

        if (await UnfitAnywhereAsync(found, domain, layers, contexts, cancellation).ConfigureAwait(false)
            is { } unfit)
        {
            await AuditAsync(
                context, audit, "domain.update", found.Domain.Name, Detail(new { id, refused = unfit }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, unfit).ConfigureAwait(false);
            return;
        }

        if (!await domains.UpdateAsync(id, domain, cancellation).ConfigureAwait(false))
        {
            await Refuse(
                context, 409,
                $"There is already another shared domain named '{domain.Name}'. Names are unique across the server.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "domain.update", domain.Name,
            Detail(new { id, from = found.Domain.Name, fields = found.Uses.Count }),
            succeeded: true, cancellation).ConfigureAwait(false);

        SharedDomain now = await domains.FindAsync(id, cancellation).ConfigureAwait(false) ?? found;

        await Results.Json(DescribeShared(context, now)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteSharedDomainAsync(
        HttpContext context, Guid id, IFieldDomainStore domains, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await ChangeableSharedAsync(context, domains, id, "delete", cancellation).ConfigureAwait(false)
            is not { } found)
        {
            return;
        }

        IReadOnlyList<DomainUse> uses = await domains.DeleteAsync(id, cancellation).ConfigureAwait(false);

        if (uses.Count > 0)
        {
            await AuditAsync(
                context, audit, "domain.delete", found.Domain.Name, Detail(new { id, inUse = uses.Count }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(
                context, 409,
                $"'{found.Domain.Name}' is used by {Fields(uses)}, so it is not deleted: a field pointing at a "
                + "domain that is gone would take any value. Take it off those fields first.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "domain.delete", found.Domain.Name, Detail(new { id }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>The shared domain, when the caller may change it; the refusal is written when not.</summary>
    private static async Task<SharedDomain?> ChangeableSharedAsync(
        HttpContext context, IFieldDomainStore domains, Guid id, string what, CancellationToken cancellation)
    {
        if (await domains.FindAsync(id, cancellation).ConfigureAwait(false) is not { } found)
        {
            await Refuse(context, 404, $"No shared domain {id}.").ConfigureAwait(false);
            return null;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!LayerAccess.MayManage(found.Owner, current.Principal, current.Authorization))
        {
            await Refuse(
                context, 403,
                $"'{found.Domain.Name}' is not yours, so this server does not {what} it for you: a shared domain "
                + "is changed by its owner or an administrator, because a change reaches every layer that uses it.")
                .ConfigureAwait(false);
            return null;
        }

        return found;
    }

    /// <summary>Reads a domain from a request body, its shape judged and its fit to any column not.</summary>
    private static async Task<FieldDomain?> ReadSharedAsync(HttpContext context, SharedDomainRequest request)
    {
        if (request.Domain is not { ValueKind: JsonValueKind.Object } given)
        {
            await Refuse(context, 400, "A shared domain is sent as domain: {type, name, codedValues or range}.")
                .ConfigureAwait(false);
            return null;
        }

        if (FieldDomainJson.ReadDomain(given, out string? unreadable) is not { } domain)
        {
            await Refuse(context, 400, $"The domain cannot be read: {unreadable}").ConfigureAwait(false);
            return null;
        }

        if (domain.Name.Trim().Length == 0 || domain.Name.Length > DomainRules.LongestName)
        {
            await Refuse(context, 400, $"A shared domain has a name of 1 to {DomainRules.LongestName} characters.")
                .ConfigureAwait(false);
            return null;
        }

        if (domain.Kind == DomainKind.CodedValue
            && (domain.Codes.Count == 0 || domain.Codes.Count > DomainRules.MaximumCodes))
        {
            await Refuse(context, 400, $"A list has 1 to {DomainRules.MaximumCodes:N0} codes.").ConfigureAwait(false);
            return null;
        }

        return domain.Named(domain.Name.Trim());
    }

    /// <summary>
    /// Why a shared domain, changed to <paramref name="changed"/>, would no longer fit a field that uses it — or
    /// null when it fits them all.
    /// </summary>
    /// <remarks>
    /// <b>Each layer is judged as its own save would judge it</b>: the column's type for a domain on the column,
    /// and the whole set of subtypes — starting values included — for one a subtype gives. A column the table no
    /// longer has is skipped, for ADR-063's drift answer.
    /// </remarks>
    private static async Task<string?> UnfitAnywhereAsync(
        SharedDomain found,
        FieldDomain changed,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        Guid id = found.Id;
        FieldDomain Swap(FieldDomain d) => d.Id == id ? changed : d;

        foreach (Guid layerId in found.Uses.Select(u => u.LayerId).Distinct())
        {
            if (await layers.FindByIdAsync(layerId, cancellation).ConfigureAwait(false) is not { } layer)
            {
                continue;
            }

            (_, LayerDescription table) = await contexts.TableAsync(layer, cancellation).ConfigureAwait(false);

            foreach (FieldOverride says in layer.FieldOverrides)
            {
                if (says.Domain is { } own && own.Id == id
                    && table.Find(says.Column) is { } column
                    && DomainRules.Refuse(changed, column.Type, column.MaxLength) is { } unfit)
                {
                    return $"'{found.Domain.Name}' is used by '{says.Column}' on {layer.Definition.Name}, and the change does not "
                        + $"fit it: {unfit}";
                }

                if (says.Subtypes is { } subtypes && table.Find(says.Column) is not null)
                {
                    LayerSubtypes swapped = subtypes with
                    {
                        Types = [.. subtypes.Types.Select(t => t with
                        {
                            Domains = t.Domains.ToDictionary(kv => kv.Key, kv => Swap(kv.Value), StringComparer.Ordinal),
                        })],
                    };

                    FieldDomain? Own(string name) =>
                        layer.FieldOverrides.FirstOrDefault(o => o.Matches(name)).Domain is { } d ? Swap(d) : null;

                    if (DomainRules.Refuse(swapped, table, _ => null, Own) is { } refused)
                    {
                        return $"'{found.Domain.Name}' is used by the subtypes of {layer.Definition.Name}, and the change does "
                            + $"not fit them: {refused}";
                    }
                }
            }
        }

        return null;
    }

    private static string Fields(IReadOnlyList<DomainUse> uses)
    {
        string[] named = [.. uses.Take(5).Select(u =>
            u.Subtype is { } code ? $"'{u.Column}' on {u.Layer} (subtype {code})" : $"'{u.Column}' on {u.Layer}")];

        return uses.Count <= 5
            ? string.Join(", ", named)
            : $"{string.Join(", ", named)} and {uses.Count - 5} more";
    }

    private static object DescribeShared(HttpContext context, SharedDomain shared, string? fieldType = null)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return new
        {
            id = shared.Id,
            name = shared.Domain.Name,
            type = shared.Domain.Kind == DomainKind.Range ? "range" : "codedValue",
            domain = FieldDomainJson.Write(shared.Domain),
            owner = shared.OwnerName,
            mayChange = LayerAccess.MayManage(shared.Owner, current.Principal, current.Authorization),
            updatedAt = shared.UpdatedAt,

            // The type of a column that uses it, when one does: how its values are read and typed on a screen.
            fieldType,
            uses = shared.Uses.Select(u => new { layer = u.Layer, column = u.Column, subtype = u.Subtype }).ToArray(),
        };
    }
}
