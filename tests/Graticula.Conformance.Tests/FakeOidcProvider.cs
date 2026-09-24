using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An OpenID Connect provider small enough to read: discovery, keys, an authorization endpoint that signs in
/// whoever the test says, and a token endpoint that checks the code, the client and the PKCE verifier.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written with nothing but the base library</b>, because this suite may not reference the server's assemblies
/// and a provider built on the same library the server validates with would agree with it by construction. The ID
/// token is an RS256 JWT signed here by hand, which is what makes the server's validation a real check.
/// </para>
/// <para>
/// <b>Plain HTTP on the loopback address</b>, which the server accepts only there — the one place an issuer may be
/// HTTP. It is on the machine the server runs on, as the fixture is, in CI and here.
/// </para>
/// </remarks>
internal sealed class FakeOidcProvider : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RSA _key = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Issued> _codes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    private static readonly string[] Code = ["code"];
    private static readonly string[] Public = ["public"];
    private static readonly string[] Rs256 = ["RS256"];
    private static readonly string[] S256 = ["S256"];
    private static readonly string[] Basic = ["client_secret_basic"];

    private sealed record Issued(string Subject, string Username, string Nonce, string Challenge, string RedirectUri, string ClientId);

    /// <summary>Starts a provider on a free loopback port.</summary>
    /// <param name="clientId">The client id it knows.</param>
    /// <param name="secret">The client secret it expects.</param>
    public FakeOidcProvider(string clientId, string secret)
    {
        ClientId = clientId;
        Secret = secret;

        int port;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        Issuer = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{Issuer}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    /// <summary>The issuer URL to configure on the server.</summary>
    public string Issuer { get; }

    /// <summary>The client id.</summary>
    public string ClientId { get; }

    /// <summary>The client secret.</summary>
    public string Secret { get; }

    /// <summary>Who the next authorization signs in: a subject and the name it gives.</summary>
    public (string Subject, string Username) Next { get; set; } = ("nobody", "nobody");

    /// <summary>The groups the next ID token lists in its <c>groups</c> claim — ADR-089.</summary>
    public string[] NextGroups { get; set; } = [];

    /// <summary>How the next ID token is spoiled, to see the server refuse it; null for a good one.</summary>
    public string? Spoil { get; set; }

    /// <summary>Why the token endpoint last refused a code, or null.</summary>
    public string? LastRefusal { get; private set; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _key.Dispose();
        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                await AnswerAsync(context).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                // The client went away; nothing to answer.
            }
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        string path = context.Request.Url!.AbsolutePath;

        switch (path)
        {
            case "/.well-known/openid-configuration":
                await JsonAsync(context, 200, new Dictionary<string, object>
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = $"{Issuer}/authorize",
                    ["token_endpoint"] = $"{Issuer}/token",
                    ["jwks_uri"] = $"{Issuer}/jwks",
                    ["response_types_supported"] = Code,
                    ["subject_types_supported"] = Public,
                    ["id_token_signing_alg_values_supported"] = Rs256,
                    ["code_challenge_methods_supported"] = S256,
                    ["token_endpoint_auth_methods_supported"] = Basic,
                }).ConfigureAwait(false);
                return;

            case "/jwks":
                RSAParameters p = _key.ExportParameters(false);
                await JsonAsync(context, 200, new
                {
                    keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "k1", n = B64(p.Modulus!), e = B64(p.Exponent!) } },
                }).ConfigureAwait(false);
                return;

            case "/authorize":
                var q = context.Request.QueryString;
                string code = B64(RandomNumberGenerator.GetBytes(24));
                _codes[code] = new Issued(Next.Subject, Next.Username, q["nonce"] ?? "", q["code_challenge"] ?? "", q["redirect_uri"] ?? "", q["client_id"] ?? "");
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation =
                    $"{q["redirect_uri"]}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(q["state"] ?? "")}";
                context.Response.Close();
                return;

            case "/token":
                await TokenAsync(context).ConfigureAwait(false);
                return;

            default:
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
        }
    }

    private async Task TokenAsync(HttpListenerContext context)
    {
        string body;
        using (System.IO.StreamReader reader = new(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        Dictionary<string, string> form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(kv => Uri.UnescapeDataString(kv[0].Replace('+', ' ')), kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "");

        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(ClientId)}:{Uri.EscapeDataString(Secret)}"));

        string? refusal = null;
        Issued? issued = null;

        if (context.Request.Headers["Authorization"] != expected)
        {
            refusal = "the client did not authenticate with its secret";
        }
        else if (!form.TryGetValue("code", out string? code) || !_codes.TryRemove(code, out issued))
        {
            refusal = "the code is not one this provider issued, or it was used";
        }
        else if (issued.RedirectUri != form.GetValueOrDefault("redirect_uri"))
        {
            refusal = "the redirect URI differs from the one the code was asked with";
        }
        else if (B64(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? ""))) != issued.Challenge)
        {
            refusal = "the PKCE verifier does not match";
        }

        if (refusal is not null || issued is null)
        {
            LastRefusal = refusal;
            await JsonAsync(context, 400, new { error = "invalid_grant", error_description = refusal }).ConfigureAwait(false);
            return;
        }

        await JsonAsync(context, 200, new { access_token = "unused", token_type = "Bearer", id_token = IdToken(issued) }).ConfigureAwait(false);
    }

    private string IdToken(Issued who)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = "k1" }));
        string payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = Spoil == "issuer" ? "http://127.0.0.1:1/somebody-else" : Issuer,
            ["sub"] = who.Subject,
            ["aud"] = Spoil == "audience" ? "another-client" : who.ClientId,
            ["iat"] = Spoil == "expired" ? now - 3600 : now,
            ["exp"] = Spoil == "expired" ? now - 1800 : now + 300,
            ["nonce"] = Spoil == "nonce" ? "a-nonce-from-another-sign-in" : who.Nonce,
            ["preferred_username"] = who.Username,
            ["name"] = $"Test {who.Username}",
            ["groups"] = NextGroups,
        }));

        using RSA stranger = RSA.Create(2048);
        byte[] signature = (Spoil == "signature" ? stranger : _key)
            .SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64(signature)}";
    }

    private static async Task JsonAsync(HttpListenerContext context, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
