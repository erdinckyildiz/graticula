using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An organisation may require a second factor of its administrators or of everyone; a member it covers who has none sets
/// one up at sign-in — ADR-201 condition 2, owner decision 2026-10-19 (off unless an administrator turns it on).
/// </summary>
/// <remarks>
/// <b>Alone, and put back in <c>finally</c></b>: the requirement covers this suite's own administrator too. Their
/// token was issued before it is turned on, and an open session is not ended by it, but another class signing in
/// meanwhile would be asked for a code it cannot give.
/// </remarks>
[Collection("server settings")]
public sealed class AnOrganisationMayRequireASecondFactorTests : ArcGisClient
{
    private const string Member = "zz_required_two_step";
    private const string Password = "a-long-enough-test-password-2012";

    private async Task<(HttpStatusCode Status, JsonElement Body)> LoginAsync(string root, string? code)
    {
        using StringContent body = new(
            JsonSerializer.Serialize(new { name = Member, password = Password, code }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), body);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private static string[] Details(JsonElement body) =>
        [.. body.GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetString()!)];

    private async Task<(int Status, string Body)> RequireAsync(string required) =>
        await AdminAsync(HttpMethod.Put, "/admin/security/second-factor", JsonSerializer.Serialize(new { required }));

    [Fact]
    public async Task A_covered_member_without_one_sets_it_up_at_sign_in()
    {
        string root = await RequireServerAsync();
        Assert.False(await TokenAsync(root) is null, "No administrator credential; set the suite's user and password.");

        try
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = Member, role = "user", userType = "creator" }));
            Assert.True(made is 200 or 201, $"making the member: {made} {madeBody}");
            string issued = JsonDocument.Parse(madeBody).RootElement.GetProperty("password").GetString()!;

            using (StringContent first = new(JsonSerializer.Serialize(new { name = Member, password = issued }), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage signedIn = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), first))
            {
                string once = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
                (HttpStatusCode changed, string why) = await RequestAsync(HttpMethod.Post, $"{root}/rest/auth/password", once,
                    JsonSerializer.Serialize(new { currentPassword = issued, newPassword = Password }));
                Assert.True(changed is HttpStatusCode.OK or HttpStatusCode.NoContent, $"changing the password: {changed} {why}");
            }

            // Off by default: nobody is asked.
            (int read, string readBody) = await AdminAsync(HttpMethod.Get, "/admin/security/second-factor");
            Assert.True(read == 200, $"reading the requirement: {read} {readBody}");
            Assert.Equal("off", JsonDocument.Parse(readBody).RootElement.GetProperty("required").GetString());

            // Administrators only: a member who is not one signs in as before.
            (int admins, string adminsBody) = await RequireAsync("administrators");
            Assert.True(admins == 200, $"requiring it of administrators: {admins} {adminsBody}");
            Assert.DoesNotContain(Member, JsonDocument.Parse(adminsBody).RootElement.GetProperty("askedAtNextSignIn")
                .EnumerateArray().Select(n => n.GetString()));
            (HttpStatusCode plain, _) = await LoginAsync(root, null);
            Assert.Equal(HttpStatusCode.OK, plain);

            // Everyone: named among those asked next time, and asked — with the secret to set one up.
            (int all, string allBody) = await RequireAsync("everyone");
            Assert.True(all == 200, $"requiring it of everyone: {all} {allBody}");
            Assert.Contains(Member, JsonDocument.Parse(allBody).RootElement.GetProperty("askedAtNextSignIn")
                .EnumerateArray().Select(n => n.GetString()));

            (HttpStatusCode asked, JsonElement askedBody) = await LoginAsync(root, null);
            Assert.Equal(HttpStatusCode.Unauthorized, asked);
            Assert.Contains("secondFactorSetup", Details(askedBody));
            string secret = askedBody.GetProperty("secondFactorSetup").GetProperty("secret").GetString()!;
            Assert.StartsWith("otpauth://totp/", askedBody.GetProperty("secondFactorSetup").GetProperty("address").GetString(),
                StringComparison.Ordinal);

            // The same secret on the next attempt, so the app scanned the first time still works.
            (_, JsonElement again) = await LoginAsync(root, null);
            Assert.Equal(secret, again.GetProperty("secondFactorSetup").GetProperty("secret").GetString());

            // A wrong code: refused, still being set up.
            (HttpStatusCode wrong, JsonElement wrongBody) = await LoginAsync(root, "000000");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong);
            Assert.Contains("secondFactorSetup", Details(wrongBody));

            // The code the app shows: set up, signed in, and the recovery codes, once.
            byte[] key = ASecondFactorIsAskedAtSignInTests.FromBase32(secret);
            (HttpStatusCode done, JsonElement doneBody) = await LoginAsync(root, ASecondFactorIsAskedAtSignInTests.Code(key, 0));
            Assert.True(done == HttpStatusCode.OK, $"setting it up at sign-in: {done} {doneBody}");
            Assert.Equal(10, doneBody.GetProperty("recoveryCodes").GetArrayLength());
            string session = doneBody.GetProperty("token").GetString()!;

            (HttpStatusCode state, string stateBody) = await RequestAsync(HttpMethod.Get, $"{root}/rest/auth/second-factor", session, null);
            Assert.Equal(HttpStatusCode.OK, state);
            JsonElement mine = JsonDocument.Parse(stateBody).RootElement;
            Assert.True(mine.GetProperty("enabled").GetBoolean());
            Assert.True(mine.GetProperty("required").GetBoolean());

            // From now on it is an ordinary second factor: asked for a code, not for a setup.
            (HttpStatusCode next, JsonElement nextBody) = await LoginAsync(root, null);
            Assert.Equal(HttpStatusCode.Unauthorized, next);
            Assert.Contains("secondFactor", Details(nextBody));
            Assert.False(nextBody.TryGetProperty("secondFactorSetup", out JsonElement setup) && setup.ValueKind == JsonValueKind.Object);

            // A member may not read or change the requirement.
            (HttpStatusCode refused, _) = await RequestAsync(HttpMethod.Get, $"{root}/admin/security/second-factor", session, null);
            Assert.Equal(HttpStatusCode.Forbidden, refused);
        }
        finally
        {
            await RequireAsync("off");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");
        }
    }

    [Fact]
    public async Task A_bad_requirement_is_refused_and_changes_nothing()
    {
        string root = await RequireServerAsync();
        Assert.False(await TokenAsync(root) is null, "No administrator credential; set the suite's user and password.");

        (int status, string body) = await RequireAsync("sometimes");
        Assert.True(status == 400, $"{status} {body}");
        (_, string read) = await AdminAsync(HttpMethod.Get, "/admin/security/second-factor");
        Assert.Equal("off", JsonDocument.Parse(read).RootElement.GetProperty("required").GetString());
    }
}
