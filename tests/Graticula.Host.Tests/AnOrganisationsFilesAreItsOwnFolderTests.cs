using System;
using System.IO;
using Graticula.Host;
using Graticula.Platform.Tenancy;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-204 §5e item 12: with several organisations, each registers GeoParquet folders and DuckDB files from its own
/// folder under the root, and the main organisation from the root without the others' folders.
/// </summary>
[Collection("organisation count")]
public sealed class AnOrganisationsFilesAreItsOwnFolderTests : IDisposable
{
    private static readonly Guid Venture = new("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Other = new("30000000-0000-0000-0000-000000000003");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gpq-orgs-" + Guid.NewGuid().ToString("N"));

    public AnOrganisationsFilesAreItsOwnFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "parcels"));
        Directory.CreateDirectory(Path.Combine(_root, GeoParquetSources.OrganisationsFolder, "jv", "roads"));
        Directory.CreateDirectory(Path.Combine(_root, GeoParquetSources.OrganisationsFolder, "other", "secret"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GeoParquetSources Sources() => new(_root)
    {
        OrganisationKey = id => id == Venture ? "jv" : id == Other ? "other" : null,
    };

    [Fact]
    public void Each_organisation_registers_from_its_own_folder_only()
    {
        using GeoParquetSources sources = Sources();
        TenantScope.SetCount(3);

        try
        {
            using (TenantScope.Enter(Venture))
            {
                // A name is read inside its own folder.
                Assert.True(sources.TryLocate("roads", out _, out string? why), why);

                // The main organisation's folder and another organisation's are refused, by whole path too.
                Assert.False(sources.TryLocate(Path.Combine(_root, "parcels"), out _, out why));
                Assert.Contains("your organization's folder", why, StringComparison.Ordinal);
                Assert.False(sources.TryLocate(
                    Path.Combine(_root, GeoParquetSources.OrganisationsFolder, "other", "secret"), out _, out _));

                // Climbing out of its folder by a relative path is the same refusal.
                Assert.False(sources.TryLocate("../other/secret", out _, out _));
            }

            using (TenantScope.Enter(TenantScope.DefaultTenant))
            {
                Assert.True(sources.TryLocate("parcels", out _, out string? why), why);
                Assert.False(sources.TryLocate(
                    Path.Combine(GeoParquetSources.OrganisationsFolder, "jv", "roads"), out _, out why));
                Assert.Contains("another organization's", why, StringComparison.Ordinal);
            }
        }
        finally
        {
            TenantScope.SetCount(1);
        }
    }

    [Fact]
    public void On_a_server_of_one_the_whole_root_is_as_before()
    {
        using GeoParquetSources sources = Sources();

        using (TenantScope.Enter(TenantScope.DefaultTenant))
        {
            Assert.True(sources.TryLocate("parcels", out _, out string? why), why);
            Assert.True(sources.TryLocate(Path.Combine(GeoParquetSources.OrganisationsFolder, "jv", "roads"), out _, out why), why);
        }
    }
}
