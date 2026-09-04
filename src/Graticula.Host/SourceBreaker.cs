using System;
using System.Collections.Concurrent;
using System.Threading;
using Graticula.Platform.Postgres;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// A data source that has just failed to answer is not asked again for a moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8 asked for this as N3 —
/// *a circuit breaker per data source, with backoff* — and gave the reason: without one,
/// an outage becomes a connection storm at exactly the moment recovery is being
/// attempted.</b> It was the last of that section's four requirements still absent, and
/// [D-131](../../docs/architecture-debt.md) is what it cost.
/// </para>
/// <para>
/// <b>Measured 2026-08-23 with the platform database stopped: every refusal took 8.0
/// seconds, six times out of six.</b> Decomposed by path: <c>/healthz/live</c> answered in
/// 24 ms, <c>/rest/info</c> in 4.0 s, and everything that reads data in 8.0 s. So one
/// failed connect costs four seconds and a data request pays two — authentication resolves
/// a principal before every route, and the endpoint then reads. **The cost is not the
/// latency, it is that each refusal occupies a connection for the whole four seconds**, so
/// under load the outage's cost grows with traffic instead of staying flat. That is a queue
/// collapse rather than a degradation.
/// </para>
/// <para>
/// <b>Four seconds is not a timeout anybody configured</b>, which is worth saying because
/// the obvious repair is to lower one. A stopped container leaves Docker's port proxy
/// listening and swallowing the connection, so <c>connect</c> neither succeeds nor is
/// refused; a firewall that drops rather than rejects does the same thing in production.
/// A blackholed connect is the case a breaker exists for, because it is the one no timeout
/// makes cheap.
/// </para>
/// <para>
/// <b>What trips it is a socket that failed, not a database that answered.</b> A
/// <c>PostgresException</c> means PostgreSQL received the request and said no — a bad
/// query, a missing column, a statement timeout — and a source answering *no* is a source
/// that is up. Tripping on those would take a service down over one malformed filter,
/// which is a far worse failure than the one being fixed.
/// </para>
/// </remarks>
internal sealed class SourceBreaker : Graticula.Platform.Catalog.IStoreHealth
{
    /// <summary>
    /// How long a failed source is left alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ten seconds, and the first attempt at this was three, which did nothing.</b> A
    /// failed connect costs four seconds; with a three-second window a caller making one
    /// request at a time always found the breaker cooled by the time it came back, so
    /// eight measured requests during an outage each took 8.0 seconds exactly as before.
    /// <b>The window has to be longer than the failure it is protecting against</b>, or it
    /// only helps callers that overlap — which is the opposite of the case
    /// [D-131](../../docs/architecture-debt.md) is about, since the whole point is that a
    /// serial client should not pay repeatedly.
    /// </para>
    /// <para>
    /// <b>Two bounds, and ten sits between them.</b> Below about eight it is shorter than
    /// two failed attempts and a serial caller starts paying again; above about thirty it
    /// is longer than an operator will accept being refused after the database is back.
    /// Ten gives a recovered source at most ten seconds of stale refusals — well inside
    /// any readiness period — and gives a client retrying every second a fast refusal
    /// every time.
    /// </para>
    /// <para>
    /// <b>It is a number with a measurement under it and it is still one number for every
    /// deployment</b>, which is the honest limit: a source whose failures cost thirty
    /// seconds rather than four would want a longer window, and nothing here adapts. That
    /// is a setting when somebody has such a source, not before.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Cooling = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The key for the platform store, which is not a layer's connection string.
    /// </summary>
    /// <remarks>
    /// <b>Named rather than the store's own connection string, and that is deliberate.</b>
    /// The platform store's string carries its credentials, and a key is a thing that gets
    /// logged, counted and put on a status page. A layer's string is already handled that
    /// way elsewhere in this server and is its own debt to pay; this one does not need to
    /// join it.
    /// </remarks>
    public const string PlatformStore = "\u0000platform-store";

    private readonly ConcurrentDictionary<string, long> _tripped = new(StringComparer.Ordinal);

    private readonly Func<DateTimeOffset> _now;
    private readonly Microsoft.Extensions.Logging.ILogger? _log;

