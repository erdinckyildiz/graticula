using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// How long a client may occupy a service, for every service rather than one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner requirement, restated 2026-08-18 over the reference's Pooling page:</b> *"sadece
/// geometri değil, tüm servislerde timeout olmalı"*. The first delivery of that requirement was
/// narrowed to the geometry service, and the correction is the reason this exists.
/// </para>
/// <para>
/// <b>Tested at the middleware rather than through a query, deliberately.</b> Proving a timeout by
/// finding a request slow enough to hit it makes the test a measurement of the machine — which is
/// exactly D-43, where three tests failed on a cold page cache and passed on a warm one. A handler
/// that waits for a token is deterministic on any machine.
/// </para>
/// </remarks>
public sealed class RequestDeadlineTests
{
    /// <summary>A request that outlives its deadline is cancelled.</summary>
    [Fact]
    public async Task A_request_past_its_deadline_is_cancelled()
    {
        DefaultHttpContext context = new();
        bool ran = false;

        RequestDeadline.Middleware middleware = new(
            async _ =>
            {
                ran = true;
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            },
            TimeSpan.FromMilliseconds(120));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context));

        Assert.True(ran, "The handler never ran, so nothing was bounded.");
    }

    /// <summary>
    /// A request that finishes in time is untouched, and the deadline is put back.
    /// </summary>
    /// <remarks>
    /// <b>The second half matters more than it looks.</b> Kestrel reads
    /// <c>RequestAborted</c> after the pipeline unwinds, so leaving it pointing at a disposed
    /// source is an <c>ObjectDisposedException</c> in framework code — a failure that would appear
    /// as a broken server rather than as a broken middleware.
    /// </remarks>
    [Fact]
    public async Task A_request_that_finishes_in_time_is_untouched()
    {
        DefaultHttpContext context = new();
        CancellationToken original = context.RequestAborted;
        bool ran = false;

        RequestDeadline.Middleware middleware = new(
            _ =>
            {
                ran = true;
                Assert.NotNull(RequestDeadline.Of(context));
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(30));

        await middleware.InvokeAsync(context);

        Assert.True(ran);
        Assert.Equal(original, context.RequestAborted);
        // The deadline is still readable: the exception handler needs it, and `Items` is per
        // request so there is nothing to clean up.
        Assert.NotNull(RequestDeadline.Of(context));
    }

    /// <summary>
    /// A service may shorten the deadline, and that is the whole per-service story.
    /// </summary>
    /// <remarks>
    /// <b>Lowering is the only operation offered</b>, which is ADR-031 §4's *may only lower* rule
    /// expressed as an API rather than as a check somebody has to remember to write. A per-service
    /// override that could raise the bound would be a per-service way around the deployment's.
    /// </remarks>
    [Fact]
    public async Task A_service_may_lower_the_deadline()
    {
        DefaultHttpContext context = new();

        RequestDeadline.Middleware middleware = new(
            async _ =>
            {
                RequestDeadline.LowerTo(context, TimeSpan.FromMilliseconds(120));

                Assert.Equal(
                    TimeSpan.FromMilliseconds(120),
                    RequestDeadline.Of(context)!.Allowed);

                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            },
            TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context));
    }

    /// <summary>Asking for more time than the server allows changes nothing.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(3600)]
    public async Task A_service_cannot_raise_the_deadline(int wanted)
    {
        DefaultHttpContext context = new();
        TimeSpan server = TimeSpan.FromSeconds(10);

        RequestDeadline.Middleware middleware = new(
            _ =>
            {
                RequestDeadline.LowerTo(context, TimeSpan.FromSeconds(wanted));

                // Unchanged, and silently: a service asking for more is not making an error,
                // it is being held to the deployment's limit.
                Assert.Equal(server, RequestDeadline.Of(context)!.Allowed);
                return Task.CompletedTask;
            },
            server);

        await middleware.InvokeAsync(context);
    }

    /// <summary>A service cannot take the bound off by asking for nothing.</summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-031](../../docs/adr/ADR-031-service-capability-configuration.md) condition 3, the
    /// third of its three bounds.</b> The condition names them together — the override *can lower
    /// and cannot raise or unset* — and the first two had tests while the third did not. Unsetting
    /// is the one that matters most and looks least like an attack: nought and null are what a
    /// configuration says when it has nothing to say, so an implementation that treated them as
    /// *no limit* rather than as *no opinion* would read as reasonable and would hand every
    /// service a way out of the deployment's bound.
    /// </para>
    /// <para>
    /// <b>Negative is here for the same reason zero is.</b> `TimeSpan` is signed, the column is
    /// milliseconds, and a value that arrived through arithmetic rather than through the admin API
    /// can be negative — where <c>CancelAfter</c> would throw rather than refuse.
    /// </para>
    /// <para>
    /// The admin API refuses a non-positive timeout as well
    /// (<c>ServiceCapabilityLimitsTests.A_timeout_that_is_not_positive_is_refused</c>). That is a
    /// second defence rather than the same one: this asserts the behaviour of the seam every
    /// request goes through, which is where a value that reached the store some other way arrives.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public async Task A_service_cannot_unset_the_deadline(int seconds)
    {
        DefaultHttpContext context = new();
        TimeSpan server = TimeSpan.FromSeconds(10);

        RequestDeadline.Middleware middleware = new(
            _ =>
            {
                RequestDeadline.LowerTo(context, TimeSpan.FromSeconds(seconds));

                Assert.NotNull(RequestDeadline.Of(context));

                Assert.Equal(server, RequestDeadline.Of(context)!.Allowed);

                // And the token is still the deadline's rather than the one the request arrived
                // with, because *the bound is still installed* is the half of this an assertion
                // about `Allowed` alone would not catch.
                Assert.True(context.RequestAborted.CanBeCanceled);

                return Task.CompletedTask;
            },
            server);

        await middleware.InvokeAsync(context);
    }

    /// <summary>Saying nothing at all leaves the deployment's bound where it is.</summary>
    /// <remarks>
    /// <b>Null is the common case and the one the signature invites.</b> <c>LowerTo</c> takes a
    /// nullable so that the seam reading a nullable column does not write the same null check at
    /// every call site — which means null reaches this method on almost every request, and *almost
    /// every request* is the worst thing to leave untested.
    /// </remarks>
    [Fact]
    public async Task A_service_with_no_opinion_leaves_the_deadline_alone()
    {
        DefaultHttpContext context = new();
        TimeSpan server = TimeSpan.FromSeconds(10);

        RequestDeadline.Middleware middleware = new(
            _ =>
            {
                RequestDeadline.LowerTo(context, null);

                Assert.Equal(server, RequestDeadline.Of(context)!.Allowed);
                return Task.CompletedTask;
            },
            server);

        await middleware.InvokeAsync(context);
    }

    /// <summary>Nought means no bound, which a deployment may choose.</summary>
    /// <remarks>
    /// <b>It is also the behaviour of every build before this one.</b> A deployment with its own
    /// front-end timeout can say so rather than having two bounds disagree about the same request —
    /// and a build that silently imposed one would change what those deployments do on upgrade.
    /// </remarks>
    [Fact]
    public async Task Nought_means_no_bound_and_nothing_is_installed()
    {
        DefaultHttpContext context = new();
        CancellationToken original = context.RequestAborted;
        bool ran = false;

        RequestDeadline.Middleware middleware = new(
            _ =>
            {
                ran = true;

                // Nothing to lower and nothing to report: the request is not on a deadline at
                // all, which is different from being on a very long one.
                Assert.Null(RequestDeadline.Of(context));
                RequestDeadline.LowerTo(context, TimeSpan.FromMilliseconds(1));
                return Task.CompletedTask;
            },
            TimeSpan.Zero);

        await middleware.InvokeAsync(context);

        Assert.True(ran);
        Assert.Equal(original, context.RequestAborted);
    }

    /// <summary>
    /// The deadline firing and the client hanging up are told apart.
    /// </summary>
    /// <remarks>
    /// <b>They throw the same exception and deserve opposite answers.</b> 499 exists so an access
    /// log can say *they left*; saying that about a request the server itself stopped sends whoever
    /// reads it to look at the network. So the deadline records which happened, and
    /// <c>ErrorResponse</c> reads it.
    /// </remarks>
    [Fact]
    public async Task A_deadline_that_fired_is_not_reported_as_a_caller_going_away()
    {
        DefaultHttpContext ours = new();

        RequestDeadline.Middleware timing = new(
            async _ => await Task.Delay(Timeout.Infinite, ours.RequestAborted),
            TimeSpan.FromMilliseconds(120));

        bool expired = false;

        try
        {
            await timing.InvokeAsync(ours);
        }
        catch (OperationCanceledException)
        {
            // <b>Read after the middleware has unwound, because that is where the exception
            // handler reads it.</b> The first version of the middleware removed the entry in
            // its `finally`, which made `Expired` always false and would have answered every
            // timeout with 499 — *the caller went away* — about a request the server itself
            // stopped. This assertion is what found that.
            expired = ours.Items.TryGetValue("gis-request-deadline", out object? held)
                && held is RequestDeadline { Fired: true };
        }

        Assert.True(expired, "A deadline that fired was not recorded as having fired.");
    }

    /// <summary>A client that goes away is not reported as a timeout.</summary>
    [Fact]
    public async Task A_caller_going_away_is_not_reported_as_a_deadline()
    {
        using CancellationTokenSource gone = new();

        DefaultHttpContext context = new() { RequestAborted = gone.Token };

        bool blamedTheDeadline = true;

        RequestDeadline.Middleware middleware = new(
            async _ =>
            {
                await gone.CancelAsync();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            },
            TimeSpan.FromSeconds(30));

        try
        {
            await middleware.InvokeAsync(context);
        }
        catch (OperationCanceledException)
        {
            blamedTheDeadline =
                context.Items.TryGetValue("gis-request-deadline", out object? held)
                && held is RequestDeadline { Fired: true };
        }

        Assert.False(
            blamedTheDeadline,
            "A client that hung up was recorded as a timeout, so the log would say the request "
            + "was too slow when nobody was waiting for it.");
    }
}
