using System;
using System.Linq;
using Graticula.Platform.Jobs;
using Xunit;

namespace Graticula.Platform.Tests.Jobs;

/// <summary>
/// Every job kind has declared what re-running it would do.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-011](../../../docs/adr/ADR-011-job-system.md) condition 2</b>: *every job type
/// declares its re-run behaviour before it is registered. There is no default, because a wrong
/// default here corrupts data.*
/// </para>
/// <para>
/// <b>The declaration is a function and this is what makes it a rule.</b>
/// <c>JobKinds.RerunOf</c> throws for a kind it does not know, which turns *undeclared* into a
/// failure — but a failure at the moment somebody imports a geodatabase is a failure in
/// production. This walks the enumeration, so the same omission fails on the build that adds
/// the kind.
/// </para>
/// <para>
/// <b>It cannot check that the answer is right</b>, and nothing can. What it checks is that
/// somebody was made to write one down, which at two kinds is cheap and at twenty is the only
/// way the condition survives.
/// </para>
/// </remarks>
public sealed class JobRerunTests
{
    [Fact]
    public void Every_job_kind_declares_what_a_second_run_would_do()
    {
        foreach (JobKind kind in Enum.GetValues<JobKind>())
        {
            JobRerun rerun = JobKinds.RerunOf(kind);

            Assert.True(
                Enum.IsDefined(rerun),
                $"{kind} declared a re-run behaviour that is not one of the three.");
        }
    }

    /// <summary>
    /// No kind is registered whose second run would duplicate or corrupt.
    /// </summary>
    /// <remarks>
    /// <b>The condition's word is *before*.</b> <see cref="JobRerun.Unsafe"/> exists so the true
    /// answer can be written down when it is the true one — and a kind that would be it needs a
    /// constraint or a design change before it is registered, not a note afterwards. This is
    /// where that *before* is enforced.
    /// </remarks>
    [Fact]
    public void No_registered_kind_is_unsafe_to_run_twice()
    {
        JobKind[] unsafeKinds =
        [
            .. Enum.GetValues<JobKind>().Where(k => JobKinds.RerunOf(k) == JobRerun.Unsafe),
        ];

        Assert.True(
            unsafeKinds.Length == 0,
            "These job kinds are registered and would duplicate or corrupt if their work ran "
            + "twice, with nothing stopping them: " + string.Join(", ", unsafeKinds)
            + ". ADR-011 condition 2 says the behaviour is declared *before* the kind is "
            + "registered, which means a constraint or a design change rather than a note.");
    }

    /// <summary>
    /// A kind nobody has declared throws, and says which condition it is enforcing.
    /// </summary>
    /// <remarks>
    /// <b>The throw is the mechanism, so it is asserted.</b> A `_ =>` arm returning
    /// <c>Harmless</c> would be exactly the wrong default the condition names: a kind added
    /// without a decision would inherit the safest word silently.
    /// </remarks>
    [Fact]
    public void An_undeclared_kind_is_refused_rather_than_given_a_default()
    {
        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => JobKinds.RerunOf((JobKind)9999));

        Assert.Contains("ADR-011 condition 2", refused.Message, StringComparison.Ordinal);
    }
}
