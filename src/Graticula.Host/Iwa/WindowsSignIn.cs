using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Oidc;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host.Iwa;

/// <summary>
/// Signing in with Windows — ADR-316: .NET's Negotiate handler on one route, Kerberos with a keytab on Linux and SSPI on
/// Windows, ending in ADR-088's door and this server's own session.
/// </summary>
/// <remarks>
/// <b>The handler is run by this route alone</b>, not by a middleware every request passes: nothing else on this server
/// is authenticated by Negotiate, and a handler in the pipeline would read every request's <c>Authorization</c> header.
/// </remarks>
internal sealed class WindowsSignIn
{
    /// <summary>Whether this server signs people in with Windows: <c>Graticula:Iwa:Enabled</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>The keytab named by <c>Graticula:Iwa:Keytab</c>, or null.</summary>
    public string? Keytab { get; init; }

    /// <summary>Reads <c>Graticula:Iwa</c>; off unless turned on.</summary>
    /// <param name="configuration">The configuration.</param>
    public static WindowsSignIn Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? Text(string name) =>
            configuration[$"Graticula:Iwa:{name}"] is { Length: > 0 } current ? current.Trim()
            : configuration[$"GisServer:Iwa:{name}"] is { Length: > 0 } was ? was.Trim()
            : null;

        bool enabled = Text("Enabled") is { } on
            && (bool.TryParse(on, out bool value)
                ? value
                : throw new InvalidOperationException($"Graticula:Iwa:Enabled is '{on}'; it is true or false (ADR-316)."));

        return new WindowsSignIn { Enabled = enabled, Keytab = Text("Keytab") };
    }

    /// <summary>
    /// Registers the Negotiate handler when turned on — and, on Linux, points GSSAPI at the keytab when the process was
    /// not already given one (ADR-316 §5.2).
    /// </summary>
    /// <param name="services">The services.</param>
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(this);

        if (!Enabled)
        {
            return;
        }

        // <b>Into the process's own environment, where GSSAPI reads it</b>: on Linux `Environment.SetEnvironmentVariable`
        // changes .NET's copy only, and the keytab named that way was never read — measured 2026-11-23 against Samba AD,
        // where every ticket was answered 401 until KRB5_KTNAME was in the container's environment. One the operator
        // set already wins (overwrite 0).
        if (Keytab is { } keytab && OperatingSystem.IsLinux())
        {
            if (Native.Setenv(Terminated("KRB5_KTNAME"), Terminated(keytab), 0) != 0)
            {
                throw new InvalidOperationException($"Graticula:Iwa:Keytab is '{keytab}', and KRB5_KTNAME could not be set for this process.");
            }
        }

        services.AddAuthentication().AddNegotiate();
    }

    /// <summary>Maps the sign-in route.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/rest/auth/iwa/{id:guid}/start", StartAsync);
    }

    /// <summary>The account name a Windows name makes — ADR-316 §5.4: after a backslash, before an @.</summary>
    /// <param name="name">The name Negotiate gives: <c>DOMAIN\user</c> or <c>user@REALM</c>.</param>
    public static string AccountName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string user = name.Contains('\\', StringComparison.Ordinal) ? name[(name.LastIndexOf('\\') + 1)..] : name;
        user = user.Contains('@', StringComparison.Ordinal) ? user[..user.IndexOf('@', StringComparison.Ordinal)] : user;
        return user.Trim().Length == 0 ? name.Trim() : user.Trim();
    }

    private static byte[] Terminated(string text) => System.Text.Encoding.UTF8.GetBytes(text + "\0");

    private static class Native
    {
        // The file itself, as RunOutputs.Native names it: the image carries libc.so.6 and not the development link.
        private const string Libc = "libc.so.6";

        // DllImport rather than LibraryImport, whose generated form needs unsafe code this project allows none of.
        [DllImport(Libc, EntryPoint = "setenv", SetLastError = true)]
        internal static extern int Setenv(byte[] name, byte[] value, int overwrite);
    }

    private static async Task StartAsync(
        HttpContext context,
        Guid id,
        IIdentityProviderStore store,
        LoginService login,
        SecretProtector protector,
        WindowsSignIn windows,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        context.Response.Headers.CacheControl = "no-store";
        string returnTo = AuthEndpoints.Safe(context.Request.Query["return"].ToString() is { Length: > 0 } r ? r : "/server/");

        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { Settings: { Enabled: true, Kind: "iwa" } } provider)
        {
            await OidcEndpoints.PageAsync(context, 404, "No such sign-in", "This server offers no sign-in by that name.").ConfigureAwait(false);
            return;
        }

        if (!windows.Enabled)
        {
            await OidcEndpoints.PageAsync(context, 503, "Windows sign-in is not set up",
                "This server is not configured to sign people in with Windows. An administrator sets Graticula:Iwa (ADR-316).",
                returnTo).ConfigureAwait(false);
            return;
        }

        ILogger log = logs.CreateLogger("Graticula.Iwa");

        // NTLM's middle leg is answered by the handler itself, on the connection that carries it.
        if (await context.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>()
                .GetHandlerAsync(context, NegotiateDefaults.AuthenticationScheme).ConfigureAwait(false)
                is IAuthenticationRequestHandler legs
            && await legs.HandleRequestAsync().ConfigureAwait(false))
        {
            return;
        }

        AuthenticateResult result = await context.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme).ConfigureAwait(false);

        if (!result.Succeeded || result.Principal?.Identity?.Name is not { Length: > 0 } name)
        {
            if (result.Failure is { } failure)
            {
                Log.OidcSignInFailed(log, provider.Settings.Name, failure.Message);
            }

            await context.ChallengeAsync(NegotiateDefaults.AuthenticationScheme).ConfigureAwait(false);

            if (!context.Response.HasStarted && context.Response.StatusCode == StatusCodes.Status401Unauthorized)
            {
                await Results.Content(
                        RestDirectory.SignInMessage(
                            "Windows did not sign you in",
                            result.Failure is null
                                ? "Your browser did not answer with your Windows sign-in. Open this server from a computer "
                                  + "signed in to the domain, by the name your administrator gave it; in Firefox, that name must "
                                  + "be in network.negotiate-auth.trusted-uris."
                                : "Your Windows sign-in was refused. Ask an administrator.",
                            returnTo),
                        "text/html; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }

            return;
        }

        await OidcEndpoints.FinishAsync(
            context, store, login, log, provider, name.ToLowerInvariant(), name, null, [], returnTo, cancellation,
            OAuthEndpoints.Carried(context, protector), AccountName(name)).ConfigureAwait(false);
    }
}
