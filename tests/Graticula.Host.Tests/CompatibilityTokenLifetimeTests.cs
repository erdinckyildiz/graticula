using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// An ArcGIS token lives sixty minutes unless its client asks — ADR-015 §4 mitigation 3, D-268.
/// </summary>
/// <remarks>Written 2026-09-15: a token requested without <c>expiration</c> lived the whole twelve-hour
/// session lifetime.</remarks>
public sealed class CompatibilityTokenLifetimeTests
{
    private static Task<TimeSpan?> LifetimeAsync(string? expiration)
    {
        DefaultHttpContext context = new();

        if (expiration is not null)
        {
            context.Request.QueryString = QueryString.Create("expiration", expiration);
        }

        return AuthEndpoints.RequestedLifetimeAsync(context, CancellationToken.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    public async Task No_usable_expiration_is_sixty_minutes(string? expiration)
    {
        Assert.Equal(TimeSpan.FromMinutes(60), await LifetimeAsync(expiration));
    }

    [Fact]
    public async Task An_expiration_the_client_sends_is_what_is_asked_for()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), await LifetimeAsync("5"));
        Assert.Equal(TimeSpan.FromMinutes(1440), await LifetimeAsync("1440"));
    }

    [Fact]
    public async Task The_form_is_read_before_the_query()
    {
        DefaultHttpContext context = new();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["expiration"] = "15" });
        context.Request.QueryString = QueryString.Create("expiration", "90");

        Assert.Equal(TimeSpan.FromMinutes(15), await AuthEndpoints.RequestedLifetimeAsync(context, CancellationToken.None));
    }
}
