using System;

namespace Graticula.Host;

/// <summary>
/// A failure that repeats, logged once in full and counted thereafter.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-133](../../docs/architecture-debt.md), and the number is the argument.</b> Two
/// background workers retry a job claim every three seconds and logged the complete
/// Npgsql stack on every failure. Measured over one seventeen-minute outage:
/// <b>338 warnings, 1.68 MB</b> — about 100 kB a minute, roughly 145 MB for a day of
/// sustained outage. Every one of those 338 says the same thing.
/// </para>
/// <para>
/// <b>The cost is not disk, it is the log becoming unreadable exactly when somebody is
/// reading it.</b> An operator opening the log during an outage wants the one line that
/// says what broke; what they get is the same stack trace three hundred times, with
/// anything else that happened buried between repetitions. A log that grows fastest when
/// the server is least well is a log that fails at its own job.
/// </para>
/// <para>
/// <b>So: the exception in full the first time, the sentence and a count thereafter, and a
/// line on recovery saying how many there were and for how long.</b> The recovery line is
/// the part that is easy to leave out and is the one an operator reads afterwards — *this
/// was away for seventeen minutes and came back* is the sentence that closes an incident.
/// </para>
/// <para>
/// <b>Keyed on the message rather than the type.</b> Two different failures that happen to
/// be the same exception class are two things an operator needs to see; `Npgsql` reports a
/// refused connection and an authentication failure as the same type with different text,
/// and collapsing those would hide a misconfiguration behind an outage.
/// </para>
/// <para>
/// <b>Not thread-safe, and it does not need to be.</b> Each instance belongs to one
/// worker's own retry loop, which is a single sequential loop by construction. A shared
/// instance would need locking and would also be wrong: two workers failing is two facts.
/// </para>
/// </remarks>
internal sealed class RepeatedFailure
{
    /// <summary>
    /// How many repetitions between the summary lines.
    /// </summary>
    /// <remarks>
    /// <b>Not one, and not never.</b> Logging nothing after the first would leave an
    /// operator unable to tell *still broken* from *the worker died*, which is the failure
    /// this replaces in the other direction. At a three-second retry, twenty is about once
    /// a minute — often enough to prove the loop is alive and rare enough that the log
    /// stays readable.
    /// </remarks>
    public const int Every = 20;

    private string? _reason;
    private int _times;
    private DateTimeOffset _since;

    /// <summary>What to do about a failure that has just happened.</summary>
    public enum Action
    {
        /// <summary>Log it in full: it is new, or it is a different failure.</summary>
        InFull,

        /// <summary>Log nothing. It is the same failure and the count is not due.</summary>
        Nothing,

        /// <summary>Log the sentence and the count.</summary>
        Summarise,
    }

    /// <summary>How many times the current failure has happened.</summary>
    public int Times => _times;

    /// <summary>How long it has been failing, or zero.</summary>
    /// <param name="now">The current time, passed in so this can be tested.</param>
    /// <returns>The span.</returns>
    public TimeSpan For(DateTimeOffset now) =>
        _times == 0 ? TimeSpan.Zero : now - _since;

    /// <summary>Records a failure and says how to report it.</summary>
    /// <param name="reason">The exception's message.</param>
    /// <param name="now">The current time.</param>
    /// <returns>What to log.</returns>
    public Action Failed(string reason, DateTimeOffset now)
    {
        if (_times == 0 || !string.Equals(reason, _reason, StringComparison.Ordinal))
        {
            // <b>A different message restarts the count rather than adding to it.</b> The
            // alternative — one counter for every failure — would report *the 300th
            // failure* for something that had just happened for the first time.
            _reason = reason;
            _times = 1;
            _since = now;

            return Action.InFull;
        }

        _times++;

        return _times % Every == 0 ? Action.Summarise : Action.Nothing;
    }

    /// <summary>
    /// Records a success, and says how many failures preceded it.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="over">How long the failures had been going on.</param>
    /// <returns>The count, or zero when nothing had failed.</returns>
    public int Recovered(DateTimeOffset now, out TimeSpan over)
    {
        int times = _times;
        over = For(now);

        _times = 0;
        _reason = null;

        return times;
    }
}
