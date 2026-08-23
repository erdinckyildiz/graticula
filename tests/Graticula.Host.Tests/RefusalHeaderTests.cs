using System;
using Graticula.Host;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// What each shape of refusal tells a client about coming back.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-130](../../docs/architecture-debt.md)'s third check.</b>
/// `ConnectionBudgetFullException`'s own remark said the refusal comes with a
/// <c>Retry-After</c>; nothing set one, and the performance gate found it absent by reading
/// the live headers off a real 503. A sentence describing behaviour the code does not have
/// is the whole shape of that debt, and the repair is an assertion per refusal shape rather
/// than a corrected sentence.
/// </para>
/// <para>
/// <b>Both directions are asserted, and the second is the one that rots quietly.</b> A
/// missing <c>Retry-After</c> is a client that hammers a saturated server. A
/// <c>Retry-After</c> on a refusal whose recovery this server cannot predict is worse: it
/// is a promise, and a client that believes it retries into the same wall on a schedule.
/// </para>
/// </remarks>
public sealed class RefusalHeaderTests
{
    /// <summary>
    /// A refusal this server will lift on its own says when.
    /// </summary>
    /// <remarks>
    /// <b>Two shapes, and both times are the server's own.</b> A budget refusal frees a
    /// slot when a query finishes — bounded by the wait the caller has already spent — and
    /// the breaker's refusal ends when its cooling window closes. Neither is an estimate.
    /// </remarks>
    [Fact]
    public void A_refusal_the_server_will_lift_says_when()
    {
        int? budget = ErrorResponse.RetryAfterFor(
            new ConnectionBudgetFullException("too many waiting"));

        Assert.NotNull(budget);
        Assert.True(budget > 0, "a Retry-After of zero or less tells a client nothing");

        int? breaker = ErrorResponse.RetryAfterFor(new SourceUnreachableException());

        Assert.NotNull(breaker);

        // <b>The breaker's own window, not a number picked to look plausible.</b> A client
        // told to come back sooner is refused again in microseconds; one told to come back
        // later waits longer than the server needs.
        Assert.Equal(
            (int)Math.Ceiling(SourceBreaker.Cooling.TotalSeconds),
            breaker);
    }

    /// <summary>
    /// A refusal whose recovery the server cannot predict promises nothing.
    /// </summary>
    /// <remarks>
    /// <b>An unreachable database is the case that matters.</b> Nothing in this server
    /// knows when somebody will start PostgreSQL again, and a <c>Retry-After</c> would turn
    /// that ignorance into a schedule a client follows. The breaker's refusal is different
    /// and is asserted above: it is this server's own decision, with its own end.
    /// </remarks>
    [Theory]
    [InlineData("unreachable database")]
    [InlineData("statement timeout")]
    [InlineData("cancelled")]
    [InlineData("something else")]
    public void A_refusal_the_server_cannot_predict_promises_nothing(string shape)
    {
        Exception refusal = shape switch
        {
            "unreachable database" => new NpgsqlException("Failed to connect"),
            // <b>A statement timeout rather than an out-of-memory</b>, because the
            // analyser reserves that type for the runtime and the point is the same:
            // a refusal whose recovery this server cannot name.
            "statement timeout" => new PostgresException(
                "canceling statement due to statement timeout", "ERROR", "ERROR", "57014"),
            "cancelled" => new OperationCanceledException(),
            _ => new InvalidOperationException("something else"),
        };

        Assert.Null(ErrorResponse.RetryAfterFor(refusal));
    }

    /// <summary>
    /// The two refusals that carry a time are 503, and the ones that do not are not all 503.
    /// </summary>
    /// <remarks>
    /// <b>Because <c>Retry-After</c> on anything but a 503 or a 429 means something else.</b>
    /// RFC 9110 §10.2.3 attaches it to 503, to 429 and to a 3xx redirect; putting it on a
    /// 500 or a 504 is a header a client is entitled to interpret differently from what
    /// this server meant. This asserts the pairing rather than assuming it.
    /// </remarks>
    [Fact]
    public void Only_a_service_unavailable_carries_a_retry_time()
    {
        foreach (Exception refusal in (Exception[])
        [
            new ConnectionBudgetFullException("too many waiting"),
            new SourceUnreachableException(),
        ])
        {
            (int status, _) = ErrorResponse.Classify(refusal);

            Assert.Equal(503, status);
            Assert.NotNull(ErrorResponse.RetryAfterFor(refusal));
        }
    }
}
