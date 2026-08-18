using System;
using System.Reflection;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The exception-to-status mapping.
/// </summary>
/// <remarks>
/// These exist because the mapping is a set of claims about whose fault a
/// failure is, and a wrong claim sends an operator to the wrong system. A 500
/// for a statement timeout says "the server is broken"; a 504 says "your query
/// was too expensive". Only one of those gets the problem fixed.
/// </remarks>
public sealed class ErrorResponseTests
{
    /// <summary>
    /// Builds a <see cref="PostgresException"/> with a chosen SQL state.
    /// </summary>
    /// <remarks>
    /// Reflection, reluctantly. Npgsql's public constructor does not set
    /// <c>SqlState</c>, and the alternative is an integration test per branch —
    /// which would make the cheap, high-value assertions below cost a database.
    /// If this breaks on an Npgsql upgrade it breaks loudly, which is the
    /// property that matters.
    /// </remarks>
    private static PostgresException WithSqlState(string sqlState)
    {
        PostgresException exception = new(
            messageText: "test", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);

        Assert.Equal(sqlState, exception.SqlState);
        return exception;
    }

    [Fact]
    public void A_statement_timeout_is_504_and_says_the_server_did_not_fail()
    {
        (int status, string message) = ErrorResponse.Classify(WithSqlState("57014"));

        Assert.Equal(504, status);
        Assert.Contains("statement timeout", message, StringComparison.Ordinal);

        // The advice is the point of the message, not the diagnosis.
        Assert.Contains("resultRecordCount", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client-side statement timeout is a timeout, not an unreachable database.
    /// </summary>
    /// <remarks>
    /// <b>Measured before it was fixed.</b> A service configured with a one-second statement
    /// timeout answered 19 of 30 concurrent queries with *a database this server depends on is
    /// unreachable* — because Npgsql's command timeout does not raise 57014. It gives up on the
    /// socket read and throws <c>NpgsqlException</c> wrapping a <c>TimeoutException</c>, which fell
    /// through to the general connectivity case. The operator had set the bound themselves and
    /// their clients were sent to check the network.
    /// </remarks>
    [Fact]
    public void A_client_side_statement_timeout_is_not_reported_as_an_unreachable_database()
    {
        NpgsqlException timedOut = new(
            "Exception while reading from stream",
            new TimeoutException("Timeout during reading attempt"));

        (int status, string message) = ErrorResponse.Classify(timedOut);

        Assert.Equal(504, status);
        Assert.Contains("statement timeout", message, StringComparison.Ordinal);

        // The half that was wrong: it must not send anybody to look at the network.
        Assert.DoesNotContain("unreachable", message, StringComparison.Ordinal);
        Assert.Contains("up and reachable", message, StringComparison.Ordinal);
    }

    /// <summary>A genuinely unreachable database still says so.</summary>
    /// <remarks>
    /// <b>The other side of the branch above, and the reason it is written narrowly.</b> Matching
    /// every <c>NpgsqlException</c> as a timeout would have traded one misdiagnosis for its
    /// opposite — a database that is actually down reported as a slow query, which is worse
    /// because it is reassuring.
    /// </remarks>
    [Fact]
    public void An_unreachable_database_is_still_reported_as_unreachable()
    {
        NpgsqlException down = new("Failed to connect", new System.Net.Sockets.SocketException(10061));

        (int status, string message) = ErrorResponse.Classify(down);

        Assert.Equal(503, status);
        Assert.Contains("unreachable", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dropped_table_says_retrying_will_not_help()
    {
        (int status, string message) = ErrorResponse.Classify(WithSqlState("42P01"));

        Assert.Equal(503, status);
        Assert.Contains("will not", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("42501")]
    [InlineData("28P01")]
    public void A_credential_problem_points_at_the_stored_credential(string sqlState)
    {
        (int status, string message) = ErrorResponse.Classify(WithSqlState(sqlState));

        Assert.Equal(503, status);
        Assert.Contains("credential", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_postgres_error_is_503_rather_than_falling_through_to_500()
    {
        // 23505 has no case of its own. It must still be a 503 attributed to a
        // database rather than to our own logic — the NpgsqlException arm does
        // that, and it only works because it is ordered after the specific ones.
        //
        // The message used to say "the layer's data source, not the server".
        // Stopping the datastore for the ADR-017 condition 1 test showed it
        // saying exactly that while the *platform store* was what had failed,
        // which sends an administrator to the wrong database during an outage.
        // It now names the two endpoints that can tell them apart.
        (int status, string message) = ErrorResponse.Classify(WithSqlState("23505"));

        Assert.Equal(503, status);
        Assert.Contains("/healthz/ready", message, StringComparison.Ordinal);
        Assert.Contains("/admin/health", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_failure_is_500_and_leaks_nothing()
    {
        (int status, string message) = ErrorResponse.Classify(
            new InvalidOperationException("connection string Password=hunter2 was rejected"));

        Assert.Equal(500, status);

        // The endpoint is reachable without authentication until ADR-015 is
        // implemented, so the exception's own text must never reach the caller.
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
        Assert.Contains("server log", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_request_is_distinguishable_from_a_server_failure()
    {
        (int status, _) = ErrorResponse.Classify(new OperationCanceledException());

        Assert.Equal(499, status);
    }

    [Fact]
    public void Every_message_tells_the_caller_something_they_can_act_on()
    {
        // A guard on the class rather than on one branch. The failure this
        // catches is a future arm added with a message like "an error occurred",
        // which is the exact thing this class was written to replace.
        //
        // 499 is deliberately excluded, and the exclusion is the interesting
        // part: the caller has already disconnected, so that message is written
        // for the log and read by nobody. Holding it to the same standard would
        // mean inventing advice for an audience that does not exist. This test
        // failed on exactly that before the invariant was stated properly.
        Exception[] readByACaller =
        [
            WithSqlState("57014"), WithSqlState("42P01"), WithSqlState("42501"),
            WithSqlState("23505"), new InvalidOperationException(),
            new NpgsqlException("read", new TimeoutException()),
        ];

        foreach (Exception exception in readByACaller)
        {
            (_, string message) = ErrorResponse.Classify(exception);

            Assert.True(
                message.Length > 40,
                $"{exception.GetType().Name} maps to '{message}', which is too short to say what "
                + "to do about it.");
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }
}
