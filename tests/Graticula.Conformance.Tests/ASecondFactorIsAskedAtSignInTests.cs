using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A member who turns on a second factor is asked for a code at sign-in, a token request names the way round, and an
/// administrator can remove it — ADR-201.
/// </summary>
public sealed class ASecondFactorIsAskedAtSignInTests : ArcGisClient
{
    private const string Member = "zz_two_step";
    private const string Password = "a-long-enough-test-password-201";

    private async Task<(HttpStatusCode Status, string Body)> LoginAsync(string root, string? code)
    {
        using StringContent body = new(
            JsonSerializer.Serialize(new { name = Member, password = Password, code }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_code_is_asked_after_the_password_and_an_administrator_can_remove_it()
    {
        string root = await RequireServerAsync();

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = Member, role = "user", userType = "creator" }));
            Assert.True(made is 200 or 201, $"making the member: {made} {madeBody}");
            string issued = JsonDocument.Parse(madeBody).RootElement.GetProperty("password").GetString()!;

            // The issued password is changed first, as every member's is.
            using (StringContent first = new(JsonSerializer.Serialize(new { name = Member, password = issued }), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage signedIn = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), first))
            {
                string token = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
                (HttpStatusCode changed, string why) = await RequestAsync(HttpMethod.Post, $"{root}/rest/auth/password", token,
                    JsonSerializer.Serialize(new { currentPassword = issued, newPassword = Password }));
                Assert.True(changed == HttpStatusCode.OK || changed == HttpStatusCode.NoContent, $"changing the password: {changed} {why}");
            }

            (HttpStatusCode plain, string plainBody) = await LoginAsync(root, null);
            Assert.Equal(HttpStatusCode.OK, plain);
            string session = JsonDocument.Parse(plainBody).RootElement.GetProperty("token").GetString()!;

            (HttpStatusCode begun, string begunBody) = await RequestAsync(HttpMethod.Post, $"{root}/rest/auth/second-factor", session, "{}");
            Assert.True(begun == HttpStatusCode.OK, $"beginning: {begun} {begunBody}");
            byte[] secret = FromBase32(JsonDocument.Parse(begunBody).RootElement.GetProperty("secret").GetString()!);

            (HttpStatusCode confirmed, string confirmedBody) = await RequestAsync(HttpMethod.Post,
                $"{root}/rest/auth/second-factor/confirm", session, JsonSerializer.Serialize(new { code = Code(secret, 0) }));
            Assert.True(confirmed == HttpStatusCode.OK, $"confirming: {confirmed} {confirmedBody}");
            Assert.Equal(10, JsonDocument.Parse(confirmedBody).RootElement.GetProperty("recoveryCodes").GetArrayLength());

            // Without the code: refused, and told so in `details`.
            (HttpStatusCode asked, string askedBody) = await LoginAsync(root, null);
            Assert.Equal(HttpStatusCode.Unauthorized, asked);
            Assert.Contains("secondFactor", JsonDocument.Parse(askedBody).RootElement.GetProperty("error").GetProperty("details")
                .EnumerateArray().Select(d => d.GetString()));

            // With the next step's code: in. (The confirming code's step is spent.)
            (HttpStatusCode withCode, string withCodeBody) = await LoginAsync(root, Code(secret, 1));
            Assert.True(withCode == HttpStatusCode.OK, $"signing in with a code: {withCode} {withCodeBody}");

            // A token request cannot carry it, and says what to do instead.
            (HttpStatusCode tokenStatus, string tokenBody) = await AnonymousAsync(
                $"/rest/generateToken?username={Member}&password={Uri.EscapeDataString(Password)}&client=requestip");
            Assert.Contains("second step", tokenBody, StringComparison.Ordinal);
            _ = tokenStatus;

            (int reset, string resetBody) = await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}/second-factor");
            Assert.True(reset == 204, $"removing it as an administrator: {reset} {resetBody}");

            (HttpStatusCode after, _) = await LoginAsync(root, null);
            Assert.Equal(HttpStatusCode.OK, after);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}");
        }
    }

    /// <summary>RFC 6238's code for the step <paramref name="ahead"/> steps after now.</summary>
    private static string Code(byte[] secret, int ahead)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) + ahead);
#pragma warning disable CA5350 // RFC 6238's default, as the server's.
        byte[] hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0, bits = 0;

        foreach (char c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
