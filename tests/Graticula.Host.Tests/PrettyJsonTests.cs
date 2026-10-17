using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>f=pjson</c> is indented on every face, as ArcGIS writes it — the ArcGIS gap review of 2026-10-17 found it
/// compact everywhere but the geometry service.
/// </summary>
public sealed class PrettyJsonTests
{
    private static async Task<string> RunAsync(string contentType, string body)
    {
        DefaultHttpContext context = new();
        MemoryStream sent = new();
        context.Response.Body = sent;

        await PrettyJson.IndentAsync(context, async c =>
        {
            c.Response.ContentType = contentType;
            await c.Response.WriteAsync(body);
        });

        return Encoding.UTF8.GetString(sent.ToArray());
    }

    [Fact]
    public async Task A_json_answer_is_written_again_indented()
    {
        string pretty = await RunAsync("application/json; charset=utf-8", "{\"currentVersion\":12.1,\"folders\":[\"a\"]}");

        Assert.Contains("\n", pretty);
        Assert.Contains("  \"currentVersion\": 12.1", pretty);
    }

    [Fact]
    public async Task Anything_that_is_not_json_passes_unchanged()
    {
        Assert.Equal("<p>hi</p>", await RunAsync("text/html", "<p>hi</p>"));
        Assert.Equal("{not json", await RunAsync("application/json", "{not json"));
    }
}
