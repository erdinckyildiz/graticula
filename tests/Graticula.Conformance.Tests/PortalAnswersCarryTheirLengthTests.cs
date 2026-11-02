using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A portal answer travels with its length — <c>PortalContentLength</c>. The ArcGIS API for Python hands an error to its
/// own error handler only when the answer has a <c>Content-Length</c>; a chunked one is raised as a raw object, and
/// <c>gis.groups.get</c> of a deleted group raised instead of answering <c>None</c> (found 2026-11-02, ADR-256).
/// </summary>
public sealed class PortalAnswersCarryTheirLengthTests : ArcGisClient
{
    [Theory]
    [InlineData("sharing/rest/community/groups/00000000000000000000000000000000?f=json")]
    [InlineData("sharing/rest/content/items/00000000000000000000000000000000?f=json")]
    [InlineData("sharing/rest/portals/self?f=json")]
    public async Task An_answer_and_an_error_alike_are_sent_with_a_length_not_chunked(string path)
    {
        string root = await RequireServerAsync();

        using HttpResponseMessage answer = await Http.GetAsync(root.TrimEnd('/') + "/" + path);
        string body = await answer.Content.ReadAsStringAsync();

        Assert.True(answer.Headers.TransferEncodingChunked != true, $"{path} was sent chunked: {body}");
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(body), answer.Content.Headers.ContentLength);
    }
}
