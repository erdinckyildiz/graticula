using System;
using System.Globalization;
using System.Threading.Tasks;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Wfs;
using Graticula.Api.Wms;
using Graticula.Platform.Identity;
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

    /// <summary>
    /// How long a refused caller is told to wait, or null when this server cannot say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The retry signal ADR-007 §4.9 asks admission control to send, and it was missing
    /// for a day.</b> `ConnectionBudgetFullException`'s own remark said the refusal comes
    /// *with a `Retry-After`*; nothing in this path ever set one, and the performance gate
    /// read the live headers off a real 503 and found it absent — which is
    /// [D-130](../../docs/architecture-debt.md)'s shape exactly, a sentence describing
    /// behaviour the code did not have.
    /// </para>
    /// <para>
    /// <b>A separate function so the rule can be asserted per refusal shape</b>, which is
    /// the third of the three checks D-130 asks for. Testing it through the pipeline means
    /// building a request, a response body and a JSON serialiser to look at one header; the
    /// decision is the part with judgement in it, and this is it.
    /// </para>
    /// <para>
    /// <b>Only where the server knows the answer.</b> A budget refusal frees a slot when
    /// somebody's query finishes, and the breaker's refusal ends when its window closes —
    /// both are times this server sets. An unreachable database and a geometry request
    /// that ran out of memory produce the same outcome on retry, and a `Retry-After` on
    /// those would be a promise this server cannot keep.
    /// </para>
    /// </remarks>
    /// <param name="exception">Why the request was refused.</param>
    /// <returns>Seconds to wait, or null.</returns>
    internal static int? RetryAfterFor(Exception exception) => exception switch
    {
        ConnectionBudgetFullException => RetrySeconds,

        // <b>The one 503 whose recovery time this server actually knows.</b> The breaker
        // will try the source again when its window closes, so telling a client to come
        // back then is a fact rather than an estimate — and a client that retries sooner
        // is refused again in microseconds, which is the whole point of D-131's repair.
        SourceUnreachableException => (int)Math.Ceiling(SourceBreaker.Cooling.TotalSeconds),

        _ => null,
    };

    /// <summary>Maps an exception to a status and a message for the caller.</summary>
    public static async Task WriteAsync(HttpContext context, Exception exception, ILogger logger)
    {
        // <b>A request that ran out of time and a client that hung up throw the same
        // exception, and they deserve opposite answers.</b> 499 exists so the access log
        // can say *they left*; saying that about a request the server itself stopped sends
        // whoever reads it to look at the network. Only the deadline knows which happened,
        // so it is asked before the general mapping — and it answers false when the client's
        // own token was cancelled too.
        // <b>[D-03](../../docs/architecture-debt.md): detail is authorization-scoped, and until
        // 2026-08-24 this handler had no authorization dimension at all.</b>
        // [security.md](../../docs/security.md) §5 states the rule — an authenticated
        // administrator sees the provider and the reason, anybody else sees a generic refusal —
        // and it had been stated and not implemented since 2026-08-12, while the sentences below
        // named PostGIS, echoed the store's own message text and pointed at `/admin/health`.
        //
        // <b>The privilege is `admin:manageServer` because that is who the detailed sentences
        // are addressed to.</b> They say to check `/healthz/ready`, `/admin/health` and
        // `/admin/datasources/{id}/capability`, and those want the same privilege — so a caller
        // who can read the advice can act on it, and one who cannot is not told to go somewhere
        // they will be refused.
        //
        // <b>No principal means anonymous, not administrator.</b> A failure early enough that
        // the authentication middleware has not run is exactly when the safe answer matters, and
        // a null-tolerant read that defaults open would have made this handler its own hole.
        bool detailed = context.Features.Get<RequestPrincipal>() is { } who
            && who.Authorization.Allows(Privilege.AdminManageServer);

        (int status, string message) = exception is OperationCanceledException
            && RequestDeadline.Expired(context)
                ? (StatusCodes.Status504GatewayTimeout, DeadlineMessage(context))
                : Classify(exception, detailed);

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

        if (RetryAfterFor(exception) is { } after)
        {
            context.Response.Headers.RetryAfter =
                after.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>A refusal in both the forms it may be read in.</summary>
    /// <param name="Status">The status code, which is the same for everybody.</param>
    /// <param name="Operator">
    /// The sentence written for whoever can act on it, which may name the provider, the
    /// setting or the endpoint to look at.
    /// </param>
    /// <param name="Anonymous">
    /// What a caller without <c>admin:manageServer</c> is told, or null when
    /// <paramref name="Operator"/> discloses nothing and everybody may read it.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>[D-03](../../docs/architecture-debt.md), from review G7, and the rule is
    /// [security.md](../../docs/security.md) §5:</b> detail is authorization-scoped. An
    /// authenticated administrator sees the provider and the reason; anybody else sees the
    /// capability in abstract terms and a generic refusal. A capability report and a
    /// detailed refusal are pleasant to receive and they tell any client that can reach a
    /// layer which database engine sits behind it, and by implication its version and the
    /// organisation's internal topology.
    /// </para>
    /// <para>
    /// <b>The rule was already understood in exactly one arm of this switch.</b> The
    /// fallback below says its reason is in the log *"because this endpoint is reachable
    /// without authentication"* — and every arm above it named PostGIS, echoed the
    /// database's own message text, or pointed at <c>/admin/health</c>. One place knowing
    /// the rule and the rest not is [D-46](../../docs/architecture-debt.md), which is why
    /// the pair lives on one record rather than in a second switch that would have to be
    /// kept in step with this one.
    /// </para>
    /// <para>
    /// <b>Null means safe, and a test rather than a habit decides that it is.</b>
    /// <c>Every_refusal_an_anonymous_caller_can_reach_is_free_of_the_provider</c> drives
    /// every arm and reads what an unprivileged caller would receive, so an arm added with
    /// no anonymous form is caught by what its operator sentence says rather than by
    /// somebody noticing the null.
    /// </para>
    /// </remarks>
    internal readonly record struct Refusal(int Status, string Operator, string? Anonymous);

    /// <summary>Which status and sentence an exception earns.</summary>
    /// <param name="exception">What went wrong.</param>
    /// <param name="detailed">
    /// Whether the caller may read the detailed form. False is the safe default at every
    /// call site that does not know who is asking.
    /// </param>
    /// <returns>The status and the sentence to write.</returns>
    /// <remarks>
    /// Internal so it can be tested without a server. The mapping is the part
    /// with judgement in it — whether a dropped table is the caller's problem or
    /// ours — and it is the part a running-server test would exercise least.
    /// </remarks>
    internal static (int Status, string Message) Classify(Exception exception, bool detailed = true)
    {
        Refusal refusal = Explain(exception);

        return (refusal.Status, detailed ? refusal.Operator : refusal.Anonymous ?? refusal.Operator);
    }

    /// <summary>The refusal an exception earns, in both forms.</summary>
    /// <param name="exception">What went wrong.</param>
    /// <returns>The status, the operator's sentence, and the public one where they differ.</returns>
    /// <summary>
    /// Whether PROJ refused a coordinate because it lies outside its reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One predicate, because two callers have to agree about it —
    /// [D-163](../../docs/architecture-debt.md).</b> <see cref="Explain"/> turns it into a
    /// 400 for every surface that owes the caller an error; the WMS map path treats it as
    /// *nothing to draw here*, because WMS says the part of a bounding box outside its
    /// reference is blank rather than an exception. Two copies of the test would drift, and
    /// the one that drifted would turn a blank margin back into a broken layer.
    /// </para>
    /// <para>
    /// <b>Matched on the message because PostGIS gives no code of its own.</b> <c>XX000</c>
    /// is <c>internal_error</c>, which PostGIS uses for everything it raises itself. Two
    /// texts are covered and both were seen on the same request: *exceeded limits* when the
    /// latitude does not exist, and *tolerance condition error* when it exists but the target
    /// projection cannot represent it — Web Mercator at the pole. The match fails safe: any
    /// other <c>XX000</c> keeps the old answer.
    /// </para>
    /// </remarks>
    /// <returns>Whether it is a coordinate outside its reference.</returns>
    internal static bool IsOutsideItsReference(Exception exception) =>
        exception is PostgresException { SqlState: "XX000" } postgis
        && postgis.MessageText.Contains("transform", StringComparison.Ordinal)
        && (postgis.MessageText.Contains("exceeded limits", StringComparison.Ordinal)
            || postgis.MessageText.Contains("tolerance condition", StringComparison.Ordinal));

    internal static Refusal Explain(Exception exception) => exception switch
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
        //
        // <b>The public form keeps all of the advice and none of the evidence.</b> Which
        // reference system was rejected is the caller's own parameter, so naming the
        // parameters to check costs nothing; the database's message text is the projection
        // library talking and is not theirs to read.
        PostgresException { SqlState: "XX000" } srid
            when srid.MessageText.Contains("SRID", StringComparison.OrdinalIgnoreCase) => new(
                StatusCodes.Status400BadRequest,
                "A coordinate reference system in this request is not one the projection "
                + "database knows: " + srid.MessageText + ". The server is healthy; check "
                + "the CRS, SRS, srsName, outSR or bboxSR you sent.",
                "A coordinate reference system in this request is not one this server can use. "
                + "The server is healthy; check the CRS, SRS, srsName, outSR or bboxSR you sent."),

        // <b>The breaker's refusal, which is the same answer arriving 4,000 times faster.</b>
        // D-131: a database that failed moments ago is asked again on the next request and
        // blackholes for another four seconds, holding a connection throughout. This says the
        // same thing as the NpgsqlException case below and says it immediately.
        SourceUnreachableException breaker => new(
            StatusCodes.Status503ServiceUnavailable,
            breaker.Message
            + " Check /healthz/ready and /admin/health: the first says whether the platform "
            + "store is up, and the second distinguishes it from a layer's own data source. "
            + "Retry in a few seconds; the server will try the connection again by itself.",
            "This service is temporarily unavailable. Retry in a few seconds; the server will "
            + "try again by itself."),

        // The message names a setting, which is the operator's to change and nobody else's
        // to learn — but *the server is busy, wait* is the whole of what a client can do
        // with it, so the public form loses nothing the caller could have used.
        ConnectionBudgetFullException full => new(
            StatusCodes.Status503ServiceUnavailable,
            full.Message,
            "This service is busy. Retry in a few seconds."),

        // A cancelled statement is nearly always the timeout below rather than a
        // disconnected client, and 504 is the difference between "your query was
        // too expensive" and "the server is broken". The first is actionable.
        PostgresException { SqlState: "57014" } => new(
            StatusCodes.Status504GatewayTimeout,
            "The query exceeded the statement timeout on the underlying database. Narrow the "
            + "extent, lower resultRecordCount, or index the geometry column. The server did not "
            + "fail; it stopped waiting.",
            TookTooLong),

        // <b>A client-side statement timeout, which arrives wearing the connectivity costume.</b>
        // Npgsql's command timeout does not produce 57014 above: it gives up on the socket read
        // and throws `NpgsqlException("Exception while reading from stream")` with an inner
        // `TimeoutException`, which fell through to the general Npgsql case and answered *a
        // database this server depends on is unreachable*. So an operator who set a one-second
        // statement bound on their own service had their clients told the database was down —
        // measured, 19 of 30 concurrent queries, and the same misdiagnosis 42883 and 42703 were
        // already corrected for. The bound was honoured; only the sentence was wrong.
        NpgsqlException { InnerException: TimeoutException } => new(
            StatusCodes.Status504GatewayTimeout,
            "The query exceeded the statement timeout configured for this service. Narrow the "
            + "extent, lower resultRecordCount, or index the geometry column. The database is "
            + "up and reachable; this server stopped waiting for one statement.",
            TookTooLong),

        // <b>A name already taken is the caller's problem, and it was reported as an
        // outage.</b> 23505 is a unique-constraint violation — publishing a service
        // whose name exists, or registering one file twice — and it fell through to the
        // general Npgsql branch, so a publisher who picked a taken name was told the
        // database was unreachable. Found on 2026-08-21 by registering the same
        // coverage twice. 409 is the status for it, and the fourth instance of this
        // file mistaking a caller's mistake for a connectivity failure.
        //
        // The name is the caller's own, so the whole sentence is theirs to read except
        // the aside about the database being healthy, which is a fact about the server.
        PostgresException { SqlState: "23505" } => new(
            StatusCodes.Status409Conflict,
            "Something with that name or location is already registered here. The database is "
            + "healthy; it refused a duplicate. Pick another name, or look at what is already "
            + "published at that address.",
            "Something with that name or location is already registered here. Pick another name, "
            + "or look at what is already published at that address."),

        PostgresException { SqlState: "42P01" } => new(
            StatusCodes.Status503ServiceUnavailable,
            "The table behind this layer no longer exists. The registration and the database have "
            + "diverged — this is a catalogue problem, not a transient one, and retrying will not "
            + "help.",
            NeedsAnAdministrator),

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
        //
        // <b>This is the one arm where the public form was hardest to write and matters
        // most.</b> The caller's filter is genuinely at fault and they cannot fix it
        // without knowing why, so the explanation stays whole — in the vocabulary of the
        // request rather than of the store, and without the store's own message text,
        // which names types and operators the caller never wrote.
        PostgresException { SqlState: "42883" } e
            when e.MessageText.StartsWith("operator does not exist", StringComparison.Ordinal) => new(
                StatusCodes.Status400BadRequest,
                "This filter compares a column with a value of a type this server does not convert "
                + $"for it — the database reports: {e.MessageText}. The usual case is a date or "
                + "timestamp column: every filter language here sends its literals as text, and "
                + "neither the ArcGIS `where` grammar nor Filter Encoding converts them, so the "
                + "comparison reaches the database as text. The database is healthy; the request "
                + "cannot be answered as written.",
                "This filter compares a field with a value of a type this server does not convert "
                + "for it. The usual case is a date or timestamp field: every filter language here "
                + "sends its literals as text, and neither the ArcGIS `where` grammar nor Filter "
                + "Encoding converts them. The request cannot be answered as written."),

        // <b>The third thing 42883 covers, and it is the caller's too.</b> *function avg(text)
        // does not exist* is not PostGIS missing — it is a numeric statistic asked for over a
        // text column, which is exactly what classifying a text field by ranges does. It fell
        // through to the branch below and told the operator to go and check whether PostGIS was
        // installed: a frightening, wrong diagnosis of an ordinary mistake, on a database that
        // is entirely healthy.
        //
        // <b>Found by a design review on 2026-09-04, and it is the same shape this file already
        // repaired once</b> for `operator does not exist` above. Asking *what else carries this?*
        // when that one was fixed would have found this one three weeks earlier; the two arms now
        // sit together so the next reader sees both.
        //
        // <b>Told apart by the function's name.</b> Every function a missing PostGIS takes with
        // it is spatial and named for it — `st_`, `_st_`, `postgis_`, `geometry_`, `geography_`.
        // Anything else undefined at this point is a type the caller asked the database to
        // compute over.
        PostgresException { SqlState: "42883" } e when NotSpatial(e.MessageText) is { } function
            => new(
                StatusCodes.Status400BadRequest,
                $"The database has no `{function}` for the types this request asks it to compute "
                + $"over — it reports: {e.MessageText}. The usual case is a numeric statistic over "
                + "a text column: `avg`, `stddev` and `percentile_cont` all need a number. The "
                + "database is healthy; the request cannot be answered as written.",
                $"This asks for `{function}` over a field whose type it cannot be computed over. "
                + "A numeric statistic — an average, a standard deviation, a percentile — needs a "
                + "numeric field."),

        PostgresException { SqlState: "42883" } => new(
            StatusCodes.Status500InternalServerError,
            "The database is reachable but does not have a function this server needs. The usual "
            + "cause is PostGIS not being installed in that database, or being installed in a "
            + "schema outside the connection's search_path. Check /admin/datasources/{id}/capability, "
            + "which reports the PostGIS version the server can actually see.",
            NeedsAnAdministrator),

        // <b>*Retrying will not help* was measured false — [D-178](../../docs/architecture-debt.md),
        // 2026-08-26.</b> A layer's shape is remembered for `ServiceContexts.Lifetime`, which is
        // **30 seconds**, and this is what a request sees while a warm memory names a column the
        // table no longer has. Measured against a registered table: three queries inside the
        // window answer 500, and after it the layer re-reads and answers 200 with the columns
        // that are there. So the sentence sent an operator to republish a layer that repairs
        // itself in half a minute — and republishing is genuinely needed only when the
        // registration itself is wrong, which the next request tells them.
        PostgresException { SqlState: "42703" } => new(
            StatusCodes.Status500InternalServerError,
            "The database is reachable but a column this layer was registered with does not exist. "
            + "The registration and the table have diverged. This server re-reads a layer's "
            + "columns every 30 seconds, so try again shortly: if the table simply lost a column, "
            + "the layer starts serving the columns it now has. If it still refuses after that, "
            + "the registration names a column the table does not have — its identity or geometry "
            + "column — and the layer needs republishing.",
            NeedsAnAdministrator),

        // <b>A coordinate outside its reference's domain is the caller's mistake, and it was
        // answered as an outage — found 2026-08-26 by the WMS 1.3 CITE suite.</b> A `GetMap`
        // with `CRS:84` and `BBOX=-10,90,10,110` asks for latitudes up to 110°, which do not
        // exist. PROJ refuses it, PostGIS raises `XX000: transform: latitude or longitude
        // exceeded limits`, and `PostgresException` derives from `NpgsqlException` — so it fell
        // to the general branch and the server said *this service is temporarily unavailable,
        // retry in a few seconds*. **Retrying will never help**, and the sentence sends whoever
        // reads it to check a database that is working perfectly. That is the same mistake
        // [D-150](../../docs/architecture-debt.md) records one arm above, arriving on a
        // different road.
        //
        // <b>Matched on the message because PostGIS gives no code of its own.</b> `XX000` is
        // `internal_error`, which PostGIS uses for everything it raises itself, so the state
        // alone cannot tell a bad coordinate from a bad anything. Text matching is brittle and
        // this one fails safe: if PostGIS rewords it, the arm stops matching and the answer
        // goes back to what it was, which is wrong but no worse than today.
        //
        // <b>400 rather than 500, and it names the parameter.</b> The request is the thing that
        // is wrong; nothing about the server or its data needs attention. On the WMS surface
        // this becomes a `ServiceException`, which is what a conforming client reads.
        PostgresException { SqlState: "XX000" } postgis
            when IsOutsideItsReference(postgis) => new(
            StatusCodes.Status400BadRequest,
            "A coordinate in this request is outside the range its coordinate reference system "
            + "allows — a latitude beyond ±90° or a longitude beyond ±180° in a geographic "
            + "system, or a value outside the projection's domain. Check the bounding box and "
            + "the reference it is stated in; retrying will not help.",
            null),

        // <b>A lock wait is not an outage, and it was answered as one — D-150.</b> PostgreSQL
        // raises 55P03 when `lock_timeout` cuts a statement that is *waiting* rather than
        // running, and nothing here had an arm for it: `PostgresException` derives from
        // `NpgsqlException`, so it fell to the general branch and was answered *a database this
        // server depends on is unreachable*. Measured 2026-08-24: 503, with the sentence naming
        // `/healthz/ready`. The database is up, connected and answering; a DBA is holding
        // `ACCESS EXCLUSIVE` on one table, and an operator reading that goes to check the
        // network. **The fifth time this file has mistaken a specific fault for a connectivity
        // failure** — after `XX000`, `23505`, `42883` and `42703` — and the first one caught
        // before somebody met it, because nothing sets `lock_timeout` yet.
        //
        // <b>503 rather than 504.</b> The statement never ran, so *your query was too
        // expensive* is the wrong story; something else holds the table and will let go, which
        // is temporary in the way 503 means and a timeout is not.
        //
        // <b>And no `Retry-After`, deliberately.</b> `RetryAfterFor` answers only where this
        // server knows the recovery time — the breaker knows when its window closes. Nobody
        // here knows how long a DBA will hold a lock, and a number invented for the header is
        // worse than none: a client that believes it retries in lockstep with every other
        // client that believed it.
        PostgresException { SqlState: "55P03" } => new(
            StatusCodes.Status503ServiceUnavailable,
            "This layer's table is locked by something else — a schema change or an "
            + "administrative operation — and the wait exceeded this service's `lock_timeout`. "
            + "The database is healthy and the query never ran. Retry in a few seconds; if it "
            + "persists, ask whoever is running DDL against that table.",
            "This layer is busy while something else finishes with it. The query was not run. "
            + "Retry in a few seconds."),

        PostgresException { SqlState: "42501" } or PostgresException { SqlState: "28P01" } => new(
            StatusCodes.Status503ServiceUnavailable,
            "The server could not authenticate against the layer's database, or lacks permission "
            + "to read the table. The stored credential needs attention.",
            NeedsAnAdministrator),

        // <b>A body too large is the caller's problem, not a fault.</b>
        // Kestrel throws this when a request exceeds the configured limit, and
        // uncaught it became "the server failed to handle this request" — for a
        // request the server refused correctly and on purpose.
        //
        // No public form: a limit the caller just exceeded is the caller's business, and
        // the sentence names nothing behind the server.
        Microsoft.AspNetCore.Http.BadHttpRequestException big
            when big.StatusCode == StatusCodes.Status413PayloadTooLarge => new(
            StatusCodes.Status413PayloadTooLarge,
            "The request body is larger than this endpoint accepts. Each surface that takes a "
            + "body states its own limit in the refusal it would have given you; this one came "
            + "from the web server first.",
            null),

        SecretProtectionException => new(
            StatusCodes.Status503ServiceUnavailable,
            "The stored credential for this layer could not be decrypted. The server is running "
            + "with a different key than the one that sealed it. See the server log.",
            NeedsAnAdministrator),

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
        //
        // And *two* databases is precisely the topology D-03 is about, so the public form
        // has one: something this service needs is not answering, and it may come back.
        NpgsqlException => new(
            StatusCodes.Status503ServiceUnavailable,
            "A database this server depends on is unreachable. Check /healthz/ready and "
            + "/admin/health: the first says whether the platform store is up, and the second "
            + "distinguishes the platform store from a layer's own data source.",
            "This service is temporarily unavailable. Retry in a few seconds."),

        // 499 is nginx's, not an IANA code, and nothing will read it — the
        // client has gone. It exists so the access log distinguishes "they left"
        // from "we broke", which are the same 500 otherwise.
        OperationCanceledException => new(499, "The request was cancelled by the caller.", null),

        _ => new(
            StatusCodes.Status500InternalServerError,
            "The server failed to handle this request. The reason is in the server log; it is not "
            + "repeated here because this endpoint is reachable without authentication.",
            null),
    };

    /// <summary>What a caller who may not read the operator's sentence is told instead.</summary>
    /// <remarks>
    /// One sentence for the whole class of *the store behind this layer is not in a state
    /// this request can be answered from*, because the difference between a dropped table,
    /// a missing extension, a renamed column, a refused credential and an unopenable secret
    /// is the difference D-03 says an anonymous caller may not learn. It is deliberately not
    /// *try again*: none of them clear by themselves, and telling a client to retry a
    /// permanent fault is worse advice than none.
    /// </remarks>
    /// <summary>
    /// The name of an undefined function, when it is not one a missing PostGIS would explain.
    /// </summary>
    /// <remarks>
    /// <b>The prefix is the whole test.</b> PostGIS installs its functions under `st_`, `_st_`,
    /// `postgis_`, `geometry_` and `geography_`; nothing else this server calls is spatial. So an
    /// undefined `avg` or `percentile_cont` is the caller asking for arithmetic over a type that
    /// has none, and an undefined `st_intersects` is an installation.
    /// </remarks>
    /// <param name="message">The database's own message.</param>
    /// <returns>The function's name, or null when the message is not about one, or is spatial.</returns>
    private static string? NotSpatial(string message)
    {
        const string opening = "function ";

        if (!message.StartsWith(opening, StringComparison.Ordinal))
        {
            return null;
        }

        int bracket = message.IndexOf('(', StringComparison.Ordinal);

        if (bracket <= opening.Length)
        {
            return null;
        }

        string name = message[opening.Length..bracket].Trim();

        // A schema-qualified name is the same question about its last part.
        int dot = name.LastIndexOf('.');

        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        foreach (string spatial in (string[])["st_", "_st_", "postgis_", "geometry_", "geography_"])
        {
            if (name.StartsWith(spatial, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return name.Length > 0 && name.Length < 64 ? name : null;
    }

    private const string NeedsAnAdministrator =
        "This layer cannot be served at the moment. Retrying will not help; it needs attention "
        + "from whoever administers this server.";

    /// <summary>The public form of both statement timeouts.</summary>
    /// <remarks>
    /// Which bound was reached, and whether it was the store's or this server's, is an
    /// operator's fact. What is left is the caller's — their request was too expensive, and
    /// both levers named are ones they set themselves.
    /// </remarks>
    private const string TookTooLong =
        "This query took longer than this service allows and was stopped. The server did not "
        + "fail; it stopped waiting. Narrow the extent or lower resultRecordCount.";
}
