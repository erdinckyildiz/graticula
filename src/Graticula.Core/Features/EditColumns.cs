using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Catalog;
using Graticula.Geometries;

namespace Graticula.Features;

/// <summary>
/// The rules every feature writer applies to what an edit sends, before any engine sees it — the column whitelist
/// (ADR-008 §4.6), domains and subtypes (ADR-065, ADR-013 condition 5), the columns this server writes itself
/// (ADR-064) and the GlobalID — and the refusal of a geometry whose ordinates the store would lose (ADR-077).
/// </summary>
/// <remarks>
/// <b>Moved out of <c>PostGisFeatureWriter</c> on 2026-10-20 for ADR-205 phase 2</b>, so the Oracle writer refuses
/// the same values with the same sentences by running the same code, rather than a copy that could drift. Nothing
/// here talks to a database: what a writer reads from its own store — a row's subtype, the values a subtype move would
/// keep — it reads itself and hands in.
/// </remarks>
public sealed class EditColumns
{
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<FieldDescription> _fields;
    private readonly LayerSubtypes? _subtypes;

    /// <summary>The rules for one layer.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="fields">Its real columns, from the source's describe — the whitelist.</param>
    /// <param name="subtypes">Its subtypes, or null.</param>
    public EditColumns(LayerDefinition layer, IReadOnlyList<FieldDescription> fields, LayerSubtypes? subtypes)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(fields);

