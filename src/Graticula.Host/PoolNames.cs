using System;

namespace Graticula.Host;

/// <summary>
/// What this server's connection pools call themselves in <c>pg_stat_activity</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-166](../../docs/architecture-debt.md) named the pools; [D-208](../../docs/architecture-debt.md)
/// is why the name also carries who.</b> A name that every server shares answers *is a Graticula
/// server holding this database* and cannot answer *how many*, so a test that measures a
/// per-server property — `PollerPoolTests`, whose subject is one connection per job kind — counts
/// two servers' sessions as one server's and reports the sum as a product defect. It did that
/// twice in one afternoon, both times mistaken for a regression in unrelated work.
/// </para>
/// <para>
/// <b>The instance is the discriminator PostgreSQL does not otherwise offer.</b>
/// <c>pg_stat_activity</c> has no column that groups a pool's connections by the process that
/// opened them: <c>client_addr</c> is the same for two servers on one host and
/// <c>client_port</c> is different for every connection in one pool. <c>application_name</c> is
/// the only field this server controls, so it is the only place the answer can go.
/// </para>
/// <para>
/// <b>The colon is load-bearing.</b> <c>graticula</c> is a prefix of <c>graticula-jobs</c>, so
/// matching the request pool by prefix alone would match the pollers' too. Every name here is
/// <c>{pool}:{instance}</c>, which makes <c>graticula-jobs:%</c> unambiguous and leaves
/// <c>graticula%</c> meaning *any pool of any Graticula server* — including one built before
/// this existed, which is what keeps a development server started an hour ago visible to the
/// suite that runs now.
/// </para>
/// </remarks>
public static class PoolNames
{
    /// <summary>What the pool serving requests calls itself, before the instance.</summary>
    public const string Request = "graticula";

    /// <summary>What the job pollers' pool calls itself, before the instance.</summary>
    /// <remarks>
    /// Split out by [D-110](../../docs/architecture-debt.md) so the shared pool can reach the
    /// floor of zero <see href="../../docs/adr/ADR-007-service-runtime.md">ADR-007</see> §4.8
    /// claims, and named so the floor can be attributed rather than recognised from its
    /// statement text.
    /// </remarks>
    public const string Jobs = "graticula-jobs";

    /// <summary>
    /// A <c>like</c> pattern matching every pool of every Graticula server on a database.
    /// </summary>
    /// <remarks>
    /// Deliberately matches the bare names as well as the instance-qualified ones. A server
    /// built before <see cref="Of"/> existed still names itself <c>graticula</c>, and a suite
    /// that stopped seeing it would go green in exactly the state it exists to refuse.
    /// </remarks>
    public const string AnyPattern = Request + "%";

    /// <summary>A <c>like</c> pattern matching the pollers' pool of every server.</summary>
    public const string JobsPattern = Jobs + "%";

    /// <summary>
    /// The name a pool of this process reports to PostgreSQL.
    /// </summary>
    /// <param name="pool">The pool's own name — <see cref="Request"/> or <see cref="Jobs"/>.</param>
    /// <returns>The name, within PostgreSQL's sixty-three byte ceiling.</returns>
    /// <remarks>
    /// <b>Machine and process, because both halves are asked for.</b> Two servers on one host
    /// are told apart by the process id; two hosts against one database are told apart by the
    /// name, which is also what an operator needs to know to go and stop one. The machine name
    /// is trimmed rather than the whole string, so the process id — the half that is always
    /// distinguishing — cannot be the part that falls off the end.
    /// </remarks>
    public static string Of(string pool)
    {
        // <b>Sixty-three, not sixty-four.</b> `application_name` is a `name`, which is
        // NAMEDATALEN-1 usable bytes. PostgreSQL truncates silently past it, and a silently
        // truncated instance is two servers sharing a name again.
        const int Ceiling = 63;

        string machine = Environment.MachineName;
        string process = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        int room = Ceiling - pool.Length - process.Length - 2;

        if (room < 1)
        {
            return $"{pool}:{process}";
        }

        if (machine.Length > room)
        {
            machine = machine[..room];
        }

        return $"{pool}:{machine}/{process}";
    }
}
