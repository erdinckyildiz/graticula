using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Platform.Admin;

namespace Graticula.Host;

/// <summary>
/// The external origins an administrator allows a style to fetch from — ADR-094.
/// </summary>
/// <remarks>
/// <para>
/// <b>One named value in <c>server_setting</c>, not a table.</b> Migration 54 made that table so the
/// next server-wide setting an operator changes from the console could arrive without a migration
/// (ADR-017 §5c: runtime state goes through the API, only bootstrap lives in configuration). The
/// value is a JSON array of origins in their normalised spelling.
/// </para>
/// <para>
/// <b>Read in three places, held for thirty seconds.</b> Storing a style checks against it, serving
/// one checks again, and every console page's Content-Security-Policy is built from it — the last
/// is every page load, which is why it is held rather than read per request, as the page size is. A
/// write on this node replaces what is held at once; another node catches up within the thirty
/// seconds, so an origin taken off the list stops being served everywhere within that window.
/// </para>
/// <para>
/// <b>An unreadable store keeps what was held, and an empty list if nothing was.</b> Failing closed
/// is the right direction for a list whose every entry widens what a browser may be sent to: a style
/// that needs an origin the server cannot confirm is served as the generated style, which is what
/// an origin removed from the list does anyway.
/// </para>
/// </remarks>
internal sealed class StyleOriginList
{
    /// <summary>The setting's name in the store.</summary>
    public const string Name = "style_origins";

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    private readonly IServerSettingStore _store;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private (Reading Reading, DateTimeOffset At)? _held;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">Where the list is kept.</param>
    /// <param name="clock">What now is.</param>
    public StyleOriginList(IServerSettingStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The list, and when it was last changed.</summary>
    /// <param name="Origins">The allowed origins; empty when nobody allowed any.</param>
    /// <param name="ChangedAt">When it was set, or null.</param>
    internal sealed record Reading(IReadOnlyList<StyleOrigin> Origins, DateTimeOffset? ChangedAt);

    /// <summary>The list in force, from what is held when it is fresh.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The origins.</returns>
    public async Task<IReadOnlyList<StyleOrigin>> CurrentAsync(CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (_held is { } held && _clock.GetUtcNow() - held.At < Fresh)
            {
                return held.Reading.Origins;
            }
        }

        try
        {
            return (await ReadAsync(cancellation).ConfigureAwait(false)).Origins;
        }
        catch (DbException)
        {
            lock (_gate)
            {
                return _held?.Reading.Origins ?? [];
            }
        }
    }

    /// <summary>Reads the list from the store, and holds it.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The reading.</returns>
    public async Task<Reading> ReadAsync(CancellationToken cancellation)
    {
        StoredSetting? stored = await _store.ReadAsync(Name, cancellation).ConfigureAwait(false);
        Reading reading = new(Parse(stored?.Value), stored?.ChangedAt);

        lock (_gate)
        {
            _held = (reading, _clock.GetUtcNow());
        }

        return reading;
    }

    /// <summary>Stores the list, or removes it when it is empty.</summary>
    /// <param name="origins">Origins that passed <see cref="StyleOrigins.TryParseList"/>.</param>
    /// <param name="changedBy">Who changed it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>What was stored before.</returns>
    public async Task<IReadOnlyList<StyleOrigin>> SetAsync(
        IReadOnlyList<StyleOrigin> origins, Guid? changedBy, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(origins);

        StoredSetting? before = await _store
            .WriteAsync(
                Name,
                origins.Count == 0 ? null : JsonSerializer.Serialize(origins.Select(o => o.Text)),
                changedBy,
                cancellation)
            .ConfigureAwait(false);

        await ReadAsync(cancellation).ConfigureAwait(false);

        return Parse(before?.Value);
    }

    /// <summary>
    /// Reads a stored value, dropping any entry today's rules refuse.
    /// </summary>
    /// <remarks>
    /// <b>Re-parsed, not trusted.</b> The value was checked when it was written, but the rules can
    /// tighten between releases, and a list that widened a browser's reach under yesterday's rules
    /// should not keep doing so under today's.
    /// </remarks>
    private static StyleOrigin[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        string[] entries;

        try
        {
            entries = JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

        List<StyleOrigin> origins = [];

        foreach (string entry in entries)
        {
            if (StyleOrigins.TryParse(entry, out StyleOrigin? origin, out _) && !origins.Contains(origin!))
            {
                origins.Add(origin!);
            }
        }

        return [.. origins.Take(StyleOrigins.MostOrigins)];
    }
}
