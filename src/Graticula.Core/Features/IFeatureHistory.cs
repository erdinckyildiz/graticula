using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;

namespace Graticula.Features;

/// <summary>
/// A layer's history, kept by its database — ADR-078 for a hosted layer, ADR-207 for a registered one.
/// </summary>
/// <remarks>
/// <b>An interface since 2026-10-20</b>, when a registered Oracle layer was given a history: until then the one
/// implementation was PostgreSQL's trigger and the faces named it.
/// </remarks>
public interface IFeatureHistory
{
    /// <summary>Whether this layer keeps its history: the table is there and the trigger is on it.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether history is on.</returns>
    Task<bool> IsOnAsync(CancellationToken cancellationToken);

    /// <summary>How this layer's history is kept, or null when it keeps none.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The state, or null.</returns>
    Task<HistoryKept?> KeptAsync(CancellationToken cancellationToken);

    /// <summary>Turns history on: makes the table and the trigger, and records every feature as it is now.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many features the history begins with.</returns>
    Task<long> EnableAsync(CancellationToken cancellationToken);

    /// <summary>Turns history off: removes the trigger and the history with it.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DisableAsync(CancellationToken cancellationToken);

    /// <summary>Keeps this layer's history for so many days, or for ever.</summary>
    /// <param name="days">Days, or null for no limit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the layer keeps a history to set it on.</returns>
    Task<bool> SetKeepAsync(int? days, CancellationToken cancellationToken);

    /// <summary>The latest changes to the layer, newest first.</summary>
    /// <param name="before">Only changes before this moment, or null for the latest.</param>
    /// <param name="limit">How many.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The changes.</returns>
    Task<IReadOnlyList<FeatureChange>> ChangesAsync(DateTimeOffset? before, int limit, CancellationToken cancellationToken);

    /// <summary>One feature's versions, oldest first.</summary>
    /// <param name="objectId">The feature.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its versions; empty if it never had one.</returns>
    Task<IReadOnlyList<FeatureVersion>> VersionsAsync(long objectId, CancellationToken cancellationToken);

    /// <summary>Puts a feature back as one of its versions was — itself a new version.</summary>
    /// <param name="objectId">The feature.</param>
    /// <param name="historyId">The version to restore.</param>
    /// <param name="editor">Who is restoring it.</param>
    /// <param name="tracking">The layer's editor-tracking columns.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    Task<RestoreOutcome> RestoreAsync(
        long objectId, long historyId, string editor, EditorTracking? tracking, CancellationToken cancellationToken);
}

/// <summary>One change in a layer's history.</summary>
/// <param name="HistoryId">The version it opened or closed.</param>
/// <param name="ObjectId">The feature.</param>
/// <param name="At">When.</param>
/// <param name="Kind"><c>added</c>, <c>updated</c>, <c>restored</c>, <c>deleted</c> or <c>emptied</c>.</param>
/// <param name="Editor">Who: the account, or the database role when <paramref name="Direct"/>.</param>
/// <param name="Direct">Whether the write came straight to the database rather than through this server.</param>
public sealed record FeatureChange(long HistoryId, long ObjectId, DateTimeOffset At, string Kind, string Editor, bool Direct);

/// <summary>One version of a feature.</summary>
/// <param name="HistoryId">Its id.</param>
/// <param name="From">When it became true.</param>
/// <param name="To">When it stopped, or null while it is current.</param>
/// <param name="OpenedBy"><c>seed</c>, <c>insert</c> or <c>update</c>.</param>
/// <param name="ClosedBy"><c>update</c>, <c>delete</c>, <c>truncate</c>, or null while current.</param>
/// <param name="Editor">Who made it.</param>
/// <param name="Direct">Whether that write came straight to the database.</param>
/// <param name="EndedBy">Who ended it, or null.</param>
/// <param name="EndedDirect">Whether that write came straight to the database.</param>
/// <param name="Attributes">Its attributes, as stored.</param>
/// <param name="GeoJson">Its shape as GeoJSON, in the layer's reference, or null.</param>
public sealed record FeatureVersion(
    long HistoryId,
    DateTimeOffset From,
    DateTimeOffset? To,
    string OpenedBy,
    string? ClosedBy,
    string Editor,
    bool Direct,
    string? EndedBy,
    bool EndedDirect,
    JsonElement Attributes,
    string? GeoJson)
{
    /// <summary>The version a restore wrote back to make this one, or null when it was not a restore.</summary>
    public long? RestoredFrom { get; init; }
}

/// <summary>What a restore did.</summary>
public enum RestoreResult
{
    /// <summary>The feature existed and was written back as the version.</summary>
    Updated,

    /// <summary>The feature had been deleted and was written again under its old object id.</summary>
    Recreated,

    /// <summary>No such version of that feature.</summary>
    NoSuchVersion,
}

/// <summary>A restore's outcome.</summary>
/// <param name="Result">What happened.</param>
/// <param name="AttachmentsNotRestored">Whether a recreated feature came back without attachments it may have had.</param>
public sealed record RestoreOutcome(RestoreResult Result, bool AttachmentsNotRestored);

/// <summary>How a layer's history is kept — ADR-078.</summary>
/// <param name="Since">The earliest moment it answers for.</param>
/// <param name="KeepDays">How many days of ended versions are kept, or null for all of them.</param>
public sealed record HistoryKept(DateTimeOffset Since, int? KeepDays);