        _layer = layer;
        _fields = fields;
        _subtypes = subtypes;
        GlobalId = GlobalIds.FieldOf(fields);
    }

    /// <summary>The layer's GlobalID column, which the server fills, or null.</summary>
    public string? GlobalId { get; }

    /// <summary>
    /// Checks every column against the layer's real columns, and every value against the domain that governs it.
    /// </summary>
    /// <param name="attributes">What the edit sends.</param>
    /// <param name="storedSubtype">The subtype the feature already is, when the writer read it; null for an add.</param>
    /// <param name="bound">The columns and values to write, in the order sent.</param>
    /// <param name="error">Why the edit is refused, when it is.</param>
    /// <param name="keepGlobalId">Whether a GlobalID the client minted is kept — an add under <c>useGlobalIds=true</c>.</param>
    /// <returns>Whether the edit may be written.</returns>
    /// <remarks>
    /// <para>
    /// ADR-008 §4.6. A column name cannot be a parameter, so it is compared against the list the database gave us
    /// and refused if absent. The object id and the geometry column are refused too: the first is assigned by the
    /// database and the second has its own handling.
    /// </para>
    /// <para>
    /// <b>ADR-065 and ADR-013 condition 5: a domain the document reports is a domain this refuses a value outside</b>
    /// — the subtype sent in the same edit, or the one the feature already is, and the column's own otherwise.
    /// <b>What is checked is what is written</b>, not the row it lands in; a subtype move that keeps a value the new
    /// subtype disallows is <see cref="SubtypeMove"/>'s.
    /// </para>
    /// </remarks>
    public bool TryBind(
        IReadOnlyDictionary<string, object?> attributes,
        long? storedSubtype,
        out List<(string Column, object? Value)> bound,
        out string? error,
        bool keepGlobalId = false)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        bound = [];
        error = null;

        // The subtype this feature will be: the one sent, when one is, and the one it is otherwise.
        long? subtype = storedSubtype;

        if (_subtypes is { } declared && attributes.TryGetValue(declared.Field, out object? sent))
        {
            subtype = DomainRules.TryCode(sent, out long code) ? code : null;
        }

        foreach (KeyValuePair<string, object?> attribute in attributes)
        {
            if (string.Equals(attribute.Key, _layer.IntegerIdentityColumn, StringComparison.Ordinal))
            {
                // Silently ignored rather than refused. Every ArcGIS client round-trips the object id in the
                // attributes of an update, and refusing would fail every well-behaved edit.
                continue;
            }

            if (string.Equals(attribute.Key, _layer.GeometryColumn, StringComparison.Ordinal))
            {
                error =
                    $"'{attribute.Key}' is the geometry column and cannot be set as an attribute. "
                    + "Send the shape in the feature's 'geometry' member.";
                return false;
            }

            FieldDescription? field = null;

            foreach (FieldDescription candidate in _fields)
            {
                if (string.Equals(candidate.Name, attribute.Key, StringComparison.Ordinal))
                {
                    field = candidate;
                    break;
                }
            }

            if (field is null)
            {
                error =
                    $"'{attribute.Key}' is not a column of this layer. Columns are taken from the "
                    + "database rather than the request, so a name that is not there cannot be "
                    + "written.";
                return false;
            }

            // <b>A tracked column is dropped, not refused — ADR-064.</b> The document says it is not editable, and an
            // ArcGIS web client echoes every attribute back on an update anyway. This server writes the column itself.
            if (field.Value.Maintained)
            {
                continue;
            }

            // <b>The GlobalID is this server's to write, like a tracked column</b> — dropped rather than refused,
            // because a client echoes it back on every update. <b>Except on an add under `useGlobalIds=true`</b>,
            // where the client minted it and will refer to the feature by it.
            if (string.Equals(attribute.Key, GlobalId, StringComparison.Ordinal)
                && !(keepGlobalId && attribute.Value is Guid))
            {
                continue;
            }

            if (_subtypes is { } subtypes
                && string.Equals(attribute.Key, subtypes.Field, StringComparison.Ordinal))
            {
                if (DomainRules.SubtypeRefusal(subtypes, attribute.Value) is { } notASubtype)
                {
                    error = notASubtype;
                    return false;
                }
            }
            else
            {
                FieldDomain? governing = _subtypes?.DomainFor(attribute.Key, subtype, field.Value.Domain)
                    ?? field.Value.Domain;

                // The subtype is named in the refusal only when the domain is the subtype's, so a value refused by
                // the column's own domain is not blamed on the feature's kind.
                Subtype? whose = governing is not null
                    && !ReferenceEquals(governing, field.Value.Domain)
                    && subtype is { } kind
                        ? _subtypes!.Find(kind)
                        : null;

                if (DomainRules.Refusal(field.Value, attribute.Value, governing, whose) is { } outside)
                {
                    error = outside;
                    return false;
                }
            }

            bound.Add((attribute.Key, attribute.Value));
        }

        return true;
    }

    /// <summary>
    /// The updates whose feature's stored subtype a writer must read: they write a column some subtype governs and do
    /// not say which subtype the feature is.
    /// </summary>
    /// <param name="updates">The batch's updates.</param>
    /// <returns>Their identities, once each; empty when the layer has no subtype that gives a column its own domain.</returns>
    public long[] NeedingStoredSubtype(IReadOnlyList<FeatureUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        return _subtypes is not { } subtypes
            ? []
            : [.. updates
                .Where(u => !u.Attributes.ContainsKey(subtypes.Field) && u.Attributes.Keys.Any(subtypes.Varies))
                .Select(u => u.Identity)
                .Distinct()];
    }

    /// <summary>The layer's subtype column, or null.</summary>
    public string? SubtypeField => _subtypes?.Field;

    /// <summary>
    /// When an update moves a feature to another subtype, the subtype and the governed columns it does not send —
    /// the values a writer must read to judge the move; null when there is nothing to judge.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <returns>The target subtype, its code, and the columns to read; or null.</returns>
    public (Subtype Target, long Code, FieldDescription[] Unsent)? SubtypeMove(FeatureUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (_subtypes is not { } subtypes
            || !update.Attributes.TryGetValue(subtypes.Field, out object? sent)
            || !DomainRules.TryCode(sent, out long code)
            || subtypes.Find(code) is not { } target)
        {
            return null;
        }

        FieldDescription[] unsent = [.. _fields.Where(f =>
            target.Domains.ContainsKey(f.Name) && !update.Attributes.ContainsKey(f.Name))];

        return unsent.Length == 0 ? null : (target, code, unsent);
    }

    /// <summary>
    /// Why a value an update keeps is outside the subtype it moves the feature to — Q-152 — or null.
    /// </summary>
    /// <param name="target">The subtype moved to.</param>
    /// <param name="field">The kept column.</param>
    /// <param name="stored">Its stored value.</param>
    /// <returns>The refusal, naming the field to send; or null.</returns>
    public static string? SubtypeMoveRefusal(Subtype target, FieldDescription field, object? stored)
    {
        ArgumentNullException.ThrowIfNull(target);

        return DomainRules.Refusal(field, stored, target.Domains[field.Name], target) is { } refusal
            ? $"{refusal} This edit moves the feature to subtype {target.Code} ({target.Name}) and leaves "
              + $"'{field.Name}' as it was, which the new subtype does not allow. Send '{field.Name}' "
              + "with a value it allows, in the same edit."
            : null;
    }

    /// <summary>
    /// Why a geometry may not be written where <paramref name="stores"/> is kept, or null when it may — ADR-077.
    /// </summary>
    /// <param name="sent">The geometry the edit carries.</param>
    /// <param name="stores">What the row or column holds.</param>
    /// <param name="where">What holds it, for the sentence.</param>
    /// <returns>The refusal, or null.</returns>
    /// <remarks>
    /// <b>Both directions are losses, so both are refused.</b> A flat shape over an elevation discards the elevation;
    /// an elevation into a flat column is discarded by the column. Padding a missing Z with zero is not an option
    /// either: zero metres is a value, and nobody could tell it from a measured one (ADR-077 §10).
    /// </remarks>
    public static string? DimensionRefusal(Geometry sent, GeometryOrdinates stores, string where)
    {
        ArgumentNullException.ThrowIfNull(sent);
        ArgumentException.ThrowIfNullOrEmpty(where);

        GeometryOrdinates carried = Ordinates.Of(sent);

        if (carried == stores)
        {
            return null;
        }

        GeometryOrdinates missing = stores & ~carried;

        if (missing != GeometryOrdinates.None)
        {
            return $"{char.ToUpperInvariant(where[0])}{where[1..]} carries {Ordinates.Name(missing)}, and the "
                + "geometry sent has none. Writing it would discard the stored value or invent one, so the edit "
                + $"is refused. Send the geometry with {(missing == (GeometryOrdinates.Z | GeometryOrdinates.M) ? "them" : "it")} "
                + "— hasZ / hasM and a value on every vertex — or update the attributes alone (ADR-077).";
        }

        GeometryOrdinates extra = carried & ~stores;

        return $"The geometry sent carries {Ordinates.Name(extra)}, and {where} stores "
            + $"{Ordinates.Name(stores) ?? "x and y only"}. Storing it would discard "
            + $"{(extra == (GeometryOrdinates.Z | GeometryOrdinates.M) ? "them" : "it")}, so the edit is refused (ADR-077).";
    }

    /// <summary>The account a row's creator must be for this batch to change it, or null for any row — ADR-064.</summary>
    /// <param name="batch">The batch.</param>
    /// <returns>The editor's name when the batch is own-only; null otherwise.</returns>
    public static string? OwnerOf(EditBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.OwnOnly ? batch.Editor ?? string.Empty : null;
    }

    /// <summary>The refusal for a row whose creator is not the caller, in the words every writer uses — ADR-064.</summary>
    /// <param name="identity">The row.</param>
    /// <param name="recordsCreator">Whether the layer records a creator at all.</param>
    /// <param name="creator">The row's recorded creator, when it has one.</param>
    /// <returns>The refusal.</returns>
    public static EditResult NotOwned(long identity, bool recordsCreator, string? creator) =>
        !recordsCreator
            ? EditResult.NotOwned(
                identity,
                "This layer does not record who created its features, so none of them is yours to "
                + "change. Changing them needs features:fullEdit.")
            : creator is null
                ? EditResult.NotOwned(
                    identity,
                    $"Feature {identity} has no creator recorded, so it is nobody's own. Changing it "
                    + "needs features:fullEdit.")
                : EditResult.NotOwned(
                    identity,
                    $"Feature {identity} was created by somebody else. Changing another account's "
                    + "feature needs features:fullEdit.");
}
