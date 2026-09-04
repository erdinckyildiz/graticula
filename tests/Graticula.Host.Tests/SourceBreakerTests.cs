using System;
using Graticula.Host;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A source that failed a moment ago is not asked again, and one that answered is.
/// </summary>
/// <remarks>
/// <b>The live measurement is in [D-131](../../docs/architecture-debt.md): 8.0 seconds a
/// refusal became 10–20 milliseconds, and 45 seconds of sustained outage went from every
/// request slow to 51 of 55 instant.</b> These are the parts a measurement cannot pin
/// down: that a database saying *no* does not count as a database being away, and that the
/// window is longer than the failure it protects against — which the first attempt got
/// wrong, with a three-second window against a four-second connect, and the eight measured
/// requests were unchanged at 8.0 seconds each.
/// </remarks>
public sealed class SourceBreakerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private const string Source = "Host=localhost;Port=5432;Database=gis";

    private static (SourceBreaker Breaker, Func<DateTimeOffset> Clock) At(DateTimeOffset[] now)
    {
        SourceBreaker breaker = new(null, () => now[0]);
        return (breaker, () => now[0]);
    }

    [Fact]
    public void A_source_nothing_has_said_about_is_asked()
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        Assert.False(breaker.IsOpen(Source));
    }

    [Fact]
    public void A_source_that_could_not_be_reached_is_left_alone()
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        Assert.True(breaker.Failed(Source, new NpgsqlException("Failed to connect")));
        Assert.True(breaker.IsOpen(Source));
    }

    /// <summary>
    /// A database that answered is not a database that is away.
    /// </summary>
    /// <remarks>
    /// <b>The assertion that keeps this from being dangerous.</b> Tripping on a
    /// <c>PostgresException</c> would take a whole service down over one malformed filter,
    /// or one missing column, or one statement timeout — a far worse failure than the eight
    /// seconds this class exists to remove. The discriminator is that PostgreSQL received
    /// the request and replied.
    /// </remarks>
    [Fact]
    public void A_database_that_refused_a_query_is_not_treated_as_unreachable()
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        PostgresException answered = new(
            "column does not exist", "ERROR", "ERROR", "42703");

        Assert.False(breaker.Failed(Source, answered));
        Assert.False(breaker.IsOpen(Source));
        Assert.False(SourceBreaker.Unreachable(answered));
    }

    /// <summary>
    /// A database saying it is going away is away, whatever kind of exception says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The four states where PostgreSQL's answer *is* that it cannot answer.</b>
    /// <c>57P01</c> is what every open connection receives when the server is shut down or
    /// restarted, <c>57P02</c> when it crashes, <c>57P03</c> when it is starting and not yet
    /// taking work, and <c>53300</c> when there are no connection slots left — a store that
    /// exists and cannot serve us, which from here is the same thing.
    /// </para>
    /// <para>
    /// <b>Written from a failure rather than from the manual.</b> CI run 33923883963:
    /// the platform container was stopped, every connection got <c>57P01</c>, and because
    /// that is a <c>PostgresException</c> the shape cache treated it as *the database
    /// answered* and refused a publicly shared service that ADR-026 says must keep being
    /// described. D-224.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("57P01")]
    [InlineData("57P02")]
    [InlineData("57P03")]
    [InlineData("53300")]
    public void A_database_that_says_it_is_going_away_is_unreachable(string state)
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        PostgresException leaving = new("shutting down", "FATAL", "FATAL", state);

        Assert.True(SourceBreaker.Unreachable(leaving));
        Assert.True(breaker.Failed(Source, leaving));
        Assert.True(breaker.IsOpen(Source));
    }

    /// <summary>
    /// The two answers to <em>is this database unreachable</em> are one answer.
    /// </summary>
    /// <remarks>
    /// <b>Because they were two, and they disagreed in production.</b>
    /// <c>CatalogFallback.IsUnreachable</c> knew the four going-away states and this one did
    /// not, so during the same outage the catalogue kept serving and the shapes stopped.
    /// This asserts they cannot drift apart again on a state either of them learns about:
    /// the list lives in one place and this is the other caller reading it.
    /// </remarks>
    [Theory]
    [InlineData("57P01")]
    [InlineData("53300")]
    [InlineData("42703")]
    [InlineData("23505")]
    [InlineData("XX000")]
    public void The_shape_cache_and_the_catalogue_agree_about_what_being_away_means(string state)
    {
        PostgresException said = new("said", "ERROR", "ERROR", state);

        Assert.Equal(
            Graticula.Platform.Postgres.CatalogFallback.IsUnreachable(said),
            SourceBreaker.Unreachable(said));
    }

    /// <summary>
    /// A Postgres error wrapped in something else is still an answer.
    /// </summary>
    /// <remarks>
    /// <b>Because the wrapping is not the caller's choice.</b> A provider that catches and
    /// rethrows would otherwise turn every bad query into an outage, and the inner
    /// exception is the one that says what really happened.
    /// </remarks>
    [Fact]
    public void A_wrapped_database_answer_is_still_an_answer()
    {
        Assert.False(SourceBreaker.Unreachable(
            new InvalidOperationException(
                "while reading",
                new PostgresException("no such column", "ERROR", "ERROR", "42703"))));
    }

    /// <summary>
    /// The window is longer than the failure it protects against.
    /// </summary>
    /// <remarks>
    /// <b>This is a test of a number, and the number was wrong once.</b> The first attempt
    /// used three seconds against a measured four-second blackholed connect, so a caller
    /// making one request at a time always found the breaker cooled by the time it came
    /// back — eight measured refusals, all still 8.0 seconds. A window shorter than the
    /// failure only helps callers that overlap, which is the opposite of the case D-131 is
    /// about.
    /// </remarks>
    [Fact]
    public void The_cooling_window_outlasts_a_failed_connect()
    {
        // Measured 2026-08-23 against a stopped container: one blackholed connect is 4.0 s.
        TimeSpan measuredFailure = TimeSpan.FromSeconds(4);

        Assert.True(
            SourceBreaker.Cooling > measuredFailure * 2,
            $"A {SourceBreaker.Cooling.TotalSeconds:0.#} s window against a "
            + $"{measuredFailure.TotalSeconds:0.#} s failure means a serial caller finds it "
            + "cooled every time, which is what happened when it was three seconds.");
    }

    [Fact]
    public void The_window_expires_and_the_source_is_tried_again()
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        breaker.Failed(Source, new NpgsqlException("Failed to connect"));

        now[0] = Start + SourceBreaker.Cooling - TimeSpan.FromMilliseconds(1);
        Assert.True(breaker.IsOpen(Source), "still inside the window");

        now[0] = Start + SourceBreaker.Cooling;
        Assert.False(breaker.IsOpen(Source), "the window has passed, so try once");
    }

    [Fact]
    public void A_source_that_answers_is_forgiven_at_once()
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        breaker.Failed(Source, new NpgsqlException("Failed to connect"));
        Assert.True(breaker.IsOpen(Source));

        breaker.Succeeded(Source);
        Assert.False(breaker.IsOpen(Source));
    }

    /// <summary>
    /// One source failing does not silence another.
    /// </summary>
    /// <remarks>
    /// <b>ADR-007 §4.8 says *per data source* and the reason is blast radius.</b> A hundred
    /// layers over one database share a pool and a bound; a second database is a separate
    /// failure, and one customer's PostGIS going away must not refuse requests against
    /// anybody else's.
    /// </remarks>
    [Fact]
    public void One_source_failing_says_nothing_about_another()
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        breaker.Failed(Source, new NpgsqlException("Failed to connect"));

        Assert.False(breaker.IsOpen("Host=elsewhere;Port=5432;Database=other"));
        Assert.False(breaker.IsOpen(SourceBreaker.PlatformStore));
    }

    /// <summary>
    /// The platform store has its own state, reachable through the narrow interface.
    /// </summary>
    /// <remarks>
    /// <b>The catalogue read is the second of D-131's two four-second waits</b>, and it
    /// happens in an assembly that cannot see this type — so it asks through
    /// <see cref="Graticula.Platform.Catalog.IStoreHealth"/>. This asserts the two views
    /// agree, because a breaker the catalogue cannot see is a breaker that only fixed half
    /// the problem, which is exactly what the first measurement showed.
    /// </remarks>
    [Fact]
    public void The_platform_store_answers_through_the_narrow_interface()
    {
        DateTimeOffset[] now = [Start];
        (SourceBreaker breaker, _) = At(now);

        Graticula.Platform.Catalog.IStoreHealth health = breaker;

        Assert.False(health.IsOpen);

        health.Failed(new NpgsqlException("Failed to connect"));

        Assert.True(health.IsOpen);
        Assert.True(breaker.IsOpen(SourceBreaker.PlatformStore));

        health.Succeeded();

        Assert.False(health.IsOpen);
    }
}
