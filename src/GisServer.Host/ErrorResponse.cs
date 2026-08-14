using System;
using System.Threading.Tasks;
using GisServer.Platform.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GisServer.Host;

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
    /// Separate from <see cref="WriteAsync"/> because there is nothing to write:
    /// the caller aborts the connection. This exists so the failure appears in
    /// the log rather than only as a client-side truncation.
    /// </remarks>
    public static void LogTruncated(ILogger logger, Exception exception) =>
        Log.FailedMidResponse(logger, exception);

    /// <summary>Maps an exception to a status and a message for the caller.</summary>
    public static async Task WriteAsync(HttpContext context, Exception exception, ILogger logger)
    {
        (int status, string message) = Classify(exception);

        if (context.Response.HasStarted)
        {
            Log.FailedMidResponse(logger, exception);
            context.Abort();
            return;
        }

        Log.RequestFailed(logger, status, context.Request.Path, exception);

        context.Response.Clear();
        context.Response.StatusCode = status;
        await Results.Json(new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    /// <summary>Which status and sentence an exception earns.</summary>
    /// <remarks>
    /// Internal so it can be tested without a server. The mapping is the part
    /// with judgement in it — whether a dropped table is the caller's problem or
    /// ours — and it is the part a running-server test would exercise least.
    /// </remarks>
    internal static (int Status, string Message) Classify(Exception exception) => exception switch
    {
        // A cancelled statement is nearly always the timeout below rather than a
        // disconnected client, and 504 is the difference between "your query was
        // too expensive" and "the server is broken". The first is actionable.
        PostgresException { SqlState: "57014" } => (
            StatusCodes.Status504GatewayTimeout,
            "The query exceeded the statement timeout on the underlying database. Narrow the "
            + "extent, lower resultRecordCount, or index the geometry column. The server did not "
            + "fail; it stopped waiting."),

        PostgresException { SqlState: "42P01" } => (
            StatusCodes.Status503ServiceUnavailable,
            "The table behind this layer no longer exists. The registration and the database have "
            + "diverged — this is a catalogue problem, not a transient one, and retrying will not "
            + "help."),

        PostgresException { SqlState: "42501" } or PostgresException { SqlState: "28P01" } => (
            StatusCodes.Status503ServiceUnavailable,
            "The server could not authenticate against the layer's database, or lacks permission "
            + "to read the table. The stored credential needs attention."),

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
