using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// HTTP Basic on the OGC faces, over HTTPS — ADR-178, the owner's answer to Q-163 (2026-10-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Basic, and why only here.</b> QGIS sends the token it is given on its GET requests and not on a WFS
/// Transaction, so a QGIS user could open a layer as editable and never save an edit (ADR-169 condition 2). QGIS's
/// authentication manager sends Basic on every request it makes; a bearer token it does not. The ArcGIS surface and
/// the APIs keep their tokens: Basic is read only on an OGC address (<see cref="AppliesTo"/>), because that is where
/// the clients that need it are, and a password on every request is a cost no other surface has to pay.
/// </para>
/// <para>
/// <b>A sign-in, not a second password check.</b> The first request with a credential goes through
/// <see cref="LoginService.AuthenticateAsync"/> — the same throttle, the same audit record, the same refusal for a
/// disabled account as <c>/rest/auth/login</c> — and opens a fifteen-minute session scoped like an ArcGIS token, so it
/// cannot open the native administration API. That session's token is held here against a hash of the credential, so
/// the next requests in those fifteen minutes cost a session lookup, as a bearer token does, rather than an Argon2
/// derivation each; QGIS fetches a map as dozens of requests. A session the store no longer knows — revoked, or its
/// password changed — is forgotten and the credential is checked again.
/// </para>
/// <para>
/// <b>HTTPS only.</b> Basic is the password itself, base64-encoded. Sent over plain HTTP it is refused with 403 rather
/// than used, so a server behind no TLS does not quietly teach its users to send passwords in the clear.
/// </para>
/// </remarks>
internal sealed class BasicCredentials
{
    private const string Prefix = "Basic ";

    /// <summary>How long a credential's session lasts, and so how long it is held.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>At most this many credentials are held; the oldest goes first.</summary>
    internal const int Capacity = 1000;

    private readonly LoginService _login;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _held = new(StringComparer.Ordinal);
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>Creates the reader.</summary>
    /// <param name="login">The sign-in every credential goes through.</param>
    /// <param name="time">The clock.</param>
    public BasicCredentials(LoginService login, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(time);

        _login = login;
        _time = time;
    }

    /// <summary>Whether a path is an OGC face, where Basic is read.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>True for <c>/wms</c>, <c>/wfs</c>, <c>/wcs</c>, <c>/wmts</c>, the OGC APIs and a service's own OGC addresses.</returns>
    public static bool AppliesTo(PathString path)
    {
        string value = path.Value ?? string.Empty;

        return value.Equals("/wms", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/wfs", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/wcs", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/wmts", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/WMSServer", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/WFSServer", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/WCSServer", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/ImageServer/WMTS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads <c>Authorization: Basic</c>, or answers false when there is none or it is malformed.</summary>
    /// <param name="context">The request.</param>
    /// <param name="name">The account name.</param>
    /// <param name="password">The password.</param>
    /// <returns>Whether the header carried a credential.</returns>
    public static bool TryRead(HttpContext context, out string name, out string password)
    {
        ArgumentNullException.ThrowIfNull(context);

        name = password = string.Empty;
        string? header = context.Request.Headers.Authorization;

        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[Prefix.Length..].Trim()));
            int colon = decoded.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 0)
            {
                return false;
            }

            (name, password) = (decoded[..colon], decoded[(colon + 1)..]);
            return password.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The session token a credential has open, opening one by signing in when it has none.</summary>
    /// <param name="name">The account name.</param>
    /// <param name="password">The password.</param>
    /// <param name="context">The request, for the caller's address.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The token and its key, or the reason it was refused.</returns>
    public async Task<(string? Token, string Key, LoginFailure Failure)> TokenAsync(
        string name, string password, HttpContext context, CancellationToken cancellation)
    {
        string key = KeyOf(name, password);
        DateTimeOffset now = _time.GetUtcNow();

        if (_held.TryGetValue(key, out (string Token, DateTimeOffset Until) held) && held.Until > now)
        {
            return (held.Token, key, LoginFailure.None);
        }

        LoginResult result = await _login
            .AuthenticateAsync(name, password, CallerAddress.Of(context), cancellation, Lifetime, scope: SessionScopes.ArcGis)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _held.TryRemove(key, out _);
            return (null, key, result.Failure);
        }

        Keep(key, result.Token!, now + Lifetime - TimeSpan.FromSeconds(30));
        return (result.Token, key, LoginFailure.None);
    }

    /// <summary>Answers a Basic credential that opened no session.</summary>
    /// <param name="context">The request.</param>
    /// <param name="refusal">Why.</param>
    /// <returns>The write.</returns>
    /// <remarks>
    /// <b>401 with a challenge for a wrong credential</b>, so a client asks its user again rather than going on as a
    /// stranger who sees only what is public — which would look like data missing rather than a password mistyped.
    /// One message for a wrong name, a wrong password and a disabled account, as <c>/rest/auth/login</c> gives.
    /// </remarks>
    public static async Task WriteAsync(HttpContext context, BasicRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(context);

        (int status, string text) = refusal switch
        {
            BasicRefusal.Plaintext => (StatusCodes.Status403Forbidden,
                "This server accepts a name and password (HTTP Basic) over HTTPS only. Connect with https://, and change "
                + "the password if it has been sent over a network you do not trust."),
            BasicRefusal.Throttled => (StatusCodes.Status429TooManyRequests,
                "Too many failed sign-in attempts. Wait and try again."),
            _ => (StatusCodes.Status401Unauthorized, "The name or password is incorrect."),
        };

        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";

        if (refusal == BasicRefusal.Rejected)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Graticula\", charset=\"UTF-8\"";
        }

        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Forgets a credential whose session the store no longer knows.</summary>
    /// <param name="key">The key <see cref="TokenAsync"/> returned.</param>
    public void Forget(string key) => _held.TryRemove(key, out _);

    private void Keep(string key, string token, DateTimeOffset until)
    {
        DateTimeOffset now = _time.GetUtcNow();

        foreach ((string stale, _) in _held.Where(p => p.Value.Until <= now).ToList())
        {
            _held.TryRemove(stale, out _);
        }

        while (_held.Count >= Capacity
            && _held.OrderBy(p => p.Value.Until).FirstOrDefault() is { Key: not null } oldest)
        {
            _held.TryRemove(oldest.Key, out _);
        }

        _held[key] = (token, until);
    }

    /// <summary>
    /// The credential as a key: an HMAC under a key made when the server started, so no password is held in memory and
    /// a memory image gives an offline guess nothing to test against. The name is taken exactly as sent; two names that
    /// differ only in case are two credentials here, whatever the store makes of them.
    /// </summary>
    private string KeyOf(string name, string password) =>
        Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(name + "\0" + password)));
}

/// <summary>Why a Basic credential on an OGC face opened no session — ADR-178.</summary>
internal enum BasicRefusal
{
    /// <summary>Sent over plain HTTP, where it is refused rather than used.</summary>
    Plaintext,

    /// <summary>A wrong name or password, or an account that may not sign in.</summary>
    Rejected,

    /// <summary>Too many failures from this address or for this account.</summary>
    Throttled,
}
