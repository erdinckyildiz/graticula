using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Graticula.Host;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-204 §5e item 13: every administrative route the server maps is either the system's or an organisation's own,
/// so a route added later is decided when it is written rather than open to every organisation's administrator by
/// default. A route on neither list is refused to them at run time; this makes the build say so first.
/// </summary>
public sealed partial class EveryAdministrativeRouteIsClassifiedTests
{
    [GeneratedRegex("""Map(Get|Post|Put|Delete|Patch)\(\s*\$?"(/admin/[^"]*)""")]
    private static partial Regex Route();

    private static string Repository()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && at.GetFiles("*.sln").Length == 0)
        {
            at = at.Parent;
        }

        Assert.False(at is null, "This test could not find the repository root from its own path.");
        return at!.FullName;
    }

    [Fact]
    public void Every_admin_route_is_the_systems_or_an_organisations()
    {
        string host = Path.Combine(Repository(), "src", "Graticula.Host");
        List<string> unclassified = [];
        int seen = 0;

        foreach (string file in Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match match in Route().Matches(File.ReadAllText(file)))
            {
                seen++;
                string method = match.Groups[1].Value.ToUpperInvariant();

                // A template's parameters become a sample segment; `{...}` interpolations likewise.
                string path = Regex.Replace(match.Groups[2].Value, @"\{[^}]*\}", "x");
                DefaultHttpContext context = new();
                context.Request.Method = method;
                context.Request.Path = path;

                if (!TenantRouting.IsClassified(context.Request))
                {
                    unclassified.Add($"{method} {match.Groups[2].Value} ({Path.GetFileName(file)})");
                }
            }
        }

        // The control: a route on neither list is unclassified, so the scan below can fail.
        DefaultHttpContext control = new();
        control.Request.Method = "POST";
        control.Request.Path = "/admin/zz-not-classified/x";
        Assert.False(TenantRouting.IsClassified(control.Request));

        Assert.True(seen > 100, $"Only {seen} administrative routes were found; the pattern no longer matches how they are mapped.");
        Assert.True(
            unclassified.Count == 0,
            "These administrative routes are neither the system's nor an organisation's own. Put each in "
            + "TenantRouting.SystemOnly or TenantRouting.OrganisationOwn (Tenancy.cs), deciding whether what it reaches "
            + "is kept apart per organisation:\n  " + string.Join("\n  ", unclassified.Distinct()));
    }
}
