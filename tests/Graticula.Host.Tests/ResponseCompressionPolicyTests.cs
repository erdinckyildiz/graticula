using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Graticula.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-068: a query-shaped response is compressed when asked, a response that carries a
/// credential never is, and the header that tells a cache the two differ is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real pipeline, not a call into the policy class alone.</b> `ResponseCompressionPolicy`
/// is a pure function of a path and a MIME type list, and a test that only calls it would prove
/// the list is right without proving that `Program.cs`'s actual wiring — `UseWhen` branching
/// `app.UseResponseCompression()` onto the allowed paths — agrees with it. That gap is exactly
/// what caught the first two designs this change went through: an
/// <see cref="IHttpsCompressionFeature"/>-based per-request override that read correctly by the
/// policy and still compressed an excluded response, in both directions, before `UseWhen`
/// replaced it. `TestServer` runs the same branch `Program.cs` registers, in process.
/// </para>
/// <para>
/// <b>Falsified, not merely passed.</b> Each of the three tests below was run against a build
/// with `ResponseCompressionPolicy.IsAllowed` hardcoded to <c>false</c> (the compressed-when-
/// asked test failed, expecting <c>"br"</c> and getting nothing), then to <c>true</c> (the
/// token test failed, asserting an empty <c>Content-Encoding</c> against <c>["br"]</c>), and the
/// third by inverting its own assertion (asserting the response <em>was</em> compressed, which
/// failed against the real, uncompressed response) — each failed the way its own assertion says
/// it would, before the policy and the assertion were restored.
/// </para>
/// </remarks>
public sealed class ResponseCompressionPolicyTests
{
    /// <summary>
    /// A query-shaped JSON response, large enough that gzip and brotli both shrink it.
    /// </summary>
    private const string QueryBody =
        """{"objectIdFieldName":"OBJECTID","fields":[],"features":[""" +
        // Repeated, not random: compressible text is the point of the assertion.
        "{\"attributes\":{\"OBJECTID\":1,\"name\":\"a redundant string, a redundant string, a redundant string\"}},"
        + "]}";

    private const string TokenBody = """{"token":"abc123secret","expires":1234567890}""";

    private static TestServer BuildServer()
    {
        IWebHostBuilder builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddResponseCompression(options =>
                {
                    options.EnableForHttps = true;
                    options.Providers.Add<BrotliCompressionProvider>();
                    options.Providers.Add<GzipCompressionProvider>();
                    options.MimeTypes = ResponseCompressionPolicy.MimeTypes;
                });
            })
            .Configure(app =>
            {
                app.UseRouting();

                // The same branch Program.cs registers: compression is only ever wired into
                // the pipeline for a request the allowlist admits.
                app.UseWhen(
                    context => ResponseCompressionPolicy.IsAllowed(context.Request.Path),
                    branch => branch.UseResponseCompression());

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/rest/services/x/FeatureServer/0/query", async context =>
                    {
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync(QueryBody).ConfigureAwait(false);
                    });

                    endpoints.MapPost("/rest/auth/login", async context =>
                    {
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync(TokenBody).ConfigureAwait(false);
                    });
                });
            });

        TestServer server = new(builder);

        // <b>HTTPS, because that is the scheme `EnableForHttps = true` exists for.</b> This
        // server refuses plain HTTP by default (`settings.RequireHttps`), and the BREACH
        // reasoning in `ResponseCompressionPolicy`'s remarks is specifically about the HTTPS
        // path — a test running over HTTP would pass even if `EnableForHttps` were false.
        server.BaseAddress = new Uri("https://localhost/");

        return server;
    }

    [Fact]
    public async Task A_query_response_is_compressed_when_the_client_asks()
    {
        using TestServer server = BuildServer();
        using HttpClient client = server.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/rest/services/x/FeatureServer/0/query");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal("br", Join(response.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", response.Headers.Vary);

        byte[] compressed = await response.Content.ReadAsByteArrayAsync();
        Assert.True(compressed.Length < Encoding.UTF8.GetByteCount(QueryBody));

        using BrotliStream decompressor = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using StreamReader reader = new(decompressor, Encoding.UTF8);
        Assert.Equal(QueryBody, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task The_same_query_response_is_not_compressed_when_the_client_does_not_ask()
    {
        using TestServer server = BuildServer();
        using HttpClient client = server.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/rest/services/x/FeatureServer/0/query");
        request.Headers.AcceptEncoding.Clear();

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(QueryBody, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The one assertion this whole change exists for: BREACH needs a compressed, attacker-
    /// observable response with a secret in it, and a token response must never be that.
    /// </summary>
    [Fact]
    public async Task A_token_response_is_never_compressed_even_when_the_client_asks()
    {
        using TestServer server = BuildServer();
        using HttpClient client = server.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/rest/auth/login");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(TokenBody, await response.Content.ReadAsStringAsync());
    }

    private static string Join(System.Collections.Generic.ICollection<string> values) =>
        string.Join(",", values);
}
