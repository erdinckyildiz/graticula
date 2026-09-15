using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Graticula.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Pages on other origins may read service responses, never with credentials, and never the
/// administrative surfaces — ADR-072.
/// </summary>
/// <remarks>
/// Written 2026-09-15. No response carried <c>Access-Control-Allow-Origin</c> and a preflight
/// answered 405, so a Maps SDK application on any other address could read nothing.
/// </remarks>
public sealed class CrossOriginReadsTests
{
    private const string Page = "https://maps.example.org";

    private static TestServer BuildServer(CrossOriginReads policy)
    {
        IWebHostBuilder builder = new WebHostBuilder()
            .ConfigureServices(services => services.AddRouting())
            .Configure(app =>
            {
                app.UseRouting();

                // The order Program.cs uses: the policy ahead of the exception handler.
                CrossOriginReads.Use(app, policy);

                app.UseExceptionHandler(handler => handler.Run(context =>
                {
                    context.Response.StatusCode = 503;
                    return context.Response.WriteAsync("unavailable");
                }));

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/rest/services/x/FeatureServer/0", () => Results.Json(new { name = "x" }));
                    endpoints.MapPost("/rest/services/x/FeatureServer/0/query", () => Results.Json(new { features = Array.Empty<object>() }));
                    endpoints.MapGet("/rest/services/broken/FeatureServer/0", (HttpContext _) =>
                        throw new InvalidOperationException("broken"));
                    endpoints.MapGet("/admin/services", () => Results.Json(new { services = Array.Empty<object>() }));
                });
            });

        return new TestServer(builder) { BaseAddress = new Uri("https://localhost/") };
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
        : response.Content.Headers.TryGetValues(name, out var content) ? string.Join(", ", content)
        : null;

    [Fact]
    public async Task A_service_response_is_readable_from_any_origin_by_default()
    {
        using TestServer server = BuildServer(CrossOriginReads.Parse(null));
        using HttpRequestMessage request = new(HttpMethod.Get, "/rest/services/x/FeatureServer/0");
        request.Headers.Add("Origin", Page);

        using HttpResponseMessage response = await server.CreateClient().SendAsync(request);

        Assert.Equal("*", Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains("ETag", Header(response, "Access-Control-Expose-Headers"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preflight_is_answered_rather_than_refused_405()
    {
        using TestServer server = BuildServer(CrossOriginReads.Parse(null));
        using HttpRequestMessage request = new(HttpMethod.Options, "/rest/services/x/FeatureServer/0/query");
        request.Headers.Add("Origin", Page);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");

        using HttpResponseMessage response = await server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("POST", Header(response, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        Assert.Contains("authorization", Header(response, "Access-Control-Allow-Headers"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_answered_by_the_exception_handler_is_still_readable()
    {
        using TestServer server = BuildServer(CrossOriginReads.Parse(null));
        using HttpRequestMessage request = new(HttpMethod.Get, "/rest/services/broken/FeatureServer/0");
        request.Headers.Add("Origin", Page);

        using HttpResponseMessage response = await server.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("*", Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task The_administrative_surface_answers_nothing_across_origins()
    {
        using TestServer server = BuildServer(CrossOriginReads.Parse(null));
        using HttpRequestMessage request = new(HttpMethod.Get, "/admin/services");
        request.Headers.Add("Origin", Page);

        using HttpResponseMessage response = await server.CreateClient().SendAsync(request);

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_named_list_allows_only_its_origins_and_varies_by_origin()
    {
        using TestServer server = BuildServer(CrossOriginReads.Parse($"{Page}, https://other.example.org:8443"));

        using HttpRequestMessage allowed = new(HttpMethod.Get, "/rest/services/x/FeatureServer/0");
        allowed.Headers.Add("Origin", Page);
        using HttpResponseMessage yes = await server.CreateClient().SendAsync(allowed);

        using HttpRequestMessage refused = new(HttpMethod.Get, "/rest/services/x/FeatureServer/0");
        refused.Headers.Add("Origin", "https://elsewhere.example.org");
        using HttpResponseMessage no = await server.CreateClient().SendAsync(refused);

        Assert.Equal(Page, Header(yes, "Access-Control-Allow-Origin"));
        Assert.Contains("Origin", Header(yes, "Vary"), StringComparison.Ordinal);
        Assert.Null(Header(no, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task None_turns_it_off()
    {
        using TestServer server = BuildServer(CrossOriginReads.Parse("none"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/rest/services/x/FeatureServer/0");
        request.Headers.Add("Origin", Page);

        using HttpResponseMessage response = await server.CreateClient().SendAsync(request);

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://maps.example.org/")]
    [InlineData("https://maps.example.org/app")]
    [InlineData("maps.example.org")]
    [InlineData("ftp://maps.example.org")]
    public void Something_that_is_not_an_origin_refuses_to_start(string configured)
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => CrossOriginReads.Parse(configured));

        Assert.Contains("CorsOrigins", refused.Message, StringComparison.Ordinal);
    }
}
