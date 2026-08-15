using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Catalog;
using Npgsql;

namespace GisServer.Platform.Postgres;

/// <summary>
/// What the catalogue answered, and whether the store was there to answer it.
/// </summary>
/// <param name="Service">The service, or null if there is no such service — or,
/// when <paramref name="Blind"/>, no memory of one.</param>
/// <param name="Blind">True when the platform store could not be reached and
/// this answer came from the last one it gave.</param>
/// <param name="Age">How old that memory is. Zero when not blind.</param>
public readonly record struct CatalogAnswer(PublishedService? Service, bool Blind, TimeSpan Age);

/// <summary>
/// The catalogue, plus the last answer it gave, for when it cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Q-95, answered by the owner 2026-08-15: serve public-only while blind.</b>
/// Every feature request reads the catalogue, so a platform-store outage stopped
/// the server answering even layers it had served a thousand times — the
/// isolation <see href="ADR-019">ADR-019</see> spent when it fused the tiers.
/// This buys it back for the only data where a stale grant costs nothing.
/// </para>
/// <para>
/// <b>The healthy path is unchanged, and that is the point of the design.</b>
/// This is not a read-through cache: while the store answers, every request
/// reads it, exactly as before, and the remembered copy is only refreshed as a
/// side effect. So revocation stays instant, a stopped service stops instantly,
/// and none of the reasoning in <see cref="PostgresLayerCatalog"/> changes. The
/// memory is consulted only when the store cannot be reached at all.
/// </para>
/// <para>
/// <b>Only connectivity failures fall back.</b> A malformed query, a missing
/// column, a permission error — anything the server answered — is a bug and is
/// rethrown. Serving stale data because of a defect in our own SQL would hide
/// the defect behind a degraded mode that looks deliberate.
/// </para>
/// <para>
/// <b>And the memory expires.</b> An entry refreshes on every successful read,
/// so its age is roughly how long the store has been unreachable. Capping that
/// age caps how long degraded serving lasts: an outage longer than the window is
/// one somebody is handling, and indefinite stale serving is how a layer
/// somebody decommissioned stays online for a week.
/// </para>
/// </remarks>
public sealed class CatalogFallback
{
    /// <summary>
    /// How long a remembered entry may be served after the store goes quiet.
    /// </summary>
    /// <remarks>
    /// <b>Fifteen minutes, and the number is a judgement rather than a
    /// measurement.</b> Long enough to cover a restart, a failover or a
    /// connection storm without anybody noticing; short enough that a real
    /// outage stops being papered over while somebody is still reading the first
    /// alert. It is configurable because the right value depends on how fast the
    /// deployment's operators actually respond, which is not a thing this
    /// project can know.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many services are remembered.
    /// </summary>
    /// <remarks>
    /// The scale target is 100–1,000 services (`CLAUDE.md` §7) and an entry is
    /// small, so this is a runaway guard rather than a tuning parameter. When it
    /// is hit the whole memory is dropped rather than evicted cleverly: a
    /// deployment past this number has a different problem, and an LRU here
    /// would be machinery serving a case nobody has.
    /// </remarks>
    public const int Capacity = 4096;

    /// <summary>
    /// The catalogue underneath, for reads this class does not gate.
    /// </summary>
    /// <remarks>
    /// <b>Exposed rather than proxied, because the fallback is not universal.</b>
    /// Only the service lookup on the serving path has a defined answer while
    /// blind; a relationship read or an administrative list does not, and
    /// wrapping them all would imply it did. Null when this instance was built
    /// over a delegate, which is how the policy is tested.
    /// </remarks>
    public PostgresLayerCatalog? Catalog { get; }

    private readonly Func<string?, string, CancellationToken, Task<PublishedService?>> _read;
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;

    private readonly ConcurrentDictionary<(string Folder, string Name),
        (PublishedService Service, long Stamp)> _last = new();

    /// <summary>Wraps the real catalogue.</summary>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="time">The clock.</param>
    /// <param name="window">How long a remembered entry may be served.</param>
    public CatalogFallback(
        PostgresLayerCatalog catalog, TimeProvider time, TimeSpan? window = null)
        : this(
            (folder, name, token) =>
                (catalog ?? throw new ArgumentNullException(nameof(catalog)))
                    .FindServiceAsync(folder, name, token),
            time,
            window)
    {
        Catalog = catalog;
    }

    /// <summary>Wraps any catalogue read, which is how the policy is tested.</summary>
    /// <param name="read">The read.</param>
    /// <param name="time">The clock.</param>
    /// <param name="window">How long a remembered entry may be served.</param>
    public CatalogFallback(
        Func<string?, string, CancellationToken, Task<PublishedService?>> read,
        TimeProvider time,
        TimeSpan? window = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _window = window ?? DefaultWindow;
    }

    /// <summary>The service, and whether the store was reachable.</summary>
    /// <param name="folder">The folder, or null for the root.</param>
    /// <param name="name">The service name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was found, and how.</returns>
    public async Task<CatalogAnswer> FindServiceAsync(
        string? folder, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        (string, string) key = (folder ?? string.Empty, name.ToLowerInvariant());

        try
        {
            PublishedService? service = await _read(folder, name, cancellationToken)
                .ConfigureAwait(false);

            if (service is null)
            {
                // <b>Forgotten, not kept.</b> A service that has been deleted
                // must not come back during the next outage, and a rename is a
                // delete as far as this key is concerned.
                _last.TryRemove(key, out _);
            }
            else
            {
                Remember(key, service);
            }

            return new CatalogAnswer(service, Blind: false, Age: TimeSpan.Zero);
        }
        catch (Exception e) when (IsUnreachable(e))
        {
            if (!_last.TryGetValue(key, out (PublishedService Service, long Stamp) remembered))
            {
                return new CatalogAnswer(null, Blind: true, Age: TimeSpan.Zero);
            }

            TimeSpan age = _time.GetElapsedTime(remembered.Stamp);

            return age > _window
                ? new CatalogAnswer(null, Blind: true, Age: age)
                : new CatalogAnswer(remembered.Service, Blind: true, Age: age);
        }
    }

    private void Remember((string, string) key, PublishedService service)
    {
        if (_last.Count >= Capacity && !_last.ContainsKey(key))
        {
            _last.Clear();
        }

        _last[key] = (service, _time.GetTimestamp());
    }

    /// <summary>
    /// Whether this failure means the store is not there, as opposed to us
    /// having asked it something stupid.
    /// </summary>
    /// <param name="e">The failure.</param>
    /// <returns>True when the store could not be reached.</returns>
    /// <remarks>
    /// <para>
    /// <b>The distinction is the whole safety of this class.</b> Falling back on
    /// any exception would mean a typo in our own SQL quietly switches the
    /// server into a mode that serves remembered authorization — and it would
    /// look like a designed degradation rather than the bug it is.
    /// </para>
    /// <para>
    /// So: anything Npgsql raised before the server answered counts, and
    /// anything the server itself answered does not — except the three classes
    /// where the server's answer *is* "I am going away". 57P01, 57P02 and 57P03
    /// are shutdown and cannot-connect-now; 53300 is too many connections, which
    /// is a store that exists and cannot serve us, which is the same thing from
    /// here.
    /// </para>
    /// </remarks>
    public static bool IsUnreachable(Exception e) => e switch
    {
        TimeoutException => true,
        PostgresException postgres => postgres.SqlState is "57P01" or "57P02" or "57P03" or "53300",
        NpgsqlException => true,
        _ => false,
    };
}
