using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

            // <b>ADR-065: a domain is refused here, on a column it cannot govern</b>, for the reason
            // a role is: a domain that fits no value is found by whoever edits next, and a domain on
            // an identity would refuse the value the database assigns.
            FieldDomain? domain = null;

            if (entry.Domain is { ValueKind: not JsonValueKind.Null } given)
            {
                domain = FieldDomainJson.ReadDomain(given, out string? unreadable);

                if (domain is null)
                {
                    await Refuse(context, 400, $"The domain given to '{column}' cannot be read: {unreadable}")
                        .ConfigureAwait(false);
                    return;
                }

                if (Unhideable(layer, column) is { } identity)
                {
                    await Refuse(context, 400, $"'{column}' cannot have a domain: {identity}")
                        .ConfigureAwait(false);
                    return;
                }

                if (tracks != EditRole.None)
                {
                    await Refuse(
                        context, 400,
                        $"'{column}' records {Recorded(tracks)}, which this server writes and a client "
                        + "never does, so a domain on it would govern nothing anybody sends.")
                        .ConfigureAwait(false);
                    return;
                }

                // <b>A column the table does not have keeps its domain unjudged</b> — ADR-063's
                // drift answer: it is what an operator's earlier save said about a column that has
                // since gone, carried back by the Fields page, and it governs nothing until a column
                // of a type it fits appears under that name.
                if (table.Find(column) is { } target
                    && DomainRules.Refuse(domain, target.Type, target.MaxLength) is { } unfit)
                {
                    // <b>A domain named after its column is called by its kind</b>, or the sentence
                    // quotes one word as two things — design review 2026-09-12, *"'diameter' cannot
                    // take the domain 'diameter'"*, which is what the Fields page's default name gives.
                    string called = string.Equals(domain.Name, column, StringComparison.OrdinalIgnoreCase)
                        ? (domain.Kind == DomainKind.Range ? "this range" : "this list")
                        : $"the domain '{domain.Name}'";

                    await Refuse(context, 400, $"'{column}' cannot take {called}: {unfit}")
                        .ConfigureAwait(false);
                    return;
                }
            }

            // Subtypes on an entry are the ones a dropped column left behind, carried back unchanged;
            // the live set is the request's own `subtypes`, below.
            LayerSubtypes? leftBehind = null;

            if (entry.Subtypes is { ValueKind: not JsonValueKind.Null } kept)
            {
                if (table.Find(column) is not null)
                {
                    await Refuse(
                        context, 400,
                        $"Subtypes are given on the override for '{column}'. The subtypes of a column this "
                        + "table has are the request's own `subtypes`, which names the column as field; "
                        + "an override carries them only for a column that has gone.")
                        .ConfigureAwait(false);
                    return;
                }

                leftBehind = FieldDomainJson.ReadSubtypes(column, kept, out string? unreadable);

                if (leftBehind is null)
                {
                    await Refuse(context, 400, $"The subtypes kept for '{column}' cannot be read: {unreadable}")
                        .ConfigureAwait(false);
                    return;
                }
            }

            wanted.Add(new FieldOverride(column, alias, entry.Hidden, tracks, domain, leftBehind));
        }

        // <b>ADR-065: the layer's subtypes, judged against the whole request</b> — a subtype's
        // default is checked against the domain the same request gives its column, and the subtype
        // column may be neither hidden nor tracked by the same save that names it.
        if (request.Subtypes is { ValueKind: not JsonValueKind.Null } declared)
        {
            string field = declared.ValueKind == JsonValueKind.Object
                && declared.TryGetProperty("field", out JsonElement named)
                && named.ValueKind == JsonValueKind.String
                    ? named.GetString()!.Trim()
                    : string.Empty;

            if (field.Length == 0)
            {
                await Refuse(
                    context, 400,
                    "Subtypes name the column that holds their codes, as field. Send null to have none.")
                    .ConfigureAwait(false);
                return;
            }

            if (FieldDomainJson.ReadSubtypes(field, declared, out string? unreadable) is not { } subtypes)
            {
                await Refuse(context, 400, $"The subtypes cannot be read: {unreadable}").ConfigureAwait(false);
                return;
            }

            int at = wanted.FindIndex(o => o.Matches(field));
            FieldOverride? onField = at >= 0 ? wanted[at] : null;

            if (onField is { Domain: { } clash })
            {
                await Refuse(
                    context, 400,
                    $"'{field}' is the subtype column, whose values are the subtype codes, and it is also "
                    + $"given the domain '{clash.Name}'. The subtypes are its domain; remove the other.")
                    .ConfigureAwait(false);
                return;
            }

            string? Locked(string name)
            {
                if (Unhideable(layer, name) is { } identity)
                {
                    return identity;
                }

                FieldOverride? said = wanted.FirstOrDefault(o => o.Matches(name)) is { Column: not null } found
                    ? found
                    : null;

                if (said is { Tracks: not EditRole.None } tracked)
                {
                    return $"it records {Recorded(tracked.Tracks)}, which this server writes.";
                }

                return said is { Hidden: true }
                    ? "it is hidden, and a template would put it in front of every client that creates a feature."
                    : null;
            }

            if (DomainRules.Refuse(
                    subtypes,
                    table,
                    Locked,
                    name => wanted.FirstOrDefault(o => o.Matches(name)).Domain) is { } refused)
            {
                await Refuse(context, 400, $"The subtypes cannot be stored: {refused}").ConfigureAwait(false);
                return;
            }

            if (onField is { } existing)
            {
                wanted[at] = existing with { Subtypes = subtypes };
            }
            else
            {
                wanted.Add(new FieldOverride(field, null, Hidden: false, Subtypes: subtypes));
            }
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
                domains = stored.Where(o => o.Domain is not null).Select(o => o.Column).ToArray(),
                subtypes = stored.FirstOrDefault(o => o.Subtypes is not null && table.Find(o.Column) is not null).Column,
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

                    // ADR-065: what values it may hold, in the ArcGIS domain object's shape, or null.
                    domain = said?.Domain is { } domain ? FieldDomainJson.Write(domain) : null,

                    // <b>What the Fields page may offer, from the rules that judge what it sends</b>:
                    // the kinds of domain this column can take, and whether it can hold subtype codes.
                    // Empty and false for an identity. A role or a hidden box set on the page rules
                    // a column out as well, and the page reads those from its own controls.
                    domainKinds = Unhideable(layer, f.Name) is null
                        ? DomainRules.KindsFor(f.Type).Select(k => k == DomainKind.Range ? "range" : "codedValue")
                        : [],
                    holdsSubtypes = Unhideable(layer, f.Name) is null && DomainRules.HoldsSubtypes(f.Type),

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
            // <b>ADR-065: the layer's subtypes, naming their column</b> — the same object the request
            // takes back, so the Fields page sends what it was given. Null when the layer has none,
            // or when the column that held them has gone, in which case they are in `inert`.
            subtypes = overrides.FirstOrDefault(o => o.Subtypes is not null && table.Find(o.Column) is not null)
                is { Subtypes: { } live }
                    ? FieldDomainJson.Write(live, withField: true)
                    : null,

            inert = inert.Select(o => new
            {
                column = o.Column,
                alias = o.Alias,
                hidden = o.Hidden,
                tracks = Role(o.Tracks),
                domain = o.Domain is { } domain ? FieldDomainJson.Write(domain) : null,
                subtypes = o.Subtypes is { } left ? FieldDomainJson.Write(left, withField: false) : null,
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
    /// <param name="Subtypes">
    /// The layer's subtypes — ADR-065 — as <c>{field, defaultCode, types}</c>, or null for none.
    /// Replaced with the list, like everything else in this body: a save without it leaves the
    /// layer with no subtypes.
    /// </param>
    internal sealed record FieldOverridesRequest(
        [property: JsonPropertyName("overrides")] IReadOnlyList<FieldOverrideEntry>? Overrides,
        [property: JsonPropertyName("subtypes")] JsonElement? Subtypes = null);

    /// <summary>One entry of <see cref="FieldOverridesRequest"/>.</summary>
    /// <param name="Column">The column it is about.</param>
    /// <param name="Alias">Its label, or null.</param>
    /// <param name="Hidden">Whether every face refuses it.</param>
    /// <param name="Tracks">
    /// What it records about edits — <c>creator</c>, <c>created</c>, <c>editor</c> or
    /// <c>edited</c> — or null. ADR-064. Last and optional, so a body written before editor
    /// tracking existed means what it meant.
    /// </param>
    /// <param name="Domain">
    /// What values it may hold, as an ArcGIS domain object, or null — ADR-065. Optional for the
    /// reason <paramref name="Tracks"/> is.
    /// </param>
    /// <param name="Subtypes">
    /// Subtypes a dropped column left behind, as the answer's <c>inert</c> gave them, or null.
    /// </param>
    internal sealed record FieldOverrideEntry(
        [property: JsonPropertyName("column")] string? Column,
        [property: JsonPropertyName("alias")] string? Alias,
        [property: JsonPropertyName("hidden")] bool Hidden,
        [property: JsonPropertyName("tracks")] string? Tracks = null,
        [property: JsonPropertyName("domain")] JsonElement? Domain = null,
        [property: JsonPropertyName("subtypes")] JsonElement? Subtypes = null);

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
