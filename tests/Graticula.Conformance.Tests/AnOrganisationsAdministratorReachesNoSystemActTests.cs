using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An organisation's administrator is refused every administrative route that is not the organisation's own — ADR-204
/// condition 5, by enumerating them rather than by picking three.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every route, read from the server's own source</b>, as <c>EveryAdministrativeRouteIsClassifiedTests</c> reads
/// them: each <c>Map{Method}("/admin/…")</c> in the host, with its method. The two lists that decide a route's owner,
/// <c>TenantRouting.SystemOnly</c> and <c>OrganisationOwn</c>, are read from the same source, so a route added to
/// either is walked here without anybody remembering it. Every route that is not the organisation's own — the system's
/// by name, and every one on neither list, which is the system's by default — is sent, by the organisation's
/// administrator, at the organisation's host, and must answer 403.
/// </para>
/// <para>
/// <b>Writes are sent with a body of <c>{}</c> and ids that name nothing</b>, so a route that wrongly let one through
/// would most likely refuse it for the body rather than act; the assertion is on the status either way, and anything
/// but 403 fails by route.
/// </para>
/// <para>
/// <b>Returns early where the store's role is a superuser</b>, as every organisation test does (ADR-204 condition 9).
/// </para>
/// </remarks>
[Collection("server settings")]
public sealed partial class AnOrganisationsAdministratorReachesNoSystemActTests : ArcGisClient
{
    private string _root = string.Empty;

    [GeneratedRegex("""Map(Get|Post|Put|Delete|Patch)\(\s*\$?"(/admin/[^"]*)""")]
    private static partial Regex Route();

    [Fact]
    public async Task Every_route_that_is_not_the_organisations_own_is_refused_to_its_administrator()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");

        (List<(string Method, string Path)> walked, int organisations) = Routes();
        Assert.True(walked.Count > 10, $"Only {walked.Count} system routes were found; the reading of the source no longer matches it.");
        Assert.True(organisations > 50, $"Only {organisations} organisation routes were found; the reading of the source no longer matches it.");

        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"sy{run}", host = $"sy{run}.localhost";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", null, admin, new
        {
            key, name = $"System walk {run}", hosts = new[] { host }, administrator = $"syadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}: {tenant}");

        try
        {
            string theirs = await SettleAsync($"syadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            // The control: the same administrator reaches their own organisation's administration.
            (HttpStatusCode own, JsonElement ownBody) = await SendAsync(HttpMethod.Get, "/admin/members", host, theirs);
            Assert.True(own == HttpStatusCode.OK, $"The organisation's administrator could not read their own members: {(int)own} {ownBody}");

            List<string> reached = [];

            foreach ((string method, string path) in walked)
            {
                (HttpStatusCode status, JsonElement body) = await SendAsync(
                    new HttpMethod(method), path, host, theirs, method == "GET" ? null : new { });

                if (status != HttpStatusCode.Forbidden)
                {
                    reached.Add($"{method} {path} answered {(int)status}: {Shorten(body)}");
                }
            }

            Assert.True(reached.Count == 0,
                $"An organisation's administrator reached {reached.Count} of {walked.Count} routes that are not the "
                + "organisation's own:\n  " + string.Join("\n  ", reached));
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", null, admin);
        }
    }

    /// <summary>
    /// Every administrative route the host maps that is not the organisation's own, with an address that matches it,
    /// and how many organisation routes were left out.
    /// </summary>
    private static (List<(string Method, string Path)> Walked, int Organisations) Routes()
    {
        string src = Path.Combine(RepositoryRoot(), "src", "Graticula.Host");
        string tenancy = File.ReadAllText(Path.Combine(src, "Tenancy.cs"));

        string[] systemOnly = ListIn(tenancy, "SystemOnly");
        string[] organisationOwn = ListIn(tenancy, "OrganisationOwn");

        HashSet<(string, string)> walked = [];
        int organisations = 0;

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match match in Route().Matches(File.ReadAllText(file)))
            {
                string pattern = match.Groups[2].Value;
                string literal = Regex.Replace(pattern, @"\{[^}]*\}", "x");

                bool system = systemOnly.Any(p => Under(literal, p)) || literal.Equals("/admin/settings", StringComparison.OrdinalIgnoreCase);
                bool own = organisationOwn.Any(p => Under(literal, p));

                if (own && !system)
                {
                    organisations++;
                    continue;
                }

                walked.Add((match.Groups[1].Value.ToUpperInvariant(), Address(pattern)));
            }
        }

        // <b>And each system prefix as an address of its own.</b> A route can be mapped with a parameter whose one value
        // is the system's — `/admin/logs/{source}`, under the organisation's `/admin/logs`, where `/admin/logs/server` is
        // the server's process log — so reading the patterns alone counted it the organisation's and never sent it.
        foreach (string prefix in systemOnly.Append("/admin/settings"))
        {
            walked.Add(("GET", prefix));
        }

        return ([.. walked.OrderBy(w => w.Item2, StringComparer.Ordinal).ThenBy(w => w.Item1, StringComparer.Ordinal)], organisations);
    }

    /// <summary>The string literals of one of <c>TenantRouting</c>'s arrays.</summary>
    private static string[] ListIn(string source, string name)
    {
        int at = source.IndexOf($"internal static readonly string[] {name} =", StringComparison.Ordinal);
        Assert.True(at >= 0, $"TenantRouting.{name} is not where this test reads it from.");
        int end = source.IndexOf("];", at, StringComparison.Ordinal);

        return [.. Regex.Matches(source[at..end], "\"(/admin[^\"]*)\"").Select(m => m.Groups[1].Value)];
    }

    private static bool Under(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A path that matches a route pattern: each parameter given a value its constraint takes, naming nothing.</summary>
    private static string Address(string pattern) =>
        Regex.Replace(pattern, @"\{([^}:]+)(?::([^}]*))?\}", m => m.Groups[2].Value switch
        {
            var c when c.StartsWith("guid", StringComparison.Ordinal) => Guid.NewGuid().ToString(),
            var c when c.StartsWith("int", StringComparison.Ordinal) || c.StartsWith("long", StringComparison.Ordinal) => "999999",
            var c when c.StartsWith("length(32)", StringComparison.Ordinal) => new string('0', 32),
            _ => "zz-none",
        });

    private static string Shorten(JsonElement body)
    {
        string text = body.ValueKind == JsonValueKind.Undefined ? "(no body)" : body.ToString();
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host, string? token, object? json = null)
    {
        using HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();

        try
        {
            return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
        catch (JsonException)
        {
            return (response.StatusCode, JsonDocument.Parse(JsonSerializer.Serialize(text[..Math.Min(text.Length, 160)])).RootElement.Clone());
        }
    }

    private async Task<string> SettleAsync(string name, string issued, string host)
    {
        (_, JsonElement first) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, null, new { name, password = issued });
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first.GetProperty("token").GetString(),
            new { currentPassword = issued, newPassword = own });
        (_, JsonElement again) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, null, new { name, password = own });
        return again.GetProperty("token").GetString()!;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !File.Exists(Path.Combine(at.FullName, "CLAUDE.md")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);
        return at!.FullName;
    }
}
