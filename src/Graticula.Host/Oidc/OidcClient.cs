using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Graticula.Host.Oidc;

/// <summary>
/// The one place this server talks to an OpenID Connect provider — ADR-088.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only outbound HTTP in <c>src/</c>, and on purpose in one file.</b> Q-15's air-gap property is that this
/// server connects to nothing but PostgreSQL, and <c>tools/registers-check.py</c> holds it. Signing in through a
/// provider cannot be done without asking the provider, so this file is the named exception: it connects only to
/// an issuer an operator configured, only when somebody signs in through it or the operator checks it. A server
/// with no provider configured makes no connection here, and an air-gapped site's provider is inside its air gap.
/// </para>
/// <para>
/// <b>The library parses and validates; it does not fetch.</b> <c>Microsoft.IdentityModel</c>'s configuration
/// manager would open its own connections, which the check above cannot see. So the discovery document, the keys
/// and the token exchange are fetched here, and the library is handed text: it reads the configuration and key
/// set, and checks an ID token's signature, issuer, audience and lifetime — the part not to write by hand.
/// </para>
/// <para>
/// <b>HTTPS, except to this machine.</b> An issuer on plain HTTP would carry the client secret and the code in the
/// clear; a loopback issuer does not leave the host, which is what a test's provider is.
/// </para>
/// </remarks>
internal sealed class OidcClient
{
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly ConcurrentDictionary<string, (OpenIdConnectConfiguration Configuration, DateTimeOffset ReadAt)> _known =
        new(StringComparer.Ordinal);

    private readonly TimeProvider _clock;

