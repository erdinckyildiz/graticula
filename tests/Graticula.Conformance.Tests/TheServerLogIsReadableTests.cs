using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The server's own warnings and errors are a log source, and the answer says what it keeps.
/// </summary>
/// <remarks>Written 2026-09-15 — ADR-045 §5a.</remarks>
[Collection("catalogue walk")]
public sealed class TheServerLogIsReadableTests : ArcGisClient
{
    [Fact]
    public async Task The_server_source_is_listed_and_answers_with_its_limits()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (HttpStatusCode indexStatus, string index) = await RequestAsync(HttpMethod.Get, $"{root}/admin/logs", token!, json: null);
        Assert.Equal(HttpStatusCode.OK, indexStatus);
        Assert.Contains("server", JsonDocument.Parse(index).RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));

        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/logs/server?level=warning", token!, json: null);
        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement answer = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Array, answer.GetProperty("rows").ValueKind);
        Assert.Contains("since it started", answer.GetProperty("scope").GetString(), StringComparison.Ordinal);

        (HttpStatusCode anonymous, _) = await AnonymousAsync("/admin/logs/server");
        Assert.NotEqual(HttpStatusCode.OK, anonymous);
    }
}
