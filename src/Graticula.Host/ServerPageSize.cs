using System;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;

namespace Graticula.Host;

/// <summary>
/// The server's page size: what a query naming none answers on a service that set none, and the number
/// that service's document gives as <c>maxRecordCount</c> — V-70, ADR-084.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three sources, in order.</b> The value an operator stored from the console; else
/// <c>Graticula:DefaultRecordCount</c>, which is what a deployment configured before there was a screen, so
/// none has to be reconfigured to keep its number; else 1000, ArcGIS Server's own default. Whichever it is,
/// it is never above <c>Graticula:MaximumRecordCount</c>, the deployment's ceiling, which nothing exceeds.
/// </para>
/// <para>
/// <b>Held for thirty seconds rather than read per request.</b> Every query needs it and it changes when an
/// operator presses Save. A write on this node replaces what is held at once; another node, if there is one,
/// catches up within the thirty seconds. When the store cannot be read, the last value held is kept, or the
/// configured one if there never was one — a page size is not a reason to refuse a query.
/// </para>
/// </remarks>
internal sealed class ServerPageSize
{
    /// <summary>The setting's name in the store.</summary>
    public const string Name = "page_size";

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    private readonly IServerSettingStore _store;
    private readonly HostSettings _settings;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private Reading? _held;

    /// <summary>Creates the reader.</summary>
    /// <param name="store">Where the operator's value is kept.</param>
    /// <param name="settings">The configured default and the ceiling.</param>
    /// <param name="clock">What now is.</param>
    public ServerPageSize(IServerSettingStore store, HostSettings settings, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>What the page size is, and where it came from.</summary>
    /// <param name="Value">The page size in force.</param>
    /// <param name="Stored">The operator's value when one is stored, else null.</param>
    /// <param name="Fallback">What it is when nothing is stored — the configured default or 1000.</param>
    /// <param name="Ceiling">The deployment's ceiling, which no page size exceeds.</param>
    /// <param name="ChangedAt">When the stored value was set, or null.</param>
    internal sealed record Reading(int Value, int? Stored, int Fallback, int Ceiling, DateTimeOffset? ChangedAt)
    {
        /// <summary>When this was read, for how long it is held.</summary>
        public DateTimeOffset ReadAt { get; init; }
    }

    /// <summary>The deployment's ceiling: the most any page may be.</summary>
    public int Ceiling => Math.Clamp(_settings.MaximumRecordCount, 1, Graticula.Features.FeatureQuery.MaximumLimit);

    /// <summary>The page size when nothing is stored.</summary>
    public int Fallback => Math.Clamp(_settings.DefaultRecordCount, 1, Ceiling);

    /// <summary>The page size in force, from the request's services.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The number.</returns>
    public static Task<int> OfAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken cancellation) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<ServerPageSize>(context.RequestServices)
            .CurrentAsync(cancellation);

    /// <summary>The page size in force.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The number.</returns>
    public async Task<int> CurrentAsync(CancellationToken cancellation) =>
        (await ReadAsync(cancellation).ConfigureAwait(false)).Value;

    /// <summary>The page size in force, with where it came from.</summary>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The reading.</returns>
    public async Task<Reading> ReadAsync(CancellationToken cancellation)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        lock (_gate)
        {
            if (_held is { } held && now - held.ReadAt < Fresh)
            {
                return held;
            }
        }

        StoredSetting? stored;

        try
        {
            stored = await _store.ReadAsync(Name, cancellation).ConfigureAwait(false);
        }
        catch (DbException)
        {
            lock (_gate)
            {
                return _held ?? Read(null, now);
            }
        }

        Reading reading = Read(stored, now);

        lock (_gate)
        {
            _held = reading;
        }

        return reading;
    }

    /// <summary>Why a value cannot be the page size, or null when it can.</summary>
    /// <param name="value">The value asked for.</param>
    /// <returns>The refusal, or null.</returns>
    public string? Refusal(int value) =>
        value < 1
            ? "A page size is at least 1: a query that may answer nothing is what switching Query off says."
            : value > Ceiling
                ? $"A page size of {value} is above this deployment's ceiling of {Ceiling} "
                  + "(Graticula:MaximumRecordCount), which no page may exceed. Raise the ceiling in the "
                  + "server's configuration first, or choose a smaller page."
                : null;

    /// <summary>Stores the operator's page size, or removes it when <paramref name="value"/> is null.</summary>
    /// <param name="value">The page size, or null for the configured one.</param>
    /// <param name="changedBy">Who changed it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>What was stored before, and what is in force now.</returns>
    public async Task<(StoredSetting? Before, Reading Now)> SetAsync(
        int? value, Guid? changedBy, CancellationToken cancellation)
    {
        if (value is { } asked && Refusal(asked) is { } refusal)
        {
            throw new ArgumentOutOfRangeException(nameof(value), asked, refusal);
        }

        StoredSetting? before = await _store
            .WriteAsync(Name, value?.ToString(CultureInfo.InvariantCulture), changedBy, cancellation)
            .ConfigureAwait(false);

        DateTimeOffset now = _clock.GetUtcNow();

        Reading reading = Read(
            value is { } set ? new StoredSetting(set.ToString(CultureInfo.InvariantCulture), now) : null, now);

        lock (_gate)
        {
            _held = reading;
        }

        return (before, reading);
    }

    private Reading Read(StoredSetting? stored, DateTimeOffset now)
    {
        // A stored value outside what this deployment allows — the ceiling lowered in configuration after
        // it was saved — is clamped here and still reported as what was stored, so the screen can say so.
        int? parsed = stored is not null
            && int.TryParse(stored.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;

        int value = parsed is { } p ? Math.Clamp(p, 1, Ceiling) : Fallback;

        return new Reading(value, parsed, Fallback, Ceiling, stored?.ChangedAt) { ReadAt = now };
    }
}
