using System;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// What actually happened to a response, for the access log to write down.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-132](../../docs/architecture-debt.md): the access log recorded <c>- 200</c> for
/// responses nobody received.</b> <c>Response.StatusCode</c> is fixed the moment the
/// headers go out, so every failure after that — a client hanging up, a projection
/// throwing on the thousandth row — is logged as the success the header promised.
/// <c>grep -c ' - 499'</c> over a 6.6 MB log returned **1**, and that one was ArcGIS's own
/// *Token Required*, which uses the number for an unrelated meaning. The disconnect 499
/// had never been written.
/// </para>
/// <para>
/// <b>The question this exists to answer is *did they leave or did we break*</b>, and it
/// is the question somebody asks when a client reports truncation. Those have opposite
/// next steps — look at the network, or look at the server — and an access log that says
/// 200 sends them to neither.
/// </para>
/// <para>
/// <b><c>RequestAborted</c> alone cannot tell them apart, which is why this is a marker
/// and not a one-line check.</b> Kestrel cancels that token when the client disconnects
/// *and* when the server calls <see cref="HttpContext.Abort"/> — which is exactly what a
/// server-side truncation does, to stop the client mistaking a short document for a
/// complete one. So a naive read reports every one of our own failures as the client's
/// fault, which is worse than 200: it is 200's error with a plausible number on it.
/// </para>
/// <para>
/// <b>499 is nginx's and not IANA's</b>, and this server already reasoned that through
/// once — see <c>ErrorResponse.Classify</c>. It goes in the log and never on the wire,
/// because by the time it is known there is no status line left to write.
/// </para>
/// </remarks>
internal static class ResponseOutcome
{
    /// <summary>The status the log records when a caller hangs up mid-response.</summary>
    public const int ClientLeft = 499;

    /// <summary>The status the log records when this server fails mid-response.</summary>
    /// <remarks>
    /// <b>500 rather than the header's status, because the header's status is a lie by
    /// then.</b> A response that announced 200 and then stopped is a failed request, and
    /// an operator counting 500s during an incident should see it.
    /// </remarks>
    public const int ServerBroke = 500;

    private const string Key = "graticula.truncated";

    /// <summary>
    /// Records that a response failed after bytes were on the wire.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="exception">What went wrong.</param>
    /// <remarks>
    /// <b>Called before <see cref="HttpContext.Abort"/>, not after.</b> The abort cancels
    /// <c>RequestAborted</c>, so afterwards there is no way left to tell whose fault it
    /// was — the marker has to be set while the answer is still knowable. Every caller
    /// here is a streaming writer's catch block, which is that moment.
    /// </remarks>
    public static void Truncated(HttpContext context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        /*
          <b>The client's own cancellation is the one signal that means they left.</b>
          Read before the abort, so it reflects the connection rather than our reaction to
          it. An `OperationCanceledException` on its own is not enough: the request
          deadline throws the same type, and that is the server stopping the request.
        */
        bool left = context.RequestAborted.IsCancellationRequested
            && exception is OperationCanceledException;

        context.Items[Key] = left ? ClientLeft : ServerBroke;
    }

    /// <summary>
    /// Records the status an exception that escaped the endpoint will be answered with, while the
    /// response has not started.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="exception">What escaped.</param>
    /// <remarks>
    /// <para>
    /// <b>The access log sits inside the exception handler, so it ran before the answer was
    /// written.</b> An endpoint that threw unwound through the log's <c>finally</c> while
    /// <c>Response.StatusCode</c> was still its default 200, and only then did
    /// <c>ErrorResponse</c> turn the exception into a 503 on the wire. Every failure that reached
    /// the handler was stored as 200: found 2026-09-15 when an attachment upload that answered 503
    /// showed as 200 on the Logs screen, whose newest 503 was three weeks old.
    /// </para>
    /// <para>
    /// <b>Asked of the same classification the handler uses</b> — <c>ErrorResponse.Classify</c>
    /// and the deadline check in front of it — so the log and the wire cannot disagree about the
    /// number. A response that had already started keeps whatever <see cref="Truncated"/>
    /// recorded, and one with nothing recorded is left to <see cref="StatusFor"/>.
    /// </para>
    /// </remarks>
    public static void Threw(HttpContext context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        if (context.Response.HasStarted || context.Items.ContainsKey(Key))
        {
            return;
        }

        context.Items[Key] = exception is OperationCanceledException && RequestDeadline.Expired(context)
            ? StatusCodes.Status504GatewayTimeout
            : exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested
                ? ClientLeft
                : ErrorResponse.Classify(exception).Status;
    }

    /// <summary>
    /// The status the access log should record for this request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The status.</returns>
    /// <remarks>
    /// <b>The marker when there is one, and the response's own status otherwise.</b> The
    /// last clause catches the case with no exception at all: a handler that finished
    /// writing while the client was already gone. Nothing threw, nothing was aborted by
    /// us, and the bytes went nowhere — so the token is the whole of the evidence and it
    /// is trustworthy here precisely because we did not abort.
    /// </remarks>
    public static int StatusFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(Key, out object? recorded) && recorded is int status)
        {
            return status;
        }

        return context.RequestAborted.IsCancellationRequested
            ? ClientLeft
            : context.Response.StatusCode;
    }
}
