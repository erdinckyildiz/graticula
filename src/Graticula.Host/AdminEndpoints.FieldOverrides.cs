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

        // ADR-064: each role in one column, and a role only on a column that can hold it — which
        // needs the table's own shape, hidden columns included, since that is what is written to.
        HashSet<EditRole> roles = [];

        (_, LayerDescription table) = await contexts.TableAsync(layer, cancellation)
            .ConfigureAwait(false);

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

            // <b>ADR-064 condition 3: a role is refused here, on a column that cannot hold it,
            // rather than discovered at the first edit.</b> A write that fails on a bad role
            // fails for whoever edits next, for a reason nobody wrote down when it was set.
            EditRole tracks = EditRole.None;

            if (!string.IsNullOrWhiteSpace(entry.Tracks))
            {
                if (!Enum.TryParse(entry.Tracks.Trim(), ignoreCase: true, out tracks)
                    || !Enum.IsDefined(tracks)
                    || tracks == EditRole.None)
                {
                    await Refuse(
                        context, 400,
                        $"'{entry.Tracks}' is not something a column can record. A column records "
                        + "one of: creator, created, editor, edited.")
                        .ConfigureAwait(false);
                    return;
                }

                if (!roles.Add(tracks))
                {
                    await Refuse(
                        context, 400,
                        $"Two columns are given the role '{entry.Tracks.Trim().ToLowerInvariant()}'. "
                        + "Each role is recorded in one column, or the server would not know "
                        + "which to write.")
                        .ConfigureAwait(false);
                    return;
                }

                if (entry.Hidden)
                {
                    await Refuse(
                        context, 400,
                        $"'{column}' records {Recorded(tracks)}, so it cannot be hidden: clients read "
                        + "who changed a feature from it, and editing on this layer depends on it.")
                        .ConfigureAwait(false);
                    return;
                }

                if (table.Find(column) is not { } field)
                {
                    await Refuse(
                        context, 400,
                        $"'{column}' is not a column of this layer's table, so it cannot record "
                        + $"{Recorded(tracks)}.")
                        .ConfigureAwait(false);
                    return;
                }

                bool holdsName = tracks is EditRole.Creator or EditRole.Editor;

                if (field.Type != (holdsName ? FieldType.Text : FieldType.Date))
                {
                    await Refuse(
                        context, 400,
                        holdsName
                            ? $"'{column}' is not a text column, so it cannot hold an account's name."
                            : $"'{column}' is not a date column, so it cannot hold {Recorded(tracks)}.")
                        .ConfigureAwait(false);
                    return;
                }
            }

            wanted.Add(new FieldOverride(column, alias, entry.Hidden, tracks));
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
    /// id; a document without either offers features nobody can ask for again. <b>Editor-tracking
    /// columns are locked too, since ADR-064</b> — not here, because a role is a property of the
    /// overrides rather than of the definition: the write refuses a hidden tracked column where
    /// it reads the role, and the answer marks one locked beside its hide box.
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

                    // ADR-064: what the column records about edits, or null.
                    tracks = Role(said?.Tracks ?? EditRole.None),

                    // <b>A tracked column is locked like an identity column</b>, and the Fields
                    // page draws its hide box disabled with this sentence beside it.
                    locked = Unhideable(layer, f.Name)
                        ?? (said?.Tracks is { } role && role != EditRole.None
                            // Two sentences, because the page shows the first under the box and
                            // keeps the whole for its tooltip — a design review found one long
                            // sentence running to nine lines in a narrow column.
                            ? $"it records {Recorded(role)}. Clients read it, and editing on this "
                              + "layer depends on it."
                            : null),
                };
            }),

            // <b>ADR-063 condition 3: inert is the design, visible is the condition.</b> An
            // override that names a column this table does not have changes nothing — and an
            // operator who renamed a column and lost its label has to be able to see why.
            inert = inert.Select(o => new
            {
                column = o.Column, alias = o.Alias, hidden = o.Hidden, tracks = Role(o.Tracks),
            }),

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
    /// <param name="Column">The column it is about.</param>
    /// <param name="Alias">Its label, or null.</param>
    /// <param name="Hidden">Whether every face refuses it.</param>
    /// <param name="Tracks">
    /// What it records about edits — <c>creator</c>, <c>created</c>, <c>editor</c> or
    /// <c>edited</c> — or null. ADR-064. Last and optional, so a body written before editor
    /// tracking existed means what it meant.
    /// </param>
    internal sealed record FieldOverrideEntry(
        [property: JsonPropertyName("column")] string? Column,
        [property: JsonPropertyName("alias")] string? Alias,
        [property: JsonPropertyName("hidden")] bool Hidden,
        [property: JsonPropertyName("tracks")] string? Tracks = null);

    /// <summary>A role's wire name, or null for none — the word the request takes back.</summary>
    private static string? Role(EditRole role) =>
        role == EditRole.None ? null : role.ToString().ToLowerInvariant();

    /// <summary>What a role records, in the words a refusal uses.</summary>
    private static string Recorded(EditRole role) => role switch
    {
        EditRole.Creator => "who created each feature",
        EditRole.Created => "when each feature was created",
        EditRole.Editor => "who last changed each feature",
        EditRole.Edited => "when each feature was last changed",

        // enum-default-is-deliberate: None records nothing, and no caller asks for it.
        _ => "nothing",
    };
}
