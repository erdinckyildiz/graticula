using System;
using System.Reflection;
using Graticula.Platform.Secrets;
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
    public void A_duplicate_is_a_conflict_rather_than_an_outage()
    {
        // <b>The fourth time this file has confused a caller's mistake for a
        // connectivity failure</b> — after 42883-for-schema, 42703, and the statement
        // timeout that arrived "wearing the connectivity costume". A name already taken
        // is something the caller can fix, and telling them the database is down sends
        // them to look at a database that is working.
        (int status, string message) = ErrorResponse.Classify(WithSqlState("23505"));

        Assert.Equal(409, status);
        Assert.Contains("already registered", message, StringComparison.Ordinal);
        Assert.Contains("healthy", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_postgres_error_is_503_rather_than_falling_through_to_500()
    {
        // <b>The example moved on 2026-08-21, and the reason is the point of the
        // test.</b> This used 23505 as its unclassified code; 23505 is a unique
        // violation, and registering the same coverage twice showed a publisher being
        // told the database was unreachable when it had simply refused a duplicate. So
        // 23505 now has a case of its own and answers 409, and this test needs a code
        // that genuinely has none. 40001 is a serialisation failure — real, rare, and
        // nothing here reasons about it.
        //
        // What is under test is unchanged: an error this file does not recognise must
        // still be a 503 attributed to a database rather than to our own logic. The
        // NpgsqlException arm does that, and it only works because it is ordered after
        // every specific one.
        //
        // The message used to say "the layer's data source, not the server".
        // Stopping the datastore for the ADR-017 condition 1 test showed it
        // saying exactly that while the *platform store* was what had failed,
        // which sends an administrator to the wrong database during an outage.
        // It now names the two endpoints that can tell them apart.
        (int status, string message) = ErrorResponse.Classify(WithSqlState("40001"));

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

    /// <summary>Every exception this switch has an arm for, one of each.</summary>
    /// <remarks>
    /// <b>Enumerated here rather than per test</b>, so a new arm is covered by whichever
    /// invariant it breaks rather than by somebody remembering to widen a list — the
    /// property [D-46](../../docs/architecture-debt.md) says is the difference between a
    /// fix and a fix in one of the places that carry it.
    /// </remarks>
    public static TheoryData<string, Exception> EveryArm()
    {
        TheoryData<string, Exception> arms = [];

        PostgresException srid = new(
            messageText: "Invalid reserved SRID 900913", severity: "ERROR",
            invariantSeverity: "ERROR", sqlState: "XX000");

        PostgresException mismatch = new(
            messageText: "operator does not exist: timestamp with time zone = text",
            severity: "ERROR", invariantSeverity: "ERROR", sqlState: "42883");

        arms.Add("bad SRID", srid);
        arms.Add("breaker open", new SourceUnreachableException("The layer's database is unreachable."));
        arms.Add("budget full", new ConnectionBudgetFullException());
        arms.Add("statement timeout", WithSqlState("57014"));
        arms.Add("client timeout", new NpgsqlException("read", new TimeoutException()));
        arms.Add("duplicate", WithSqlState("23505"));
        arms.Add("dropped table", WithSqlState("42P01"));
        arms.Add("filter type mismatch", mismatch);
        arms.Add("missing function", WithSqlState("42883"));
        arms.Add("missing column", WithSqlState("42703"));
        arms.Add("credential refused", WithSqlState("42501"));
        arms.Add("password refused", WithSqlState("28P01"));
        arms.Add("secret unopenable", new SecretProtectionException("sealed with another key"));
        arms.Add("database down", new NpgsqlException("no route to host"));
        arms.Add("caller left", new OperationCanceledException());
        arms.Add("anything else", new InvalidOperationException("boom"));

        return arms;
    }

    /// <summary>
    /// What an unprivileged caller reads names no provider, no internal address and no
    /// message the store wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-03](../../docs/architecture-debt.md), from review G7, stated 2026-08-12 in
    /// [security.md](../../docs/security.md) §5 and unimplemented until 2026-08-24.</b>
    /// The rule is that detail is authorization-scoped: an authenticated administrator
    /// sees the provider and the reason, anybody else sees the capability in abstract
    /// terms and a generic refusal. Before this test, fourteen of the sixteen arms told
    /// an anonymous caller something about what sits behind the server — five named
    /// PostGIS or the engine, two echoed the store's own message text verbatim, and four
    /// sent the reader to an `/admin` address they cannot open.
    /// </para>
    /// <para>
    /// <b>It reads the public form rather than checking that one was written.</b> An arm
    /// added with no anonymous sentence falls back to the operator's, so this fails on
    /// what that sentence says — which is the failure a reviewer would otherwise have to
    /// notice as an absent argument.
    /// </para>
    /// </remarks>
    /// <param name="arm">Which failure, for the message.</param>
    /// <param name="exception">The failure.</param>
    [Theory]
    [MemberData(nameof(EveryArm))]
    public void Every_refusal_an_anonymous_caller_can_reach_is_free_of_the_provider(
        string arm, Exception exception)
    {
        (_, string message) = ErrorResponse.Classify(exception, detailed: false);

        // The engine and its extension, the addresses only an administrator can open, the
        // configuration this server reads, and the two words that say there are two stores.
        string[] disclosures =
        [
            "postgis", "postgres", "npgsql", "search_path", "/admin", "/healthz",
            "platform store", "data source", "credential", "the database",
        ];

        foreach (string disclosure in disclosures)
        {
            Assert.False(
                message.Contains(disclosure, StringComparison.OrdinalIgnoreCase),
                $"The '{arm}' refusal tells a caller without admin:manageServer about "
                + $"'{disclosure}': \"{message}\". security.md §5 keeps the provider and the "
                + "reason for an authenticated administrator; everybody else gets the "
                + "capability in abstract terms. D-03.");
        }
    }

    /// <summary>An administrator still gets the sentence that says what to do.</summary>
    /// <remarks>
    /// <b>The half that makes the other half safe to have.</b> Scoping detail is only
    /// defensible if the detail still reaches somebody — otherwise it is deleting the
    /// diagnosis and calling it security. Each of these is an arm whose operator sentence
    /// exists to send a person to a particular system, and this asserts they still arrive.
    /// </remarks>
    [Fact]
    public void An_administrator_still_reads_the_provider_and_the_reason()
    {
        (_, string missingFunction) = ErrorResponse.Classify(WithSqlState("42883"), detailed: true);
        Assert.Contains("PostGIS", missingFunction, StringComparison.Ordinal);
        Assert.Contains("search_path", missingFunction, StringComparison.Ordinal);

        (_, string down) = ErrorResponse.Classify(
            new NpgsqlException("no route to host"), detailed: true);
        Assert.Contains("/admin/health", down, StringComparison.Ordinal);

        // Default true, because every existing caller that knows who is asking passes it
        // explicitly and the tests above read the operator's form.
        Assert.Equal(missingFunction, ErrorResponse.Classify(WithSqlState("42883")).Message);
    }

    /// <summary>
    /// A refusal an anonymous caller reads still tells them something they can act on.
    /// </summary>
    /// <remarks>
    /// <b>The generic form is where advice goes to die</b>, and *an error occurred* passed
    /// for a refusal in this file's own history — which is why
    /// <c>Every_message_tells_the_caller_something_they_can_act_on</c> exists for the
    /// operator's form. The public form needs the same guard, or scoping detail becomes a
    /// licence to say nothing. 499 is excluded for the reason it always is: the caller has
    /// already gone.
    /// </remarks>
    /// <param name="arm">Which failure, for the message.</param>
    /// <param name="exception">The failure.</param>
    [Theory]
    [MemberData(nameof(EveryArm))]
    public void Every_public_refusal_still_says_what_to_do(string arm, Exception exception)
    {
        (int status, string message) = ErrorResponse.Classify(exception, detailed: false);

        if (status == 499)
        {
            return;
        }

        Assert.True(
            message.Length > 40,
            $"The '{arm}' refusal reads '{message}' to a caller without admin:manageServer, "
            + "which is too short to say what to do about it. Scoping the detail is not a "
            + "licence to drop the advice.");
        Assert.EndsWith(".", message, StringComparison.Ordinal);
    }
}
