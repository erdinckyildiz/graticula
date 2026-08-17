using System;
using System.IO;
using System.Threading;

namespace Graticula.Testing;

/// <summary>
/// Lets one database-backed suite run at a time, across processes.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-60, and the second attempt at it.</b> Four of this repository's nine test
/// assemblies reach the same PostgreSQL — two directly, and two through a running
/// server that holds its own pool against it. Run at once, individual Postgres
/// tests time out at exactly 30 seconds and are reported as failures. Measured
/// 2026-08-17 and again 2026-08-18: three named tests failed at 33, 33 and 49
/// seconds in a solution-wide run and all three passed in a serialised one, where
/// the whole suite is 1 m 38 s.
/// </para>
/// <para>
/// <b>The first attempt was the wrong lever, and knowing why matters.</b>
/// <c>MaxCpuCount</c> in <c>graticula.runsettings</c> limits parallelism <em>inside
/// one VSTest run</em>. <c>dotnet test</c> on a solution does not make one run: it
/// asks MSBuild to build the solution, and MSBuild invokes the <c>VSTest</c> target
/// per project on as many nodes as it has — a verbose build shows nodes 1, 8 and 14
/// — so four separate test hosts start whatever any single one of them was told
/// about CPUs. Two solution-wide runs passed under that setting and the third
/// failed, which is exactly how a fix that does nothing looks.
/// </para>
/// <para>
/// <b>Why not <c>-m:1</c>.</b> It works, and it is one token on the command line.
/// But it cannot be put anywhere that makes the bare command safe:
/// <c>Directory.Build.rsp</c> is not read by <c>dotnet build</c> here — the same
/// verbose build still spread across nodes with the file in place — and MSBuild has
/// no property for node count. A fix that only exists when somebody remembers a
/// flag is the failure mode D-60 is about. It also serialises the build, which is
/// not the thing that contends.
/// </para>
/// <para>
/// <b>So the lock is where the contention is.</b> An exclusive handle on one file
/// in the temporary directory, taken by each suite that touches the shared
/// database, held for as long as its test host lives. The five suites that need
/// nothing outside their process are untouched and still run in parallel, and so
/// does every build.
/// </para>
/// <para>
/// <b>The operating system releases it, which is what makes this safe.</b> A test
/// host that crashes, is killed, or is stopped mid-run drops the handle with the
/// process — there is no stale lock to clear and no cleanup path that has to run.
/// That is the whole reason this is a file handle rather than a lock file with a
/// process id in it.
/// </para>
/// <para>
/// <b>In CI this does nothing, and that is correct.</b> Each job runs one
/// <c>dotnet test</c> per project, so nothing else is ever holding the handle; the
/// wait is satisfied immediately. The setting is for the command a person types
/// before committing.
/// </para>
/// </remarks>
public static class OneSuiteAtATime
{
    /// <summary>How long to wait for the suite ahead before giving up.</summary>
    /// <remarks>
    /// Generous, because the suite ahead can legitimately take a minute and a half,
    /// and bounded, because a wait with no end is a hang and a hang looks like
    /// nothing at all — which is the shape of failure D-60 spent a week not
    /// noticing.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(15);

    private static readonly object Gate = new();

    // Held, never released. The handle is the lock, and the process is its lifetime.
    private static FileStream? _held;

    /// <summary>
    /// Waits until no other database-backed suite is running, then proceeds.
    /// </summary>
    /// <remarks>
    /// Called from a fixture, and safe to call any number of times: the first call
    /// takes the handle and the rest return at once.
    /// </remarks>
    public static void Enter()
    {
        if (_held is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_held is not null)
            {
                return;
            }

            string path = Path.Combine(Path.GetTempPath(), "graticula-test-database.lock");
            DateTime deadline = DateTime.UtcNow + Patience;

            while (true)
            {
                try
                {
                    _held = new FileStream(
                        path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                    return;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    // Another suite has it. Half a second is short against a suite
                    // that runs for a minute and long enough not to spin.
                    Thread.Sleep(500);
                }
                catch (IOException e)
                {
                    throw new TimeoutException(
                        $"Waited {Patience.TotalMinutes:0} minutes for another test suite to "
                        + $"finish with the shared database. The lock is an exclusive handle on "
                        + $"'{path}', held only for the lifetime of a test host — so if nothing "
                        + "else is running, something is holding that file that is not a test. "
                        + "See D-60.", e);
                }
            }
        }
    }
}
