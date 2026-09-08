using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Graticula.Host;

/// <summary>
/// Which data sources an operator has taken out of service so a DBA can work on them.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md), and it is the fifth of
/// [ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8's five bullets — the one that was
/// never built.</b> A held connection blocks `ALTER TABLE`, and on PostgreSQL the waiting DDL
/// then queues every request behind it: a read that costs 296 ms unblocked holds its pooled
/// connection for **30.30 s** behind a lock ([D-08](../../docs/architecture-debt.md)). Quiesce is
/// how a DBA is given a window with our connections gone.
/// </para>
/// <para>
/// <b>It refuses; it does not hold.</b> §4.8's wording was *hold its requests*, and ADR-059 §5a
/// amends it: a wait whose length an operator controls is a queue that grows until it collapses,
/// which is what
/// [ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md) already
/// decided about waits in general.
/// </para>
/// <para>
/// <b>Every quiesce ends by itself.</b> This is the only refusal in this server that a person
/// starts, so it is the only one that needs a clock: an operator called away mid-change would
/// otherwise leave a service down with the server behaving exactly as instructed. The breaker
/// cools in ten seconds and admission control clears as the queue drains; this lapses.
/// </para>
/// <para>
/// <b>Node-local, deliberately.</b> It is about *this* process's connections. A second worker
/// holds its own and must be quiesced too — ADR-059 §4 states that as a limit rather than
/// leaving it to be discovered, and coordinating it across workers is
/// [Q-65](../../docs/open-questions.md).
/// </para>
/// </remarks>
internal sealed class SourceQuiesce
{
    /// <summary>
    /// How long a quiesce lasts when the caller does not say.
    /// </summary>
    /// <remarks>
    /// <b>Fifteen minutes, argued rather than measured — ADR-059 §5b.</b> Longer than any single
    /// `ALTER TABLE` this product's own migrations issue, shorter than a working session: a DBA
    /// doing one change does not meet it and a DBA who has gone to lunch does. Resuming is one
    /// request, so a change that genuinely needs longer is a second quiesce rather than a
    /// hostage. What nobody has measured is how long a real customer's DDL takes on a real
    /// table, which is ADR-059 condition 3.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

    /// <summary>The longest window this server will accept, however long one is asked for.</summary>
    /// <remarks>
    /// <b>A bound on the bound.</b> The deadline exists so a forgotten quiesce ends; a caller
    /// able to ask for a day would have removed the safety by using the feature. An hour is long
    /// enough for any migration somebody is watching and short enough that the worst mistake
    /// costs one hour rather than a weekend.
    /// </remarks>
    public static readonly TimeSpan LongestWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Held> _quiesced = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Creates the register.</summary>
    /// <param name="now">
    /// Where the time comes from. Injected so the window can be tested without waiting a quarter
    /// of an hour, which is the only way a test of a time-based rule is worth running.
    /// </param>
    public SourceQuiesce(Func<DateTimeOffset>? now = null) =>
        _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>One source taken out of service, and by whom.</summary>
    /// <param name="Who">The operator, for the refusal's sentence.</param>
    /// <param name="Since">When it began.</param>
    /// <param name="Until">When it ends by itself.</param>
    /// <param name="Why">What they said they were doing, or null.</param>
    public readonly record struct Held(
        string Who, DateTimeOffset Since, DateTimeOffset Until, string? Why);

    /// <summary>Takes a source out of service until a deadline.</summary>
    /// <param name="source">The source key — a connection string, as the pools are keyed.</param>
    /// <param name="who">The operator.</param>
    /// <param name="window">How long, clamped to <see cref="LongestWindow"/>.</param>
    /// <param name="why">What they are doing, for the refusal.</param>
    /// <returns>What was recorded, including the deadline actually applied.</returns>
    public Held Hold(string source, string who, TimeSpan? window, string? why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        DateTimeOffset began = _now();

        TimeSpan wanted = window ?? DefaultWindow;

        // <b>Clamped rather than refused.</b> An operator asking for two hours has said something
        // true about their migration; answering *no* teaches them to quiesce twice in a row,
        // which is the same outcome with an extra step. The response says what was applied.
        if (wanted > LongestWindow) wanted = LongestWindow;
        if (wanted <= TimeSpan.Zero) wanted = DefaultWindow;

        Held held = new(who, began, began + wanted, why);

        _quiesced[source] = held;

        return held;
    }

    /// <summary>Puts a source back in service before its deadline.</summary>
    /// <param name="source">The source key.</param>
    /// <returns>Whether it was quiesced.</returns>
    public bool Resume(string source) => _quiesced.TryRemove(source, out _);

    /// <summary>
    /// Whether this source is out of service, and what to say about it.
    /// </summary>
    /// <param name="source">The source key.</param>
    /// <returns>The hold, or null when the source is answering.</returns>
    /// <remarks>
    /// <b>The lapse happens here rather than on a timer.</b> A background sweep would be a second
    /// thing to start, stop and test for a state whose only reader is this method — the same
    /// shape <see cref="SourceBreaker"/> uses for its cooling window, and for the same reason.
    /// </remarks>
    public Held? Holding(string source)
    {
        if (!_quiesced.TryGetValue(source, out Held held))
        {
            return null;
        }

        if (_now() < held.Until)
        {
            return held;
        }

        // Ended by itself. Removed on the way past, so the register does not grow with every
        // quiesce a deployment has ever made.
        _quiesced.TryRemove(source, out _);

        return null;
    }

