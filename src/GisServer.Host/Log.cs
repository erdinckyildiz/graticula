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
    public static partial void Listening(ILogger logger, string scheme, string address, int port);
}
