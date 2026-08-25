using System;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Source-generated log messages.
/// </summary>
/// <remarks>
/// <para>
/// CA1848 flags <c>ILogger.LogWarning</c> and friends because each call boxes
/// its arguments and formats eagerly. On a startup path called three times that
/// is irrelevant — but this is a server where A-037 established allocation as
/// the binding constraint, and logging is exactly the thing that migrates from
/// the startup path to the request path without anyone deciding to move it.
/// </para>
/// <para>
/// Doing it the generated way from the start costs a few lines now and removes
/// the judgement call later.
/// </para>
/// </remarks>
internal static partial class Log
{
    // <b>An EventId is unique, and three of them were not.</b> Found 2026-08-19: 1008 named both
    // `AuthorizationIsPortalShaped` and `OverlayKilled`, 1010 named `SetupStillPending` and
    // `OverlayWorkersReclaimed`, and 1014 named three unrelated messages — a datastore refusal, a
    // query's timings and a configuration warning. An operator filtering on an id got two or three
    // things, which is worse than no id: the number reads as an identity.
    //
    // The first declaration of each shared number kept it, so a filter that already worked still
    // works, and the later ones moved to 1017 through 1020. **The check is
    // `LogEventIdTests`, not this comment** — the same lesson as the debt register, where a numbering
    // collision took three entries to notice and then got a tool.
    //
    // The next id is 1033.

    /// <summary>
    /// One request, with its query string redacted.
    /// </summary>
    /// <remarks>
    /// <b>This replaces ASP.NET's own request logging, which writes the raw URL.</b>
    /// ADR-015 §4.1: a token may travel in the query string because Esri clients put
    /// it there, and *"redaction is the code path, and logging the raw query on a
    /// token-bearing route is the bug"*. The framework's line is filtered off in
    /// `Program`; this is what is written instead.
    /// </remarks>
    /// <param name="logger">The logger.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path.</param>
    /// <param name="query">The query string, already redacted.</param>
    /// <param name="status">The status code answered.</param>
    [LoggerMessage(
        EventId = 1032,
        Level = LogLevel.Information,
        Message = "{Method} {Path}{Query} - {Status}")]
    public static partial void Request(
        ILogger logger, string method, string? path, string query, int status);

    [LoggerMessage(
        EventId = 1024,
        Level = LogLevel.Information,
        Message = "The geodatabase inspector is not running: this deployment did not ship the reader, "
                + "so there is nothing it could claim. Uploading a File Geodatabase is refused at the "
                + "door with the same reason, and every other import is unaffected.")]
    public static partial void InspectorIdleWithoutReader(ILogger logger);

    [LoggerMessage(
        EventId = 1025,
        Level = LogLevel.Warning,
        Message = "The geodatabase inspector could not claim work and will try again shortly. The "
                + "platform database is the thing it asks, so this is usually that being briefly away "
                + "— the loop does not exit, because a worker that ends on a failed claim needs a "
                + "restart to come back.")]
    public static partial void InspectorClaimFailed(ILogger logger, System.Exception? exception);

    [LoggerMessage(
        EventId = 1046,
        Level = LogLevel.Warning,
        Message = "A worker has failed to claim work {Times} times over {Minutes:0.#} minutes, "
                + "with the same reason each time: {Reason}. The first one carries the stack "
                + "trace; this line exists so that a reader can tell *still away* from *the "
                + "worker died* without the log growing by a stack trace every three seconds "
                + "(D-133).")]
    public static partial void ClaimStillFailing(
        ILogger logger, int times, double minutes, string reason);

    [LoggerMessage(
        EventId = 1047,
        Level = LogLevel.Information,
        Message = "A worker is claiming work again after {Times} failures over {Minutes:0.#} "
                + "minutes. This is the line that closes an incident: whatever it was asking "
                + "for is back.")]
    public static partial void ClaimRecovered(ILogger logger, int times, double minutes);

    [LoggerMessage(
        EventId = 1048,
        Level = LogLevel.Warning,
        Message = "A data source failed to answer and is not being asked again for "
                + "{Seconds:0.#} s: {Source}. ADR-007 §4.8's N3 — without this, an outage "
                + "becomes a connection storm at exactly the moment recovery is being "
                + "attempted, and every refusal holds a connection for the whole of a "
                + "blackholed connect (D-131).")]
    public static partial void SourceTripped(ILogger logger, double seconds, string source);

