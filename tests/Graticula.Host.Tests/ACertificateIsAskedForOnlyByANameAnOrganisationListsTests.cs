using System;
using Graticula.Platform.Tenancy;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The answer a proxy's on-demand TLS reads before it requests a certificate — <c>/healthz/certificate</c>, ADR-204.
/// </summary>
/// <remarks>
/// <b>The case that matters is the default organisation listing nothing.</b> It then answers every host, and a proxy
/// asking where requests go would be told yes for any name a wildcard DNS record sends — each yes a certificate
/// requested against the domain's weekly limit. Only a name an organisation lists is one.
/// </remarks>
public sealed class ACertificateIsAskedForOnlyByANameAnOrganisationListsTests
{
    private static readonly Tenant Default = new(TenantScope.DefaultTenant, "default", "Default", [], "p0", false);

    private static readonly Tenant Deneme = new(Guid.NewGuid(), "deneme", "Deneme", ["deneme.graticula.com"], "p1", false);

    [Fact]
    public void A_listed_name_is_one_however_it_is_written()
    {
        Assert.True(TenantDirectory.Claims([Default, Deneme], "deneme.graticula.com"));
        Assert.True(TenantDirectory.Claims([Default, Deneme], " Deneme.Graticula.COM "));
        Assert.True(TenantDirectory.Claims([Default, Deneme], "deneme.graticula.com:443"));
    }

    [Fact]
    public void A_name_nobody_lists_is_not_one_though_the_default_organisation_would_answer_it()
    {
        Assert.False(TenantDirectory.Claims([Default, Deneme], "typo.graticula.com"));
        Assert.False(TenantDirectory.Claims([Default], "anything.graticula.com"));
        Assert.False(TenantDirectory.Claims([Default, Deneme], ""));
    }

    [Fact]
    public void A_suspended_organisations_name_is_still_one_so_it_answers_its_503_over_tls()
    {
        Assert.True(TenantDirectory.Claims([Default, Deneme with { Suspended = true }], "deneme.graticula.com"));
    }

    [Fact]
    public void The_default_organisations_own_names_are_ones()
    {
        Assert.True(TenantDirectory.Claims([Default with { Hosts = ["demo.graticula.com"] }], "demo.graticula.com"));
    }
}
