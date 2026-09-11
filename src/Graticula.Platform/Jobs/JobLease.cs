using System;

namespace Graticula.Platform.Jobs;

/// <summary>
/// The numbers a job's lease runs on — D-243, ADR-011 §3.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Documented numbers rather than implementation details</b>, for the reason ADR-011 §3.3 gives
/// about the polling interval: each is a floor on how long something takes that an operator will
/// ask about. The longest is how long a dead worker can hold a job, which is
/// <see cref="Duration"/>.
/// </para>
/// <para>
/// <b>Renewed on its own clock, not on the work's.</b> A lease renewed only when a layer finished
/// importing would have to be longer than the slowest layer, which nobody has measured and which
/// would then be the time a crashed import stays invisible. A heartbeat that runs beside the work
/// makes the lease a statement about the *worker* — alive or not — and leaves the work's pace out
/// of it.
/// </para>
/// </remarks>
public static class JobLease
{
    /// <summary>
    /// How long a claim lasts without renewal: a minute, so a crashed worker's job is back in
    /// somebody's hands within a minute of the next idle tick.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often a working worker renews. Four renewals per lease, so three can be lost to a
    /// briefly unreachable store before the job is taken away.
    /// </summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a <c>running</c> row with no lease at all is left alone — a row claimed by a build
    /// older than migration 45, which neither sets nor renews one. An hour, because such a build
    /// may still be working on it and an import takes seconds; the stuck rows D-243 is about are
    /// days old.
    /// </summary>
    public static readonly TimeSpan UnleasedGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// The least time between two sweeps by one worker. The sweep is one statement; running it on
    /// every idle tick of every worker would be a steady trickle of writes for a condition that
    /// arises when a process dies.
    /// </summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times a harmless job may be claimed before a lost lease fails it rather than
    /// queueing it again. Two: the original claim and one retry.
    /// </summary>
    public const int Attempts = 2;
}

/// <summary>One job taken back from a worker whose lease lapsed.</summary>
/// <param name="Id">The job.</param>
/// <param name="Kind">What sort of work.</param>
/// <param name="Became">
/// <see cref="JobStatus.Queued"/> when it will be run again, <see cref="JobStatus.Failed"/> when not.
/// </param>
/// <param name="LostBy">The claimant that stopped renewing, as it named itself.</param>
public sealed record JobReclaim(Guid Id, JobKind Kind, JobStatus Became, string? LostBy);
