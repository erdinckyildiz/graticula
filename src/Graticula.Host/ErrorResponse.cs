using System;
using System.Globalization;
using System.Threading.Tasks;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Wfs;
using Graticula.Api.Wms;
using Graticula.Platform.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Turns an unhandled exception into a response a caller can act on.
/// </summary>
/// <remarks>
/// <para>
/// ADR-017 §6: an error says what went wrong and what to do about it. The
/// default for an unhandled exception is a 500 with an empty body, which tells
/// the caller only that they should try again — advice that is wrong for most of
/// the failures below.
/// </para>
/// <para>
/// <b>Two audiences, deliberately separated.</b> The log gets the exception; the
/// response gets a sentence. A response that carried the stack trace would be
/// convenient for the operator and a map of the server for everyone else, and
/// this endpoint is unauthenticated until ADR-015 is implemented.
/// </para>
/// <para>
/// <b>Once bytes exist, no clean answer is possible.</b> Feature responses
/// stream, and a failure partway through leaves a truncated document that
/// nothing can prepend a status to. The query endpoint therefore executes the
/// query and pulls its first row <em>before</em> writing anything, which moves
/// every failure that this class classifies to a point where the response is
/// still empty. What remains — the database dying mid-result — is aborted and
/// logged rather than answered.
/// </para>
/// <para>
/// The <c>HasStarted</c> check below is a backstop for handlers that do not
/// stream, and is <b>not</b> sufficient on its own: a response has not "started"
/// until the pipe flushes to the socket, so a complete JSON header can be
/// sitting in the buffer while it still reads false. The query endpoint asks
/// <c>Utf8JsonWriter.BytesPending</c> instead. That distinction produced a real
/// 504 carrying malformed JSON before it was understood.
/// </para>
/// </remarks>
internal static class ErrorResponse
{
    /// <summary>
    /// Records a response that failed after bytes were already produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="WriteAsync"/> because there is nothing to write:
    /// the caller aborts the connection. This exists so the failure appears in
    /// the log rather than only as a client-side truncation.
    /// </para>
    /// <para>
    /// <b>And it marks the request, which is the other half of
    /// [D-132](../../docs/architecture-debt.md).</b> A line in the failure log said what
    /// went wrong and the access log next to it still said <c>- 200</c>, because
    /// <c>Response.StatusCode</c> was fixed when the headers went out. Two logs
    /// disagreeing about the same request is worse than one of them being silent.
    /// </para>
    /// </remarks>
    /// <param name="context">The request, so the access log can record what happened.</param>
    /// <param name="logger">Where to write the failure.</param>
    /// <param name="exception">What went wrong.</param>
    public static void LogTruncated(
        HttpContext context, ILogger logger, Exception exception)
    {
        ResponseOutcome.Truncated(context, exception);
        Log.FailedMidResponse(logger, exception);
    }

    /// <summary>
    /// How long a caller refused by the connection budget is asked to wait.
    /// </summary>
    /// <remarks>
    /// <b>The budget's own wait window, and that is the reasoning rather than a round
    /// number.</b> A caller is refused only after the server has already waited
    /// `ConnectionBudget.Default` — five seconds — for a slot, so a slot has been busy for
    /// at least that long and asking for less would send the client straight back into a
    /// queue that has not moved. It is a hint and not a contract: RFC 9110 §10.2.3 says a
    /// client may retry sooner, and this server will refuse it again if it does.
    /// </remarks>
    private const int RetrySeconds = 5;

