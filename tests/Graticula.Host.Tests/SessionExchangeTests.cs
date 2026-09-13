using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Which requests may trade the browsing cookie for a token — ADR-023 §4c, amended 2026-09-13.
/// </summary>
/// <remarks>
/// <b>The browser's own word for where a request came from, and nothing else.</b> A page elsewhere
/// cannot write <c>Sec-Fetch-Site</c>; a request that does not carry it is refused rather than given
/// the benefit of the doubt.
/// </remarks>
public sealed class SessionExchangeTests
{
    private static HttpRequest Request(string? site)
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";

        if (site is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = site;
        }

        return context.Request;
    }

    [Theory]
    [InlineData("same-origin", true)]
    [InlineData("SAME-ORIGIN", true)]
    [InlineData("same-site", false)]
    [InlineData("cross-site", false)]
    [InlineData("none", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_request_the_browser_marks_as_from_this_origin_may_exchange(string? site, bool allowed)
    {
        Assert.Equal(allowed, AuthEndpoints.FromThisOrigin(Request(site)));
    }
}
