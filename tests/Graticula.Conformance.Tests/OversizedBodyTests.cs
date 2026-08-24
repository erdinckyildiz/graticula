using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A request too large to read is refused in words, not by hanging up.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-148](../../docs/architecture-debt.md), found while measuring D-31.</b> Two
/// 600,000-vertex operands encode to about 37 MB against a 30 MB ceiling, and the connection
/// ended after 23 milliseconds: the client saw `EOF occurred in violation of protocol`, which is
/// a TLS error rather than a status code, and nothing reached the request log because no request
/// was ever dispatched to be logged.
/// </para>
/// <para>
/// <b>The bound was right and its delivery was not.</b> An operator's next step after *the
/// connection died* is to look at the server, and there was nothing there to find.
/// </para>
/// </remarks>
public sealed class OversizedBodyTests : ArcGisClient
{
    /// <summary>The ceiling the service advertises, which is what a caller would size against.</summary>
    private async Task<long?> CeilingAsync(string root, string token)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{root}/rest/services/Utilities/Geometry/GeometryServer?f=json");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        JsonElement document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return document.TryGetProperty("maximumRequestBytes", out JsonElement bytes)
            && bytes.ValueKind == JsonValueKind.Number
                ? bytes.GetInt64()
                : null;
    }

    /// <summary>
    /// The service says how large a body it will read.
    /// </summary>
    /// <remarks>
    /// <b>Because `maximumVertices` alone is not enough to size a request with.</b> A batch
    /// inside the vertex bound can be past the byte bound, which is exactly the case that used to
    /// end the connection.
    /// </remarks>
    [Fact]
    public async Task The_service_document_says_how_large_a_body_it_reads()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs a token");

        long? ceiling = await CeilingAsync(root, token!);

        Assert.True(
            ceiling is > 0,
            "the geometry service does not report maximumRequestBytes, so a caller can only find "
            + "the byte ceiling by hitting it");
    }

    /// <summary>
    /// A body past the ceiling answers 413 with both bounds named.
    /// </summary>
    /// <remarks>
    /// <b>413 rather than 400, which is the whole of the row.</b> *Too large* and *wrong* want
    /// different things done about them, and a shared 400 cannot say which this is. The body is
    /// built out of one repeated field so the test costs a string rather than a geometry.
    /// </remarks>
    [Fact]
    public async Task A_body_past_the_ceiling_is_refused_in_words()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs a token");

        long ceiling = await CeilingAsync(root, token!)
            ?? throw new InvalidOperationException("no ceiling is advertised");

        // Just over it, and no larger: this is bytes on a socket.
        int size = (int)Math.Min(ceiling + 4_096, int.MaxValue - 1024);

        StringBuilder body = new(size + 32);
        body.Append("sr=4326&f=json&geometries=");

        while (body.Length < size)
        {
            body.Append('x');
        }

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{root}/rest/services/Utilities/Geometry/GeometryServer/intersect")
        {
            Content = new StringContent(
                body.ToString(), Encoding.ASCII, "application/x-www-form-urlencoded"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token!);

        /*
          <b>`Expect: 100-continue`, which is what a client sending a large body should do and
          what curl does by default past a kilobyte.</b> The server answers 413 before the body
          is sent, and the caller reads it.

          <b>Without it the answer is written and the connection is reset mid-upload</b>, so the
          client raises a socket error and never reads the response — measured with both
          `HttpClient` and Python's `urllib`. That is HTTP rather than this server: the only way
          to be sure a streaming client reads the refusal is to accept all thirty-seven megabytes
          first, which is the cost the ceiling exists to avoid. D-148 records it.
        */
        request.Headers.ExpectContinue = true;

        using HttpResponseMessage response = await Http.SendAsync(request);

        string said = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.RequestEntityTooLarge,
            $"a body of {size:N0} bytes against a {ceiling:N0} byte ceiling answered "
            + $"{(int)response.StatusCode}: {said[..Math.Min(300, said.Length)]}");

        // Both bounds, because a caller who reads only one of them builds the same request again.
        Assert.Contains("MB in one body", said, StringComparison.Ordinal);
        Assert.Contains("vertices", said, StringComparison.Ordinal);
    }
}