    /// <summary>Creates a breaker.</summary>
    /// <param name="logger">
    /// Where a trip is announced. <b>Announced at all, because a breaker is invisible
    /// otherwise</b> — an operator seeing fast refusals during an outage has no way to
    /// tell a working breaker from a server that has stopped trying, and those need
    /// opposite responses.
    /// </param>
    /// <param name="now">
    /// Where the time comes from. Injected so the cooling window can be tested without
    /// sleeping, which is the only way a test of a time-based rule is worth running.
    /// </param>
    public SourceBreaker(
        Microsoft.Extensions.Logging.ILoggerFactory? logger = null,
        Func<DateTimeOffset>? now = null)
    {
        _log = logger?.CreateLogger("breaker");
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether this source is currently being left alone.</summary>
    /// <param name="source">The source key: a connection string, or the platform store's.</param>
    /// <returns>Whether a request should be refused without trying.</returns>
    public bool IsOpen(string source)
    {
        if (!_tripped.TryGetValue(source, out long until))
        {
            return false;
        }

        if (_now().UtcTicks < until)
        {
            return true;
        }

        /*
          <b>One caller probes and the rest stay refused, and the first version of this
          let everybody through.</b> It removed the entry when the window expired, with a
          comment arguing that *the worst case is one cooling window's worth of slow
          requests instead of one* — and that comment was wrong about the case that
          matters. Measured: 60 requests at 20 concurrent, 45 seconds into an outage, all
          60 served and **not one of them under a second**, median 4.0 s. Twenty callers
          arrived together, all found the entry gone, and all paid a four-second blackholed
          connect at the same time. **That is the queue collapse
          [D-131](../../docs/architecture-debt.md) is about, arriving once every ten
          seconds instead of continuously**, which is better and is not the fix.

          <b>Single-flight without a second state, using the dictionary's own compare.</b>
          `TryUpdate` succeeds for exactly one caller — the one whose read of `until` was
          the current value — and that caller becomes the prober while every other read
          sees the pushed-forward window and stays refused. No probing flag, no rule about
          what happens if the prober never returns: the window it wrote expires on its own
          and the next caller takes the role.
        */
        return !_tripped.TryUpdate(source, _now().Add(Cooling).UtcTicks, until);
    }

    /// <summary>Records that a source could not be reached.</summary>
    /// <param name="source">The source key.</param>
    /// <param name="failure">What went wrong.</param>
    /// <returns>Whether this tripped the breaker.</returns>
    /// <remarks>
    /// <b>Only a socket failure trips it.</b> See the class remarks: a
    /// <c>PostgresException</c> is a database that answered, and a
    /// <see cref="OperationCanceledException"/> is usually a caller leaving.
    /// </remarks>
    public bool Failed(string source, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (!Unreachable(failure))
        {
            return false;
        }

        bool announce = !_tripped.ContainsKey(source);

        _tripped[source] = _now().Add(Cooling).UtcTicks;

        // Once per trip, not once per failure: the second is the log-flooding D-133 is
        // about, arriving through a different door.
        if (announce && _log is { } log)
        {
            Log.SourceTripped(log, Cooling.TotalSeconds, Name(source));
        }

        return true;
    }

    /// <summary>Records that a source answered.</summary>
    /// <param name="source">The source key.</param>
    /// <remarks>
    /// <b>Cheap on the hot path, which is why it is a containment check first.</b> Almost
    /// every call is a success against a source that was never tripped, and
    /// <c>ContainsKey</c> on an empty dictionary is the whole cost of that case.
    /// </remarks>
    public void Succeeded(string source)
    {
        if (!_tripped.IsEmpty)
        {
            _tripped.TryRemove(source, out _);
        }
    }

    /// <summary>
    /// A source key as something safe to write in a log.
    /// </summary>
    /// <remarks>
    /// <b>A layer's key is its connection string, which carries a password.</b> D-120 is a
    /// debt about a credential reaching a log through a query string; putting one there
    /// through a diagnostic would be the same mistake made deliberately. The host and
    /// database are what an operator needs and are not secret.
    /// </remarks>
    private static string Name(string source)
    {
        if (string.Equals(source, PlatformStore, StringComparison.Ordinal))
        {
            return "the platform store";
        }

        try
        {
            NpgsqlConnectionStringBuilder parsed = new(source);

            return $"{parsed.Host}:{parsed.Port}/{parsed.Database}";
        }
        catch (ArgumentException)
        {
            // A key this cannot parse is not a connection string, and printing it raw is
            // the one thing this method exists to avoid.
            return "a data source";
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The platform store's own view, for the catalogue read that cannot see this
    /// type.</b> See <see cref="Graticula.Platform.Catalog.IStoreHealth"/> for why the
    /// dependency points that way.
    /// </remarks>
    bool Graticula.Platform.Catalog.IStoreHealth.IsOpen => IsOpen(PlatformStore);

    /// <inheritdoc/>
    void Graticula.Platform.Catalog.IStoreHealth.Failed(Exception failure) =>
        Failed(PlatformStore, failure);

    /// <inheritdoc/>
    void Graticula.Platform.Catalog.IStoreHealth.Succeeded() => Succeeded(PlatformStore);

    /// <summary>Whether an exception means the source could not be reached at all.</summary>
    /// <param name="failure">The exception.</param>
    /// <returns>Whether it is a connectivity failure.</returns>
    public static bool Unreachable(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        for (Exception? at = failure; at is not null; at = at.InnerException)
        {
            /*
              <b>This breaker's own refusal counts, and leaving it out made the breaker
              fight the thing it exists to make cheap.</b> Measured: 45 seconds into an
              outage, the first request served a document from the remembered shape in 8 s
              and the next seven answered 503 in 12 ms. The fallback in
              `ServiceContexts` asks *was the source unreachable* before serving what it
              remembers, and a `SourceUnreachableException` is exactly that answer arriving
              instantly instead of after four seconds — so not saying so turned fast
              refusals into fast wrong refusals.

              <b>Two resilience mechanisms met and one silenced the other</b>, which is the
              failure mode worth naming: each was correct alone. D-131 made refusals cheap
              and D-127 made documents survive, and the second reads the first's signal.
            */
            if (at is SourceUnreachableException)
            {
                return true;
            }

            /*
              <b>A database that answered is a database that is up, however unwelcome the
              answer -- unless what it answered is that it is going away.</b> That exception
              is not a nicety: `57P01` is what every connection receives when PostgreSQL is
              shut down or restarted, and it arrives as a `PostgresException` like any
              syntax error. Reading it as *the database replied, so it is up* is reading a
              farewell as a greeting.

              <b>Which cost ADR-026 the thing it promises.</b> Measured on CI run
              33923883963, with the platform container stopped: the catalogue kept serving,
              because `CatalogFallback.IsUnreachable` knows the four states -- and the
              shapes did not, because this method did not, so
              `ServiceContexts.GetAsync` threw instead of returning the shape it
              remembered and a publicly shared service was refused 503 while blind. Two
              answers to *is this database unreachable*, one right and one wrong, in one
              request. [D-224](../../docs/architecture-debt.md).

              <b>So there is one answer now, and this asks it.</b> Copying the list here
              would be the third place it is written and the second place it can rot.
            */
            if (at is PostgresException postgres)
            {
                return CatalogFallback.IsUnreachable(postgres);
            }

            if (at is NpgsqlException or System.Net.Sockets.SocketException
                or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A source that is being left alone after failing, refused without being asked.
/// </summary>
/// <remarks>
/// <b>A distinct type so the answer can be a 503 that says why.</b> It maps to the same
/// status as an unreachable database, because it means the same thing to a client — and it
/// costs microseconds instead of four seconds, which is the whole of
/// [D-131](../../docs/architecture-debt.md).
/// </remarks>
public sealed class SourceUnreachableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SourceUnreachableException()
        : this("A database this server depends on failed moments ago and is not being asked "
            + "again yet.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The sentence for the caller.</param>
    public SourceUnreachableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The sentence for the caller.</param>
    /// <param name="innerException">What caused it.</param>
    public SourceUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
