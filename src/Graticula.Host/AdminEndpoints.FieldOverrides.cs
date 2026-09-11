using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
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

/// <summary>
/// A layer's per-column overrides — ADR-063.
/// </summary>
/// <remarks>
/// <para>
/// <b>In its own file</b> because <c>AdminEndpoints.cs</c> is ten thousand lines and more than
/// one piece of work is in it at a time; the routes are registered from here and the only
/// edit to that file is the call that does it.
/// </para>
/// <para>
/// <b>The admin surface is the one reader that sees hidden columns</b>, through
/// <see cref="ServiceContexts.TableAsync"/>, because an operator has to see a column in order
/// to unhide it. Every serving face goes through <see cref="ServiceContexts.GetAsync"/>, where
/// a hidden column does not exist.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary>
    /// The longest label accepted.
    /// </summary>
    /// <remarks>
    /// <b>A label is read by a person in a column header or a pop-up</b>, and 255 is the
    /// length ArcGIS itself allows a field alias. A longer one would be accepted here and cut by
    /// every client that shows it, which is a label nobody sees whole.
    /// </remarks>
    private const int LongestAlias = 255;

    private static void MapFieldOverrides(WebApplication app)
    {
        app.MapGet("/admin/layers/{name}/fields", GetFieldOverridesAsync);
        app.MapPut("/admin/layers/{name}/fields", SetFieldOverridesAsync);
    }

    private static async Task GetFieldOverridesAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (await OneNamedLayerAsync(context, layers, name, cancellation).ConfigureAwait(false)
            is not { } layer)
        {
            return;
        }

        await WriteFieldOverridesAsync(context, layer, layer.FieldOverrides, contexts, cancellation)
            .ConfigureAwait(false);
    }

    private static async Task SetFieldOverridesAsync(
        HttpContext context,
        string name,
        FieldOverridesRequest request,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
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

        if (await OneNamedLayerAsync(context, layers, name, cancellation).ConfigureAwait(false)
            is not { } layer)
        {
            await AuditAsync(
                context, audit, "layer.fieldOverrides", name, Detail(new { found = false }),
                succeeded: false, cancellation).ConfigureAwait(false);
            return;
        }

        List<FieldOverride> wanted = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (FieldOverrideEntry entry in request.Overrides ?? [])
        {
            string column = (entry.Column ?? string.Empty).Trim();
            string? alias = string.IsNullOrWhiteSpace(entry.Alias) ? null : entry.Alias.Trim();

            if (column.Length == 0)
            {
                await Refuse(context, 400, "An override has no column. Each one names the "
                    + "column it is about.").ConfigureAwait(false);
                return;
            }

            if (column.Length > 63)
            {
                await Refuse(
                    context, 400,
                    $"'{column}' is {column.Length} characters. A column name is at most 63, "
                    + "which is PostgreSQL's own limit, so this cannot be naming a column.")
                    .ConfigureAwait(false);
                return;
            }

            if (!seen.Add(column))
            {
                await Refuse(
                    context, 400,
                    $"'{column}' is overridden twice. One column has one label and is hidden or "
                    + "not; two entries would leave the answer to whichever was read last.")
                    .ConfigureAwait(false);
                return;
            }

            if (alias is { Length: > LongestAlias })
            {
                await Refuse(
                    context, 400,
                    $"The label for '{column}' is {alias.Length} characters. A label is at most "
                    + $"{LongestAlias}, the length ArcGIS allows a field alias, so a longer one "
                    + "would be cut by every client that shows it.")
                    .ConfigureAwait(false);
                return;
            }

            // <b>ADR-063 condition 2, refused here and not at query time.</b> A check at query
            // time is a check somebody can reach a state without passing: a stored layer with
            // its identity hidden would be refused on every request, by every face, for a
            // reason nobody wrote down at the moment they caused it.
            if (Unhideable(layer, column) is { } why && entry.Hidden)
            {
                await Refuse(context, 400, $"'{column}' cannot be hidden: {why}")
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(column, layer.Definition.GeometryColumn, StringComparison.Ordinal))
            {
                await Refuse(
                    context, 400,
                    $"'{column}' is the geometry column. It is not a field on any face — a "
                    + "client reads it as the feature's shape, not as an attribute — so it has "
                    + "no label to give, and hiding it would leave a layer with no shape.")
                    .ConfigureAwait(false);
                return;
            }

            wanted.Add(new FieldOverride(column, alias, entry.Hidden));
        }

        if (!await catalog.SetFieldOverridesAsync(layer.Id, wanted, cancellation)
            .ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        List<FieldOverride> stored = [.. wanted.Where(o => o.SaysSomething)];

        await AuditAsync(
            context, audit, "layer.fieldOverrides", name,
            Detail(new
            {
                hidden = stored.Where(o => o.Hidden).Select(o => o.Column).ToArray(),
                labelled = stored.Count(o => o.Alias is not null),
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await WriteFieldOverridesAsync(context, layer, stored, contexts, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Why a column may not be hidden, or null if it may.
    /// </summary>
    /// <remarks>
    /// <b>The identity columns, because the protocol requires them and a layer without them is
    /// not a layer.</b> ArcGIS addresses a feature by its object id and OGC API Features by its
    /// id; a document without either offers features nobody can ask for again. Editor-tracking
    /// columns belong on this list too and are not here because they do not exist yet — ADR-013
    /// §5a is unbuilt — so the first commit that adds them adds them here.
    /// </remarks>
    private static string? Unhideable(PublishedLayer layer, string column)
    {
        if (string.Equals(column, layer.Definition.IntegerIdentityColumn, StringComparison.Ordinal))
        {
            return "it is this layer's object id. Every ArcGIS client addresses a feature by it, "
                + "so a layer without it offers features nobody can ask for again.";
        }

        if (string.Equals(column, layer.Definition.IdentityColumn, StringComparison.Ordinal))
        {
            return "it is this layer's identity column — the feature id on every face — so a "
                + "layer without it has features nothing can name.";
        }

        return null;
    }

    private static async Task WriteFieldOverridesAsync(
        HttpContext context,
        PublishedLayer layer,
        IReadOnlyList<FieldOverride> overrides,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        // <b>The table's own shape, before the overrides</b> — the one caller allowed it,
        // because an operator has to see a hidden column in order to unhide it.
        (_, LayerDescription table) =
            await contexts.TableAsync(layer, cancellation).ConfigureAwait(false);

        IReadOnlyList<FieldOverride> inert = FieldOverrides.Inert(table, overrides);

        await Results.Json(new
        {
            name = layer.Definition.Name,
            columns = table.Fields.Select(f =>
            {
                FieldOverride? said = overrides.FirstOrDefault(o => o.Matches(f.Name)) is
                    { Column: not null } found ? found : null;

                return new
                {
                    name = f.Name,
                    // <b>The ArcGIS name, which is what the Endpoints tab beside this one
                    // shows and what a publisher sees in every client</b> — design review
                    // 2026-09-11 found the two tabs calling one column `OID` and `Integer`.
                    // The internal enum is this server's vocabulary, not the reader's.
                    type = string.Equals(
                            f.Name, layer.Definition.IntegerIdentityColumn, StringComparison.Ordinal)
                        ? "OID"
                        : Graticula.Api.ArcGis.FeatureServerMetadataWriter.TypeName(f.Type)
                            .Replace("esriFieldType", string.Empty, StringComparison.Ordinal),
                    nullable = f.Nullable,
                    alias = said?.Alias,
                    hidden = said?.Hidden ?? false,
                    locked = Unhideable(layer, f.Name),
                };
            }),

            // <b>ADR-063 condition 3: inert is the design, visible is the condition.</b> An
            // override that names a column this table does not have changes nothing — and an
            // operator who renamed a column and lost its label has to be able to see why.
            inert = inert.Select(o => new { column = o.Column, alias = o.Alias, hidden = o.Hidden }),

            note = inert.Count == 0
                ? "Every override names a column this table has."
                : $"{inert.Count} override(s) name a column this table does not have, so they "
                  + "change nothing. That is what happens when a column is renamed or dropped in "
                  + "the database: the label stays with the old name. Remove them, or rename "
                  + "them to the column's new name.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The body of <c>PUT /admin/layers/{name}/fields</c>.</summary>
    /// <param name="Overrides">The whole list; it replaces what is stored.</param>
    internal sealed record FieldOverridesRequest(
        [property: JsonPropertyName("overrides")] IReadOnlyList<FieldOverrideEntry>? Overrides);

    /// <summary>One entry of <see cref="FieldOverridesRequest"/>.</summary>
    internal sealed record FieldOverrideEntry(
        [property: JsonPropertyName("column")] string? Column,
        [property: JsonPropertyName("alias")] string? Alias,
        [property: JsonPropertyName("hidden")] bool Hidden);
}