    [LoggerMessage(
        EventId = 1026,
        Level = LogLevel.Information,
        Message = "Job {Job} read a geodatabase and recorded what is in it. Its layers are in the "
                + "job's detail; publishing one is a second request naming the layer.")]
    public static partial void InspectFinished(ILogger logger, System.Guid job);

    [LoggerMessage(
        EventId = 1027,
        Level = LogLevel.Warning,
        Message = "Job {Job} failed: {Why} The archive has been deleted, so retrying means uploading "
                + "it again — which is deliberate: nothing here is kept across a container "
                + "replacement.")]
    public static partial void InspectRefused(
        ILogger logger, System.Guid job, string why, System.Exception? exception);

    [LoggerMessage(
        EventId = 1028,
        Level = LogLevel.Information,
        Message = "Job {Job} was claimed and the server is stopping, so it is left unfinished rather "
                + "than failed. Its archive is deleted with the rest, so a restart will find the job "
                + "claimed with nothing to read — which is a state worth seeing rather than hiding.")]
    public static partial void InspectAbandoned(ILogger logger, System.Guid job);

    [LoggerMessage(
        EventId = 1029,
        Level = LogLevel.Error,
        Message = "Job {Job} failed with '{Why}' and the failure could not be written to the job "
                + "store either. It stays claimed and unfinished, which nothing will retry. Both "
                + "exceptions are here because the second one hides the first.")]
    public static partial void InspectUnrecorded(
        ILogger logger, System.Guid job, string why, System.Exception? exception);

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Information,
        Message = "Job {Job} kept a {Megabytes} MB archive at {Path} for a geodatabase reader to open. "
                + "It is deleted when the job finishes either way; an archive still here belongs to a "
                + "job that has not finished, and the two can be reconciled by id.")]
    public static partial void ImportArchiveKept(
        ILogger logger, System.Guid job, long megabytes, string path);

    [LoggerMessage(
        EventId = 1023,
        Level = LogLevel.Warning,
        Message = "The import archive at {Path} could not be deleted after its job finished. It still "
                + "counts against GisServer:ImportScratchBudgetMB, so enough of these will refuse the "
                + "next upload — delete it by hand, and look for what is holding it open.")]
    public static partial void ImportArchiveHeld(
        ILogger logger, string path, System.Exception? exception);

    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Information,
        Message = "Job {Job} published {Landed} of {Asked} geodatabase layers into one service. Which "
                + "ones landed and why the rest did not is in the job's detail, per layer — fifty-five "
                + "layers is fifty-five chances to fail and a job saying only 'failed' is one nobody "
                + "can act on.")]
    public static partial void ImportFinished(
        ILogger logger, System.Guid job, int landed, int asked);

    [LoggerMessage(
        EventId = 1031,
        Level = LogLevel.Information,
        Message = "Swept the import archive {File} ({Megabytes} MB): nothing acted on it for long "
                + "enough that nobody is going to. An inspection keeps its archive so its layers can "
                + "be chosen from, and a publish releases it — this is the case where neither "
                + "happened, and without the sweep it would count against the scratch budget for ever.")]
    public static partial void ImportArchiveSwept(
        ILogger logger, string file, long megabytes);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Debug,
        Message = "The geodatabase reader process had already exited when it was killed. This is the "
                + "ordinary race between checking and killing rather than a fault, and is logged at "
                + "Debug so that a run which hits it often is still visible to somebody looking.")]
    public static partial void ImportReaderAlreadyGone(
        ILogger logger, System.Exception? exception);

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Serving plain HTTP. TLS is disabled by configuration, so credentials and data "
                + "cross the network in clear text. This is intended for local development only.")]
    public static partial void ServingPlainHttp(ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Critical,
        Message = "The platform store is unreachable, so its schema version is unknown. The "
                + "server will not start against a store it cannot identify.")]
    public static partial void PlatformStoreUnreachable(ILogger logger, System.Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Critical, Message = "{Explanation}")]
    public static partial void SchemaIncompatible(ILogger logger, string explanation);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "{Explanation}")]
    public static partial void SchemaCompatible(ILogger logger, string explanation);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Every request is anonymous: authentication is designed (ADR-015) and not yet "
                + "implemented. Do not expose this server to a network you do not control.")]
    public static partial void AuthenticationNotImplemented(ILogger logger);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "Authorization follows the ArcGIS Portal model: roles grant privileges, a user "
                + "type caps them, and reading is governed by each layer's sharing scope rather "
                + "than by any privilege. Groups are not implemented, so an item is private, "
                + "organisation-wide, or public.")]
    public static partial void AuthorizationIsPortalShaped(ILogger logger);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "Every published layer is private, so nothing is readable by anyone but its "
                + "owner. This is the ADR-018 sharing default. If these layers were readable "
                + "before an upgrade, that is expected and reversible: share them with the "
                + "organisation, or publicly, through the admin API.")]
    public static partial void NothingIsShared(ILogger logger);

    [LoggerMessage(
        EventId = 1014,
        Level = LogLevel.Error,
        Message = "The datastore could not be registered as a data source: {Reason}. Feature "
                + "services are unaffected. Vector tile services are NOT available until this "
                + "succeeds, because tiles are served only from hosted data (Q-67) and 'hosted' "
                + "means 'in the datastore'.")]
    public static partial void DatastoreNotRegistered(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Information,
        Message = "Adopted {Entries} tiles ({Megabytes:F1} MB) left by a previous run. Without "
                + "this the budget would count from zero after every restart while the files "
                + "stayed, so the cache would grow without limit while appearing bounded.")]
    public static partial void TileCacheAdopted(ILogger logger, int entries, double megabytes);

    [LoggerMessage(
        EventId = 1016,
        Level = LogLevel.Warning,
        Message = "The tile cache is not working and is being bypassed: {Reason}. Tiles are still "
                + "served — they are rebuilt on every request, which is datastore load ADR-021 "
                + "expected the cache to absorb. This is logged once, not per request.")]
    public static partial void TileCacheDegraded(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Critical,
        // {Minimum} rather than a literal: this message stated 12 for a day
        // after the constant said 8, which is the kind of drift that teaches
        // people the messages are not worth reading.
        Message = "SETUP REQUIRED. This server has no administrator. One-time setup token, valid "
                + "for {Minutes} minutes:\n\n    {Token}\n\nPOST it to /rest/setup with a name "
                + "and a password of at least {Minimum} characters. Everything else is refused "
                + "until then. It is single-use and is not printed again.")]
    public static partial void SetupTokenIssued(
        ILogger logger, string token, int minutes, int minimum);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Critical,
        Message = "SETUP REQUIRED, and a usable setup token has already been issued. It is not "
                + "reprinted: it went to the log of an earlier start, and issuing a second would "
                + "mean two live credentials for a one-time act. If it is lost, delete the row "
                + "from setup_token and restart.")]
    public static partial void SetupStillPending(ILogger logger);

    /// <summary>Startup could not tell whether anything is shared, and carried on.</summary>
    /// <remarks>
    /// <b>[D-152](../../docs/architecture-debt.md).</b> This aside used to be able to stop the
    /// server: it listed every layer, which decrypted every credential, and one unopenable
    /// credential ended <c>Main</c>. It counts now and needs no credential, so reaching here
    /// means the platform store itself is unreadable — worth saying, and not worth refusing to
    /// start over.
    /// </remarks>
    [LoggerMessage(
        EventId = 1049,
        Level = LogLevel.Warning,
        Message = "Could not determine whether anything on this server is shared. The server is "
                + "starting anyway: this is a startup note, not a capability. Check "
                + "/healthz/ready and /admin/health.")]
    public static partial void SharingUnknownAtStartup(ILogger logger, Exception failure);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Critical,
        Message = "NO ADMINISTRATOR. This store has user accounts but no principal holds "
                + "'administrator', so nobody can grant a role, create an account or "
                + "operate this server. The setup flow does not run, because setup is for a store "
                + "with no users at all. Recover with one statement against the platform store: "
                + "insert into principal_role (principal_id, role_name) select id, "
                + "'administrator' from principal where name = 'YOUR-ACCOUNT';")]
    public static partial void NoAdministrator(ILogger logger);

    /// <summary>Where one feature query spent its time.</summary>
    /// <remarks>
    /// <b>D-30. Debug, and the level is the switch.</b> Nothing is timed unless
    /// this logger is enabled for Debug — the caller asks first and skips
    /// the whole mechanism otherwise — so leaving it in production costs a
    /// level check per query. Turn it on with
    /// <c>Logging:LogLevel:query = Debug</c> and every query answers with its
    /// own decomposition instead of a number nobody can explain.
    /// </remarks>
    [LoggerMessage(
        EventId = 1019,
        Level = LogLevel.Debug,
        Message = "query {Layer}: {TotalUs}us total = {LookupUs} lookup + {PrepareUs} prepare "
                + "+ {SqlUs} driver + {DecodeUs} decode + {SerialiseUs} serialise, {Rows} rows, "
                + "{Vertices} vertices, {Bytes} bytes out. Lookup is the per-request catalogue "
                + "read, which is a second round trip to Postgres (D-17); prepare is "
                + "authorization, the described shape and parameter parsing; serialise is the "
                + "remainder of the body \u2014 JSON writing and the flush to the socket.")]
    public static partial void QueryTimings(
        ILogger logger,
        string layer,
        long totalUs,
        long lookupUs,
        long prepareUs,
        long sqlUs,
        long decodeUs,
        long serialiseUs,
        long rows,
        long vertices,
        long bytes);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Debug,
        Message = "'{Parameter}' on {Layer} was accepted and ignored: {Reason}. If ignoring it "
                + "could ever change an answer, that is the silent degradation ADR-008 forbids.")]
    public static partial void QueryParameterIgnored(
        ILogger logger, string parameter, string layer, string reason);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Error,
        Message = "{Path} failed and was answered with {Status}.")]
    public static partial void RequestFailed(
        ILogger logger, int status, Microsoft.AspNetCore.Http.PathString path,
        System.Exception exception);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Error,
        Message = "A response failed after the body had begun, so the client received a truncated "
                + "document and no status. The connection was aborted, which is the only signal "
                + "available once bytes are on the wire.")]
    public static partial void FailedMidResponse(ILogger logger, System.Exception exception);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Warning,
        Message = "Configured under the former product name: {Keys}. The product is Graticula "
                + "since 2026-08-17 (ADR-032) and the same settings are read as Graticula__*. "
                + "The old names still work and this is not an error — but they are a "
                + "compatibility path, and one nobody is told about is one nobody stops relying "
                + "on.")]
    public static partial void ConfiguredUnderTheFormerName(ILogger logger, string keys);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Listening on {Scheme}://{Address}:{Port}")]
    public static partial void Listening(
        ILogger logger, string scheme, System.Net.IPAddress address, int port);

    [LoggerMessage(
        EventId = 1017,
        Level = LogLevel.Warning,
        Message = "An overlay ran past its {DeadlineMs} ms deadline and its worker process was "
                + "killed. This is the designed bound rather than a fault (Q-97): no property of "
                + "the input predicts overlay cost, so the only reliable limit is on execution.")]
    public static partial void OverlayKilled(
        ILogger logger, long deadlineMs, System.Exception? exception);

    [LoggerMessage(
        EventId = 1018,
        Level = LogLevel.Information,
        Message = "Reclaimed {Workers} geometry worker process(es) idle for more than "
                + "{IdleSeconds} s. Each holds a heap that may have grown to its 1 GB ceiling, "
                + "so an unused pool is memory a deployment is not getting back. Information "
                + "rather than a warning: this is the designed behaviour, and the next request "
                + "pays one process start, which is warmed outside its deadline.")]
    public static partial void OverlayWorkersReclaimed(
        ILogger logger, int workers, long idleSeconds, System.Exception? exception);

    [LoggerMessage(
        EventId = 1050,
        Level = LogLevel.Warning,
        Message = "No font on this machine can draw U+{CodePoint} ({Sample}), so labels in that "
                + "script are drawn as boxes. Q-15: this is the air-gap font gap, and it is a "
                + "packaging decision rather than a fault — an image built with no system fonts "
                + "carries Skia's own face, which is Latin. Said once per script. Install a face "
                + "that covers it, or mount one into the image.")]
    public static partial void NoFontForScript(ILogger logger, string codePoint, string sample);

    [LoggerMessage(
        EventId = 1051,
        Level = LogLevel.Warning,
        Message = "Layer '{Layer}' is stored in EPSG:{StoredAs} and was served as "
                + "EPSG:{ServedAs}, which crosses a datum. {Caution} Q-141: said once per "
                + "layer and target reference, to you rather than to the client — the caller "
                + "cannot install shift grids and a protobuf tile has nowhere to carry a "
                + "caution. The same list is on /admin/health under datumShifts.")]
    public static partial void DatumShiftServed(
        ILogger logger, string layer, int storedAs, int servedAs, string caution);

    [LoggerMessage(
        EventId = 1052,
        Level = LogLevel.Debug,
        Message = "Could not determine whether serving '{Layer}' as EPSG:{ServedAs} crosses a "
                + "datum. Debug rather than a warning: the request itself is answering "
                + "normally, this is an aside about it, and the usual cause is the projection "
                + "database being briefly unreachable — the next request asks again.")]
    public static partial void DatumShiftUnknown(
        ILogger logger, string layer, int servedAs, System.Exception exception);
}
