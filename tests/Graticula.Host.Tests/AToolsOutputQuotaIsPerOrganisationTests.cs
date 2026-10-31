using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// D-325 (a), owner decision 2026-10-31: what an organisation's tools may hold of the imagery volume is configured per
/// organisation, with a default for every other, and is unlimited when nothing is configured.
/// </summary>
public sealed class AToolsOutputQuotaIsPerOrganisationTests
{
    [Fact]
    public void Nothing_configured_is_unlimited()
    {
        ToolSettings settings = ToolSettings.From(Configured([]));

        Assert.Null(settings.OutputLimitFor(null));
        Assert.Null(settings.OutputLimitFor("acme"));
    }

    [Fact]
    public void An_organisations_own_limit_comes_before_the_default()
    {
        ToolSettings settings = ToolSettings.From(Configured(new()
        {
            ["Graticula:Tools:OutputMegabytes:Default"] = "100",
            ["Graticula:Tools:OutputMegabytes:acme"] = "2048",
        }));

        Assert.Equal(2048L * 1024 * 1024, settings.OutputLimitFor("acme"));
        Assert.Equal(2048L * 1024 * 1024, settings.OutputLimitFor("ACME"));
        Assert.Equal(100L * 1024 * 1024, settings.OutputLimitFor("other"));
        Assert.Equal(100L * 1024 * 1024, settings.OutputLimitFor(null));
    }

    [Fact]
    public void One_organisation_limited_leaves_the_rest_unlimited_and_a_bad_value_is_not_read()
    {
        ToolSettings settings = ToolSettings.From(Configured(new()
        {
            ["Graticula:Tools:OutputMegabytes:acme"] = "0",
            ["Graticula:Tools:OutputMegabytes:Default"] = "lots",
        }));

        Assert.Equal(0L, settings.OutputLimitFor("acme"));
        Assert.Null(settings.OutputLimitFor("other"));
    }

    private static IConfiguration Configured(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