    /// <summary>Maps an exception to a status and a message for the caller.</summary>
    public static async Task WriteAsync(HttpContext context, Exception exception, ILogger logger)
    {
        // <b>A request that ran out of time and a client that hung up throw the same
        // exception, and they deserve opposite answers.</b> 499 exists so the access log
        // can say *they left*; saying that about a request the server itself stopped sends
        // whoever reads it to look at the network. Only the deadline knows which happened,
        // so it is asked before the general mapping — and it answers false when the client's
        // own token was cancelled too.
        (int status, string message) = exception is OperationCanceledException
            && RequestDeadline.Expired(context)
                ? (StatusCodes.Status504GatewayTimeout, DeadlineMessage(context))
                : Classify(exception);

        if (context.Response.HasStarted)
        {
            // Marked before the abort: aborting cancels RequestAborted, and after that
            // there is no way left to tell whose fault it was.
            ResponseOutcome.Truncated(context, exception);
            Log.FailedMidResponse(logger, exception);
            context.Abort();
            return;
        }

        Log.RequestFailed(logger, status, context.Request.Path, exception);

        context.Response.Clear();
        context.Response.StatusCode = status;

        // <b>The retry signal ADR-007 §4.9 asks admission control to send, and it was
        // missing for a day.</b> `ConnectionBudgetFullException`'s own remark said the
        // refusal comes *with a `Retry-After`*; nothing in this path ever set one, and the
        // performance gate read the live headers off a real 503 and found it absent. A
        // budget refusal is the one 503 here that is genuinely transient — a slot frees
        // when somebody's query finishes — so a client that backs off gets served, and a
        // client that hammers makes it worse for everybody.
        //
        // <b>Deliberately not set on the other 503s.</b> An unreachable database and a
        // geometry request that ran out of memory both produce the same outcome on retry,
        // and `Retry-After` on those would be a promise this server cannot keep.
        if (exception is ConnectionBudgetFullException)
        {
            context.Response.Headers.RetryAfter =
                RetrySeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (await TryProtocolEnvelopeAsync(context, status, message).ConfigureAwait(false))
        {
            return;
        }

        await Results.Json(new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Answers in the shape the face being addressed refuses in, when there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One handler, seven protocols, and until 2026-08-20 one envelope.</b> Every
    /// unhandled failure — including every refusal during a database outage — was written
    /// as ArcGIS REST JSON, on all of them. A WMS 1.3.0 client expects
    /// <c>ServiceExceptionReport</c>, a WFS 2.0 client expects <c>ows:ExceptionReport</c>
    /// and an OGC API Features client expects <c>application/problem+json</c>; all three
    /// received <c>{"error":{"code":…}}</c> and could not parse it.
    /// </para>
    /// <para>
    /// <b>The moment it mattered most is the moment it was worst.</b> The handled paths on
    /// these faces get their envelopes right, and get them right well — this is only the
    /// fallback, which is what a client meets during an outage, which is exactly when its
    /// error handling is being exercised hardest. Found by the second failure gate, which
    /// stopped the database and read what each face said.
    /// </para>
    /// <para>
    /// <b>The message is unchanged; only the wrapper is chosen.</b> The sentence
    /// <see cref="Classify"/> produced is already written for an operator, and rewriting
    /// it per protocol would be seven places for it to drift.
    /// </para>
    /// </remarks>
    private static async Task<bool> TryProtocolEnvelopeAsync(
        HttpContext context, int status, string message)
    {
        PathString path = context.Request.Path;

        if (path.StartsWithSegments("/wms"))
        {
            // A WMS exception is a 200 carrying a refusal document — WmsEndpoints says
            // why, and this must not disagree with it for the same server.
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = WmsFault.MediaType(WmsVersion.V130);

            await context.Response
                .WriteAsync(new WmsFault(null, message).ToXml(WmsVersion.V130))
                .ConfigureAwait(false);

            return true;
        }

        if (path.StartsWithSegments("/wfs"))
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/xml; charset=utf-8";

            await new WfsFault(WfsFaultCode.OperationProcessingFailed, null, message)
                .WriteAsync(context.Response.Body, context.RequestAborted)
                .ConfigureAwait(false);

            return true;
        }

        if (path.StartsWithSegments("/ogc"))
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = OgcNames.Problem;

            await context.Response
                .WriteAsync(new OgcProblem(status, ReasonPhrases.GetReasonPhrase(status), message)
                    .ToJson())
                .ConfigureAwait(false);

            return true;
        }

        return false;
    }

    /// <summary>The sentence a request that ran out of time gets.</summary>
    /// <remarks>
    /// <b>It names the number, because the number is configurable and therefore not
    /// guessable.</b> A deployment may set `Graticula:RequestDeadlineSeconds`, and a service
    /// may lower it further, so *the request took too long* leaves a caller unable to tell
    /// whether they were 2 seconds over or 200. Naming what applied to this request is what
    /// makes the refusal something they can act on.
    /// </remarks>
    private static string DeadlineMessage(HttpContext context)
    {
        double seconds = RequestDeadline.Of(context)?.Allowed.TotalSeconds ?? 0;

        // <b>Singular when it is one.</b> "1 seconds" in the sentence an operator pastes into a
        // ticket is the kind of detail that makes a real message read as a generated one.
        string howLong = $"{seconds:0.#} second" + (Math.Abs(seconds - 1) < 0.05 ? "" : "s");

        return $"This request reached the time a client may occupy a service — "
            + $"{howLong} — and was stopped. The server did not fail; it stopped "
            + "waiting. Narrow the extent, lower resultRecordCount, or ask the operator: the "
            + "bound is the smaller of the server's `Graticula:RequestDeadlineSeconds` and this "
            + "service's own setting.";
    }

    /// <summary>Which status and sentence an exception earns.</summary>
    /// <remarks>
    /// Internal so it can be tested without a server. The mapping is the part
    /// with judgement in it — whether a dropped table is the caller's problem or
    /// ours — and it is the part a running-server test would exercise least.
    /// </remarks>
    internal static (int Status, string Message) Classify(Exception exception) => exception switch
    {
        // <b>The server's own bound, and it must not read as the database's.</b> ADR-007 §4.8's
        // connection budget refuses when this worker already has its full complement of requests in
        // flight — the database is fine, and telling the caller it is *unreachable* would send an
        // operator to look at something that is working. 503 with the reason, and the message names
        // the setting that would raise it.
        // <b>A caller's bad SRID is a caller's problem, and it was being reported as an
        // outage.</b> PostGIS raises XX000 — its catch-all internal class — for
        // `Invalid reserved SRID`, and XX000 is not one of the codes named below, so it
        // fell through to the general Npgsql branch and every face answered *a database
        // this server depends on is unreachable* while the database was up and answering
        // everything else. An operator reading that goes to check the network. Found by
        // the second failure gate, on four surfaces at once.
        //
        // <b>Matched on the message, which is unusual here and is the honest option.</b>
        // XX000 covers unrelated genuine faults, so the code alone cannot decide; the
        // text PROJ raises is stable and specific. Anything else in XX000 keeps the
        // conservative answer.
        PostgresException { SqlState: "XX000" } srid
            when srid.MessageText.Contains("SRID", StringComparison.OrdinalIgnoreCase) => (
                StatusCodes.Status400BadRequest,
                "A coordinate reference system in this request is not one the projection "
                + "database knows: " + srid.MessageText + ". The server is healthy; check "
                + "the CRS, SRS, srsName, outSR or bboxSR you sent."),

        // <b>The breaker's refusal, which is the same answer arriving 4,000 times faster.</b>
        // D-131: a database that failed moments ago is asked again on the next request and
        // blackholes for another four seconds, holding a connection throughout. This says the
        // same thing as the NpgsqlException case below and says it immediately.
        SourceUnreachableException breaker => (
            StatusCodes.Status503ServiceUnavailable,
            breaker.Message
            + " Check /healthz/ready and /admin/health: the first says whether the platform "
            + "store is up, and the second distinguishes it from a layer's own data source. "
            + "Retry in a few seconds; the server will try the connection again by itself."),

        ConnectionBudgetFullException full => (
            StatusCodes.Status503ServiceUnavailable, full.Message),

        // A cancelled statement is nearly always the timeout below rather than a
        // disconnected client, and 504 is the difference between "your query was
        // too expensive" and "the server is broken". The first is actionable.
        PostgresException { SqlState: "57014" } => (
            StatusCodes.Status504GatewayTimeout,
            "The query exceeded the statement timeout on the underlying database. Narrow the "
            + "extent, lower resultRecordCount, or index the geometry column. The server did not "
            + "fail; it stopped waiting."),

        // <b>A client-side statement timeout, which arrives wearing the connectivity costume.</b>
        // Npgsql's command timeout does not produce 57014 above: it gives up on the socket read
        // and throws `NpgsqlException("Exception while reading from stream")` with an inner
        // `TimeoutException`, which fell through to the general Npgsql case and answered *a
        // database this server depends on is unreachable*. So an operator who set a one-second
        // statement bound on their own service had their clients told the database was down —
        // measured, 19 of 30 concurrent queries, and the same misdiagnosis 42883 and 42703 were
        // already corrected for. The bound was honoured; only the sentence was wrong.
        NpgsqlException { InnerException: TimeoutException } => (
            StatusCodes.Status504GatewayTimeout,
            "The query exceeded the statement timeout configured for this service. Narrow the "
            + "extent, lower resultRecordCount, or index the geometry column. The database is "
            + "up and reachable; this server stopped waiting for one statement."),

        // <b>A name already taken is the caller's problem, and it was reported as an
        // outage.</b> 23505 is a unique-constraint violation — publishing a service
        // whose name exists, or registering one file twice — and it fell through to the
        // general Npgsql branch, so a publisher who picked a taken name was told the
        // database was unreachable. Found on 2026-08-21 by registering the same
        // coverage twice. 409 is the status for it, and the fourth instance of this
        // file mistaking a caller's mistake for a connectivity failure.
        PostgresException { SqlState: "23505" } => (
            StatusCodes.Status409Conflict,
            "Something with that name or location is already registered here. The database is "
            + "healthy; it refused a duplicate. Pick another name, or look at what is already "
            + "published at that address."),

        PostgresException { SqlState: "42P01" } => (
            StatusCodes.Status503ServiceUnavailable,
            "The table behind this layer no longer exists. The registration and the database have "
            + "diverged — this is a catalogue problem, not a transient one, and retrying will not "
            + "help."),

        // <b>42883 and 42703 are a schema problem wearing a connectivity
        // costume.</b> Both fell through to the NpgsqlException case below and
        // were answered "a database this server depends on is unreachable" —
        // which sends an operator to check the network and the health endpoints
        // while the database is up, connected, and simply missing the thing we
        // asked for. Found when a vector tile request hit a datastore whose
        // search_path excluded the schema PostGIS is installed in: every
        // spatial function was undefined, and the server blamed the network.
        // <b>42883 covers two situations and only one of them is the operator's.</b>
        // *function st_intersects does not exist* is PostGIS missing, which is the case
        // below and is correctly a 500. *operator does not exist: timestamp with time
        // zone = text* is a caller comparing a column to a literal this server did not
        // convert — the request cannot be served, the database is entirely healthy, and
        // 500 is both the wrong status and the wrong story. Found on 2026-08-21 by
        // re-running the WFS conformance suite: four assertions answered 500 with a
        // sentence telling an operator to check whether PostGIS was installed.
        //
        // <b>The limitation itself is [Q-124](../../docs/open-questions.md) and is not
        // fixed here.</b> Neither front end converts a date literal, deliberately, so
        // that the two give the same answer to the same question. What this changes is
        // that the refusal now says so instead of blaming the database.
        PostgresException { SqlState: "42883" } e
            when e.MessageText.StartsWith("operator does not exist", StringComparison.Ordinal) => (
                StatusCodes.Status400BadRequest,
                "This filter compares a column with a value of a type this server does not convert "
                + $"for it — the database reports: {e.MessageText}. The usual case is a date or "
                + "timestamp column: every filter language here sends its literals as text, and "
                + "neither the ArcGIS `where` grammar nor Filter Encoding converts them, so the "
                + "comparison reaches the database as text. The database is healthy; the request "
                + "cannot be answered as written."),

        PostgresException { SqlState: "42883" } => (
            StatusCodes.Status500InternalServerError,
            "The database is reachable but does not have a function this server needs. The usual "
            + "cause is PostGIS not being installed in that database, or being installed in a "
            + "schema outside the connection's search_path. Check /admin/datasources/{id}/capability, "
            + "which reports the PostGIS version the server can actually see."),

        PostgresException { SqlState: "42703" } => (
            StatusCodes.Status500InternalServerError,
            "The database is reachable but a column this layer was registered with does not exist. "
            + "The registration and the table have diverged. Retrying will not help; the layer "
            + "needs republishing against the columns the table actually has."),

        PostgresException { SqlState: "42501" } or PostgresException { SqlState: "28P01" } => (
            StatusCodes.Status503ServiceUnavailable,
            "The server could not authenticate against the layer's database, or lacks permission "
            + "to read the table. The stored credential needs attention."),

        // <b>A body too large is the caller's problem, not a fault.</b>
        // Kestrel throws this when a request exceeds the configured limit, and
        // uncaught it became "the server failed to handle this request" — for a
        // request the server refused correctly and on purpose.
        Microsoft.AspNetCore.Http.BadHttpRequestException big
            when big.StatusCode == StatusCodes.Status413PayloadTooLarge => (
            StatusCodes.Status413PayloadTooLarge,
            "The request body is larger than this endpoint accepts. Each surface that takes a "
            + "body states its own limit in the refusal it would have given you; this one came "
            + "from the web server first."),

        SecretProtectionException => (
            StatusCodes.Status503ServiceUnavailable,
            "The stored credential for this layer could not be decrypted. The server is running "
            + "with a different key than the one that sealed it. See the server log."),

        // <b>Two different databases, and telling them apart is the whole
        // value of the message.</b> "The layer's database is unreachable" sends
        // an administrator to the customer's PostGIS; if the platform store is
        // what is down, they are looking in the wrong place while their own
        // catalogue is offline. Observed saying exactly the wrong thing during
        // the ADR-017 condition 1 test.
        //
        // The distinction cannot be made from the exception, which knows only
        // that a socket failed — so it is made by the caller, which knows which
        // pool it was using.
        NpgsqlException => (
            StatusCodes.Status503ServiceUnavailable,
            "A database this server depends on is unreachable. Check /healthz/ready and "
            + "/admin/health: the first says whether the platform store is up, and the second "
            + "distinguishes the platform store from a layer's own data source."),

        // 499 is nginx's, not an IANA code, and nothing will read it — the
        // client has gone. It exists so the access log distinguishes "they left"
        // from "we broke", which are the same 500 otherwise.
        OperationCanceledException => (499, "The request was cancelled by the caller."),

        _ => (
            StatusCodes.Status500InternalServerError,
            "The server failed to handle this request. The reason is in the server log; it is not "
            + "repeated here because this endpoint is reachable without authentication."),
    };
}