    /// <summary>Every source currently out of service, for the status page and the console.</summary>
    /// <returns>The source keys and what is holding each.</returns>
    /// <remarks>
    /// <b>Lapsed entries are dropped as this reads.</b> A listing that showed a quiesce which had
    /// already ended would be the status page reporting an outage that is over — which is the
    /// class of thing this repository has had to correct more than once.
    /// </remarks>
    public IReadOnlyDictionary<string, Held> Current()
    {
        Dictionary<string, Held> live = [];

        foreach (KeyValuePair<string, Held> one in _quiesced)
        {
            if (Holding(one.Key) is { } still)
            {
                live[one.Key] = still;
            }
        }

        return live;
    }

    /// <summary>How many sources are out of service, for <c>/admin/health</c>.</summary>
    public int Count => Current().Count;

    /// <summary>
    /// The sentence a caller is refused with.
    /// </summary>
    /// <param name="held">What is holding the source.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// <b>ADR-059 §5e: who, why and until when.</b> A planned, deliberate, self-ending
    /// unavailability that reads like a network fault sends whoever is on call to the wrong place
    /// at the one moment somebody already knows the answer —
    /// [D-150](../../docs/architecture-debt.md) is this server's own instance of that mistake.
    /// </remarks>
    public static string Says(Held held) => Says(held, DateTimeOffset.UtcNow);

    /// <summary>The refusal, as of a given moment.</summary>
    /// <param name="held">What is holding the source.</param>
    /// <param name="now">When the refusal is being written.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// <para>
    /// <b>A duration, not a clock time — and the first version of this was three hours wrong for
    /// anybody outside UTC.</b> These instants are `DateTimeOffset.UtcNow`, so formatting them as
    /// <c>HH:mm</c> printed a UTC wall clock into a sentence read by an operator in their own
    /// timezone. *Answers again at 14:17* to somebody whose clock says 17:14 is worse than saying
    /// nothing: it reads as a time they can check, and it is wrong.
    /// </para>
    /// <para>
    /// <b>And a duration is the more useful of the two anyway.</b> What a reader does with this
    /// is decide whether to wait; *in about twelve minutes* answers that without arithmetic, and
    /// the exact instant is in the response body as a proper offset for anything that needs it.
    /// </para>
    /// <para>
    /// <b>The clock is a parameter so the sentence can be tested</b>, which is the same reason
    /// the register takes one.
    /// </para>
    /// </remarks>
    public static string Says(Held held, DateTimeOffset now)
    {
        TimeSpan left = held.Until - now;

        string when = left <= TimeSpan.Zero
            ? "in a moment"
            : left < TimeSpan.FromMinutes(2)
                ? $"in about {Math.Max(1, (int)Math.Round(left.TotalSeconds))} seconds"
                : $"in about {(int)Math.Round(left.TotalMinutes)} minutes";

        /*
          <b>No reason rather than an invented one — a design review's finding, 2026-09-09.</b>
          This said *for a schema change* when nobody had given a reason, which is a sentence a
          caller reads as something the operator typed. The console was doing the same thing one
          layer up, sending its own placeholder as a real value, and both were fixed together;
          this half is the one an ArcGIS client sees.

          <b>The clause is dropped rather than replaced.</b> *for no stated reason* would be a
          reproach aimed at somebody who is not reading it, and a quiesce with no reason is a
          perfectly ordinary thing for an operator in a hurry to have done.
        */
        return $"This layer's database was taken out of service by {held.Who}"
            + (held.Why is { Length: > 0 } why ? $" — {why}" : string.Empty)
            + ", so this server has closed its connections to let the work happen. It answers "
            + "again " + when + " unless it is resumed sooner. Nothing is wrong with the "
            + "database.";
    }
}

/// <summary>
/// A request reached a data source an operator has taken out of service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own type rather than <see cref="SourceUnreachableException"/>, and the reason is the
/// sentence.</b> That one appends <i>check /healthz/ready and /admin/health</i> and sets
/// <c>Retry-After</c> to the breaker's cooling window — both wrong here. A quiesce is planned,
/// the database is healthy, and this server knows exactly when it ends; sending whoever is on
/// call to a health check is the misdirection ADR-059 §5e exists to avoid.
/// </para>
/// <para>
/// <b>And the deadline travels with it</b>, so <c>Retry-After</c> can be a fact rather than an
/// estimate. <c>ErrorResponse.RetryAfterFor</c> only answers where the server sets the time
/// itself; this is one of the two cases where it does.
/// </para>
/// </remarks>
/// <param name="message">What to tell the caller.</param>
/// <param name="until">When the source answers again.</param>
public sealed class SourceQuiescedException(string message, DateTimeOffset until)
    : Exception(message)
{
    /// <summary>When the source answers again by itself.</summary>
    public DateTimeOffset Until { get; } = until;
}
