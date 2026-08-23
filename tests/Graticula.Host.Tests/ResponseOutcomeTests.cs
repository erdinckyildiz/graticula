using System;
using System.Threading;
using Graticula.Host;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// What the access log records when a response does not reach its caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-132](../../docs/architecture-debt.md), and the row named the symptom one step
/// away from the cause.</b> It says the access log records <c>- 200</c> for a response
/// nobody received, which happens when the whole document fits a socket buffer the client
/// never drains. **Measured 2026-08-23, the commoner case is worse:** two connections
/// reset during a 4000×4000 <c>GetMap</c> produced no access line at all, because the
/// middleware logged after <c>await next()</c> and an aborted pipeline unwinds straight
/// past that. A failed request the log does not mention is worse than one it mislabels —
/// a reader counting requests never sees a gap.
/// </para>
/// <para>
/// <b>The live half is in that row: both resets now log 499.</b> This is the half a
/// measurement cannot pin down — the three-way distinction between *they left*, *we
/// broke* and *it was fine* — because provoking a server-side mid-write failure on demand
/// means breaking the server.
/// </para>
/// </remarks>
public sealed class ResponseOutcomeTests
{
    private static DefaultHttpContext Request(int status, CancellationToken aborted)
    {
        DefaultHttpContext context = new();
        context.Response.StatusCode = status;
        context.RequestAborted = aborted;

        return context;
    }

    [Fact]
    public void An_untroubled_request_keeps_its_own_status()
    {
        HttpContext context = Request(200, CancellationToken.None);

        Assert.Equal(200, ResponseOutcome.StatusFor(context));
    }

    /// <summary>
    /// A client that hung up is 499, even though the header said 200.
    /// </summary>
    /// <remarks>
    /// <b>This is the case with no exception at all.</b> The handler finished writing
    /// while the client was already gone: nothing threw, nothing was aborted by us, and
    /// the bytes went nowhere. The token is the whole of the evidence, and it is
    /// trustworthy here precisely because this server did not abort.
    /// </remarks>
    [Fact]
    public void A_client_that_left_is_recorded_as_having_left()
    {
        using CancellationTokenSource gone = new();
        gone.Cancel();

        HttpContext context = Request(200, gone.Token);

        Assert.Equal(ResponseOutcome.ClientLeft, ResponseOutcome.StatusFor(context));
    }

    /// <summary>
    /// A truncation this server caused is 500, not 499.
    /// </summary>
    /// <remarks>
    /// <b>The assertion that stops the fix being a plausible-looking mistake.</b>
    /// Aborting a response cancels <c>RequestAborted</c>, so a naive read of that token
    /// reports every server-side failure as the client's fault — which is 200's error with
    /// a convincing number on it, and it sends whoever is investigating to look at the
    /// network. The marker is set before the abort for exactly this reason.
    /// </remarks>
    [Fact]
    public void A_failure_this_server_caused_is_not_blamed_on_the_client()
    {
        using CancellationTokenSource gone = new();

        HttpContext context = Request(200, gone.Token);

        // A projection that threw on the thousandth row: not a cancellation at all.
        ResponseOutcome.Truncated(context, new InvalidOperationException("row 1000"));

        // And then the response is aborted, which is what cancels the token.
        gone.Cancel();

        Assert.Equal(ResponseOutcome.ServerBroke, ResponseOutcome.StatusFor(context));
    }

    /// <summary>
    /// A cancellation while the client is already gone is the client leaving.
    /// </summary>
    [Fact]
    public void A_cancellation_from_a_departed_client_is_the_client_leaving()
    {
        using CancellationTokenSource gone = new();
        gone.Cancel();

        HttpContext context = Request(200, gone.Token);

        ResponseOutcome.Truncated(context, new OperationCanceledException());

        Assert.Equal(ResponseOutcome.ClientLeft, ResponseOutcome.StatusFor(context));
    }

    /// <summary>
    /// A deadline that expired is this server stopping the request, not the client.
    /// </summary>
    /// <remarks>
    /// <b>Both throw <see cref="OperationCanceledException"/> and they deserve opposite
    /// answers.</b> The request deadline cancels its own linked token, which does not
    /// cancel <c>RequestAborted</c> — so the type alone cannot tell them apart and the
    /// token is what distinguishes them.
    /// </remarks>
    [Fact]
    public void A_deadline_is_not_the_client_leaving()
    {
        using CancellationTokenSource connection = new();

        HttpContext context = Request(200, connection.Token);

        ResponseOutcome.Truncated(context, new OperationCanceledException());

        Assert.Equal(ResponseOutcome.ServerBroke, ResponseOutcome.StatusFor(context));
    }
}
