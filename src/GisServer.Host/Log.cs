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
        Message = "Authorization is by role only. The four roles of ADR-018 are enforced on read; "
                + "per-item ownership and sharing is not designed yet, so a layer is visible to "
                + "everyone holding 'viewer' or to nobody. The role set is INFERRED and awaits "
                + "the project owner (ADR-018 condition 1).")]
    public static partial void AuthorizationIsRoleOnly(ILogger logger);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "No principal holds 'viewer', so nothing is readable by anyone yet — including "
                + "anonymous callers. This is the ADR-018 §3 default. Grant 'viewer' to a "
                + "principal, or to 'anonymous' to run an open portal.")]
    public static partial void NothingIsReadable(ILogger logger);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Critical,
        Message = "SETUP REQUIRED. This server has no administrator. One-time setup token, valid "
                + "for {Minutes} minutes:\n\n    {Token}\n\nPOST it to /rest/setup with a name "
                + "and a password of at least 12 characters. Everything else is refused until "
                + "then. It is single-use and is not printed again.")]
    public static partial void SetupTokenIssued(ILogger logger, string token, int minutes);

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
                + "'platform-administrator', so nobody can grant a role, create an account or "
                + "operate this server. The setup flow does not run, because setup is for a store "
                + "with no users at all. Recover with one statement against the platform store: "
                + "insert into principal_role (principal_id, role_name) select id, "
                + "'platform-administrator' from principal where name = 'YOUR-ACCOUNT';")]
    public static partial void NoAdministrator(ILogger logger);

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
