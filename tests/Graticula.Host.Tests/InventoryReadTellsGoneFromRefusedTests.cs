using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Tools;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A read during a migration plan's walk tells a thing that is gone from a refusal — so a service deleted between the
/// listing and the read is skipped, and anything else still stops the plan.
/// </summary>
/// <remarks>
/// Written 2026-10-29: CI's <c>MigrationAgainstARunningServerTests</c> failed because another test deleted its own
/// service while the plan was walking, and the whole plan died on a service nobody meant to migrate.
/// </remarks>
public sealed class InventoryReadTellsGoneFromRefusedTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"No service 'x' is visible to you."}}""")]
    [InlineData(HttpStatusCode.OK, """{"error":{"code":404,"message":"Service not found"}}""")]
    public async Task A_404_in_the_status_or_in_the_error_is_gone(HttpStatusCode status, string body)
    {
        using HttpClient http = new(new Answering(status, body));

        await Assert.ThrowsAsync<InventoryScan.GoneException>(
            () => InventoryScan.GetAsync(http, "https://source.example/rest/services/x/FeatureServer", null, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"error":{"code":498,"message":"Invalid token."}}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"Not permitted."}}""")]
    public async Task A_refusal_is_not_gone(HttpStatusCode status, string body)
    {
        using HttpClient http = new(new Answering(status, body));

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InventoryScan.GetAsync(http, "https://source.example/rest/services/x/FeatureServer", null, CancellationToken.None));

        Assert.IsNotType<InventoryScan.GoneException>(thrown);
    }

    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
