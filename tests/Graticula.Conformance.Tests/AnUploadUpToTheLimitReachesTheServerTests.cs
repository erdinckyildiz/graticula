using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The 64 MB a data upload is promised is 64 MB: a body between Kestrel's own 30 MB default and the limit reaches the
/// handler, and one past the limit is refused in this server's words.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found 2026-10-13, measuring ADR-038 condition 1.</b> A geodatabase of 1.2 million points zipped to 31.2 MB, inside
/// the 64 MB <c>HostedDataEndpoints.MaximumBytes</c> names, and the upload was answered <c>413</c> by Kestrel before
/// the handler ran: the routes that read a data file never raised the server's 30,000,000-byte default, which the
/// image routes had. So the limit every refusal quoted was not the limit.
/// </para>
/// <para>
/// <b>The body is not a valid file, on purpose.</b> What is asserted is where the refusal comes from: a 400 about the
/// content is the handler having read the body, and a 413 is the body never having reached it.
/// </para>
/// </remarks>
public sealed class AnUploadUpToTheLimitReachesTheServerTests : ArcGisClient
{
    private static HttpClient Plain() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    private static async Task<(HttpStatusCode Status, string Body)> UploadAsync(string root, string token, int megabytes)
    {
        byte[] body = new byte[megabytes * 1024 * 1024];
        Array.Fill(body, (byte)' ');
        body[0] = (byte)'{';

        using MultipartFormDataContent form = new();
        using ByteArrayContent file = new(body);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json");
        form.Add(file, "file", "large.geojson");
        form.Add(new StringContent($"zz_large_{Guid.NewGuid():N}"[..17]), "name");

        using HttpClient http = Plain();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}/admin/hosted/import")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // <b>Asked first, so a refusal arrives before the body is sent.</b> Without it the server answers 413 and closes
        // while the client is still writing seventy megabytes, and the client reports the closed connection instead of
        // the answer — measured, on the first run of this test: the server's log said 413 and the test said reset.
        request.Headers.ExpectContinue = true;

        using HttpResponseMessage response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forty_megabytes_reach_the_handler_and_seventy_are_refused_by_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (HttpStatusCode read, string readSaid) = await UploadAsync(root, token!, 40);

        Assert.True(
            read == HttpStatusCode.BadRequest,
            $"Forty megabytes, inside the 64 MB limit, answered {(int)read}: {readSaid[..Math.Min(400, readSaid.Length)]}. "
            + "A 413 here is the server's own body limit stopping it before the handler read it.");
        Assert.Contains("\"error\"", readSaid, StringComparison.Ordinal);

        (HttpStatusCode refused, string refusedSaid) = await UploadAsync(root, token!, 70);

        Assert.True(refused == HttpStatusCode.RequestEntityTooLarge, $"Seventy megabytes answered {(int)refused}: {refusedSaid}");
        Assert.Contains("64 MB", refusedSaid, StringComparison.Ordinal);
    }
}
