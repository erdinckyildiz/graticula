using Microsoft.Extensions.Logging;

namespace GisServer.Host;

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
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Listening on {Scheme}://{Address}:{Port}")]
    public static partial void Listening(
        ILogger logger, string scheme, System.Net.IPAddress address, int port);
}