    /// <summary>Creates the client.</summary>
    /// <param name="clock">What now is.</param>
    public OidcClient(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Why this issuer cannot be used, or null when it can.</summary>
    /// <param name="issuer">The issuer URL an operator typed.</param>
    /// <returns>The refusal.</returns>
    public static string? IssuerRefusal(string issuer) =>
        !Uri.TryCreate(issuer, UriKind.Absolute, out Uri? uri)
            ? "The issuer is a URL, as in https://login.example.org/realms/staff."
            : uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                ? null
                : "The issuer must be HTTPS: over HTTP the sign-in code and this server's secret would cross the "
                  + "network in the clear. Plain HTTP is accepted only for a provider on this machine.";

    /// <summary>The provider's configuration and keys, read at most hourly unless <paramref name="again"/>.</summary>
    /// <param name="issuer">The issuer.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="again">True to read again, as when a token names a key not held.</param>
    /// <returns>The configuration, its signing keys filled in.</returns>
    public async Task<OpenIdConnectConfiguration> DiscoverAsync(string issuer, CancellationToken cancellation, bool again = false)
    {
        string key = issuer.TrimEnd('/');
        DateTimeOffset now = _clock.GetUtcNow();

        if (!again && _known.TryGetValue(key, out var held) && now - held.ReadAt < Fresh)
        {
            return held.Configuration;
        }

        OpenIdConnectConfiguration configuration = OpenIdConnectConfiguration.Create(
            await GetAsync($"{key}/.well-known/openid-configuration", cancellation).ConfigureAwait(false));

        if (string.IsNullOrEmpty(configuration.AuthorizationEndpoint) || string.IsNullOrEmpty(configuration.TokenEndpoint)
            || string.IsNullOrEmpty(configuration.JwksUri))
        {
            throw new OidcException(
                "The provider's discovery document does not name its authorization, token and key endpoints.");
        }

        if (IssuerRefusal(configuration.TokenEndpoint) is { } unsafeEndpoint)
        {
            throw new OidcException($"The provider's token endpoint is not usable: {unsafeEndpoint}");
        }

        JsonWebKeySet keys = new(await GetAsync(configuration.JwksUri, cancellation).ConfigureAwait(false));

        foreach (SecurityKey signing in keys.GetSigningKeys())
        {
            configuration.SigningKeys.Add(signing);
        }

        _known[key] = (configuration, now);
        return configuration;
    }

    /// <summary>Exchanges a code for an ID token at the provider's token endpoint.</summary>
    /// <param name="configuration">The provider's configuration.</param>
    /// <param name="clientId">This server's client id.</param>
    /// <param name="secret">This server's client secret, or null for a public client.</param>
    /// <param name="code">The code the provider returned.</param>
    /// <param name="verifier">The PKCE verifier the code was asked for with.</param>
    /// <param name="redirectUri">The redirect URI the code was asked for with.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The ID token.</returns>
    public static async Task<string> RedeemAsync(
        OpenIdConnectConfiguration configuration,
        string clientId,
        string? secret,
        string code,
        string verifier,
        string redirectUri,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["client_id"] = clientId,
        };

        using HttpRequestMessage request = new(HttpMethod.Post, configuration.TokenEndpoint);

        // client_secret_basic unless the provider says it takes only the secret in the form, which is the
        // default OIDC Core gives and what every provider asked about supports.
        bool postOnly = configuration.TokenEndpointAuthMethodsSupported.Count > 0
            && !configuration.TokenEndpointAuthMethodsSupported.Contains("client_secret_basic")
            && configuration.TokenEndpointAuthMethodsSupported.Contains("client_secret_post");

        if (secret is not null && postOnly)
        {
            form["client_secret"] = secret;
        }
        else if (secret is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));
        }

        request.Content = new FormUrlEncodedContent(form);

        using HttpResponseMessage response = await Send(request, cancellation).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new OidcException(
                $"The provider refused the sign-in code ({(int)response.StatusCode}): {Describe(body)}");
        }

        using JsonDocument answer = JsonDocument.Parse(body);

        return answer.RootElement.TryGetProperty("id_token", out JsonElement idToken) && idToken.ValueKind == JsonValueKind.String
            ? idToken.GetString()!
            : throw new OidcException("The provider answered without an ID token; is the openid scope asked for?");
    }

    /// <summary>
    /// Validates an ID token — signature, issuer, audience, lifetime and nonce — and returns its claims.
    /// </summary>
    /// <param name="issuer">The configured issuer, for a key refresh.</param>
    /// <param name="configuration">The provider's configuration.</param>
    /// <param name="idToken">The token.</param>
    /// <param name="clientId">This server's client id, the audience.</param>
    /// <param name="nonce">The nonce the sign-in was started with.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The claims.</returns>
    public async Task<ClaimsIdentity> ValidateAsync(
        string issuer,
        OpenIdConnectConfiguration configuration,
        string idToken,
        string clientId,
        string nonce,
        CancellationToken cancellation)
    {
        TokenValidationResult result = await Validate(configuration, idToken, clientId).ConfigureAwait(false);

        // A key the provider rotated in since the keys were read: read them once more, and only once.
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            configuration = await DiscoverAsync(issuer, cancellation, again: true).ConfigureAwait(false);
            result = await Validate(configuration, idToken, clientId).ConfigureAwait(false);
        }

        if (!result.IsValid)
        {
            throw new OidcException($"The provider's ID token is not valid: {result.Exception?.Message}");
        }

        if (!result.Claims.TryGetValue("nonce", out object? said) || !string.Equals(said as string, nonce, StringComparison.Ordinal))
        {
            throw new OidcException("The provider's ID token was not issued for this sign-in (its nonce differs).");
        }

        return result.ClaimsIdentity;
    }

    private Task<TokenValidationResult> Validate(OpenIdConnectConfiguration configuration, string idToken, string clientId) =>
        new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidAudience = clientId,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                DateTime now = _clock.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now.AddMinutes(2)) && expires is { } until && until >= now.AddMinutes(-2);
            },
        });

    private static async Task<string> GetAsync(string url, CancellationToken cancellation)
    {
        if (IssuerRefusal(url) is { } refusal)
        {
            throw new OidcException(refusal);
        }

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await Send(request, cancellation).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        return response.StatusCode == HttpStatusCode.OK
            ? body
            : throw new OidcException($"{url} answered {(int)response.StatusCode}.");
    }

    private static async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellation)
    {
        try
        {
            return await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new OidcException($"The provider could not be reached at {request.RequestUri?.GetLeftPart(UriPartial.Authority)}: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new OidcException($"The provider at {request.RequestUri?.GetLeftPart(UriPartial.Authority)} did not answer within 10 seconds.");
        }
    }

    /// <summary>The provider's own error, as far as it says one.</summary>
    private static string Describe(string body)
    {
        try
        {
            using JsonDocument error = JsonDocument.Parse(body);
            string? code = error.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;
            string? text = error.RootElement.TryGetProperty("error_description", out JsonElement d) ? d.GetString() : null;
            return string.Join(" — ", new[] { code, text }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }
}

/// <summary>A sign-in through a provider that cannot go on, with a sentence a person can act on.</summary>
/// <param name="message">What went wrong.</param>
internal sealed class OidcException(string message) : Exception(message);
