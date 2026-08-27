using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Reading something under <c>admin:viewAllContent</c> leaves a record, on whichever face.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-018 condition 3.</b> *"<c>admin:viewAllContent</c> is auditable. An administrator
/// reading a private layer is legitimate and must leave a record, or the sharing model is
/// decorative."* The privilege has to be wide, so what makes it safe is that using it is visible
/// afterwards — and that is a claim about the audit table, which is why this test reads the
/// table rather than the code.
/// </para>
/// <para>
/// <b>It exists because the condition was met on one route and unmet on every other.</b> Measured
/// 2026-08-27, before the repair: the record was written in the FeatureServer <c>query</c>
/// handler alone. A second administrator asked for a private service's FeatureServer *document*,
/// was answered <c>200</c>, and the audit log gained four rows about building the fixture and
/// none about the read. The condition had been true of the surface somebody would have tested
/// and false of the twenty-odd that serve the same data.
/// </para>
/// <para>
/// <b>The document, not the query, deliberately.</b> Querying would exercise the handler that
/// already worked; the document exercises <see cref="T:Graticula.Host.ServiceLookup"/>, which is
/// what every other face resolves through. A repair that only put the record back where it was
/// would fail here.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b>, because it publishes a service — see
/// <see cref="ContentScopeConformanceTests"/> for why that membership is the whole mechanism.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class OverrideIsRecordedTests : ArcGisClient
{
    /// <summary>The service nobody shared.</summary>
    private const string Service = "zz_override_probe";

    /// <summary>An administrator who does not own it.</summary>
    private const string Other = "zz_override_admin";

    /// <summary>What its password becomes once the generated one is spent.</summary>
    private const string Password = "Override!2026xyz";

    /// <summary>The verb the record carries.</summary>
    private const string Action = "content.read.override";

    /// <summary>
    /// A second administrator reads a private service's document, and the log says so.
    /// </summary>
    [Fact]
    public async Task An_override_read_of_a_private_service_is_recorded()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        // Removed first: a run that failed before its cleanup must not fail the next one with a
        // message about the fixture instead of about the subject. ContentScope learnt this.
        await AdminAsync(root, token!, HttpMethod.Delete, $"/admin/members/{Other}?deleteOwned=true", null);
        await AdminAsync(root, token!, HttpMethod.Delete, $"/admin/featureservices/{Service}", null);

        try
        {
            // ---------------------------------------------- a service its owner shared with nobody
            (HttpStatusCode made, string madeBody) = await AdminAsync(
                root, token!, HttpMethod.Post, "/admin/featureservices",
                JsonSerializer.Serialize(new { name = Service, sharing = "private" }));

            Assert.True(
                made is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Could not create the private probe service: {(int)made}. {Explain(madeBody)}");

            // ------------------------------------------- an administrator who is not its owner
            (HttpStatusCode created, string createdBody) = await AdminAsync(
                root, token!, HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(
                    new { name = Other, role = "administrator", userType = "creator" }));

            Assert.True(
                created is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Could not create the second administrator: {(int)created}. {Explain(createdBody)}");

            string generated = JsonDocument.Parse(createdBody).RootElement
                .GetProperty("password").GetString()!;

            string theirs = await SignInAndSetPasswordAsync(root, generated);

            // <b>The high-water mark, taken before the read.</b> The audit table is the server's
            // and outlives the run; the first version of this test asserted over the last sixty
            // rows and **passed against a build with the record disabled**, because it found the
            // row its own previous run had written. A falsification run caught it, which is the
            // only thing that could have. Everything asserted below has to be newer than this.
            long high = Newest(await AuditAsync(root, token!));

            // ------------------------------------------------------ the read the override allows
            // The document, not `/query`: this is the resolver every face shares.
            using HttpRequestMessage read =
                new(HttpMethod.Get, $"{root}/rest/services/{Service}/FeatureServer?f=json");
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", theirs);

            using HttpResponseMessage answered = await Http.SendAsync(read);
            string document = await answered.Content.ReadAsStringAsync();

            Assert.True(
                answered.StatusCode == HttpStatusCode.OK,
                "An administrator was refused a private service they may see everything of: "
                + $"{(int)answered.StatusCode}. {Explain(document)}");

            // --------------------------------------------------------------- and the record of it
            JsonElement log = await AuditAsync(root, token!);

            JsonElement[] mine = log.GetProperty("rows").EnumerateArray()
                .Where(r => r.GetProperty("cursor").GetInt64() > high)
                .Where(r => r.GetProperty("what").GetString() == Action)
                .Where(r => r.GetProperty("who").GetString() == Other)
                .ToArray();

            Assert.True(
                mine.Length > 0,
                $"An administrator read the private service '{Service}' through its FeatureServer "
                + $"document and the audit log has no '{Action}' row for '{Other}'. The sharing "
                + "model is decorative for whoever holds admin:viewAllContent — ADR-018 "
                + "condition 3. Actions present: "
                + string.Join(
                    ", ",
                    log.GetProperty("rows").EnumerateArray()
                        .Where(r => r.GetProperty("cursor").GetInt64() > high)
                        .Select(r => r.GetProperty("what").GetString())
                        .Distinct()
                        .Take(12)));

            JsonElement row = mine[0];

            Assert.Equal(Service, row.GetProperty("resource").GetString());

            // <b>The scope is on the row, because *which private thing* is the question asked
            // afterwards.</b> A record that says somebody used the override and not what it
            // reached is a record of an event rather than of a read.
            string detail = row.GetProperty("detail").GetString() ?? string.Empty;

            Assert.Contains("private", detail, StringComparison.Ordinal);
            Assert.Contains($"/rest/services/{Service}/FeatureServer", detail, StringComparison.Ordinal);

            // ADR-045 condition 2, applied to the row this test adds: the path is stored and the
            // query string is not, and `f=json` is the harmless half of the same rule that keeps
            // `?token=` out. If a query string ever reaches the detail, it will carry tokens.
            Assert.DoesNotContain("f=json", detail, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(root, token!, HttpMethod.Delete, $"/admin/featureservices/{Service}", null);
            await AdminAsync(root, token!, HttpMethod.Delete, $"/admin/members/{Other}?deleteOwned=true", null);
        }
    }

    /// <summary>
    /// Nothing an ordinary read does writes one of these rows.
    /// </summary>
    /// <remarks>
    /// <b>The other direction, and it is the one that decides whether the register is readable.</b>
    /// A record written on every read would satisfy the assertion above and tell an operator
    /// nothing: the question the audit log answers is *which reads needed the override*, and it
    /// can only answer it if the ordinary ones are absent. <c>LayerAccess.Evaluate</c> returns
    /// the override reason last for exactly this, and this asserts the consequence.
    /// </remarks>
    [Fact]
    public async Task An_ordinary_read_writes_no_override_row()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string? service = await AnyServiceNameAsync();

        Assert.False(service is null, "This server lists no service to read.");

        long high = Newest(await AuditAsync(root, token!));

        using HttpRequestMessage read =
            new(HttpMethod.Get, $"{root}/rest/services/{service}/FeatureServer?f=json");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage answered = await Http.SendAsync(read);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        JsonElement after = await AuditAsync(root, token!);

        string[] added = after.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("cursor").GetInt64() > high)
            .Where(r => r.GetProperty("what").GetString() == Action)
            .Select(r => r.GetProperty("resource").GetString() ?? "?")
            .ToArray();

        Assert.True(
            added.Length == 0,
            "Reading a service the suite's administrator owns wrote an override row for "
            + string.Join(", ", added)
            + ". Every read would then be recorded as an override and the register would answer "
            + "nothing about which ones actually needed the privilege.");
    }

    /// <summary>The newest cursor the log is showing, or zero when it is empty.</summary>
    private static long Newest(JsonElement log) =>
        log.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("cursor").GetInt64())
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>Reads the audit log, newest first.</summary>
    private async Task<JsonElement> AuditAsync(string root, string token)
    {
        (HttpStatusCode status, string body) = await AdminAsync(
            root, token, HttpMethod.Get, "/admin/logs/audit?limit=60", null);

        Assert.True(
            status == HttpStatusCode.OK,
            $"The audit log answered {(int)status}. {Explain(body)}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Spends the generated password and returns a token for the chosen one.</summary>
    /// <remarks>
    /// A created member's password must be replaced before the account will do anything else, so
    /// this is three round trips rather than one, and each says which of them failed.
    /// </remarks>
    private async Task<string> SignInAndSetPasswordAsync(string root, string generated)
    {
        (string? first, string why) = await SignInAsync(root, Other, generated);

        Assert.False(first is null, $"The second administrator could not sign in. {why}");

        using HttpRequestMessage change = new(HttpMethod.Post, $"{root}/rest/auth/password");
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
        change.Content = new StringContent(
            JsonSerializer.Serialize(new { currentPassword = generated, newPassword = Password }),
            Encoding.UTF8, "application/json");

        using HttpResponseMessage changed = await Http.SendAsync(change);
        string said = await changed.Content.ReadAsStringAsync();

        Assert.True(
            changed.IsSuccessStatusCode,
            $"Changing the second administrator's password returned {(int)changed.StatusCode}. "
            + Explain(said));

        (string? theirs, string second) = await SignInAsync(root, Other, Password);

        Assert.False(theirs is null, $"The second administrator could not sign in again. {second}");

        return theirs!;
    }

    /// <summary>Signs in, and says why when it cannot.</summary>
    private async Task<(string? Token, string Why)> SignInAsync(
        string root, string name, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return (null, $"Sign-in returned {(int)response.StatusCode}. {Explain(body)}");
        }

        return JsonDocument.Parse(body).RootElement.TryGetProperty("token", out JsonElement t)
            ? (t.GetString(), string.Empty)
            : (null, $"Sign-in succeeded and returned no token: {Explain(body)}");
    }

    /// <summary>One admin round trip.</summary>
    private async Task<(HttpStatusCode Status, string Body)> AdminAsync(
        string root, string token, HttpMethod method, string path, string? body)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The server's own words, trimmed to something a failure can carry.</summary>
    private static string Explain(string body) =>
        string.IsNullOrWhiteSpace(body)
            ? "The server said nothing."
            : body.Length > 400 ? body[..400] + "…" : body;
}
