using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Graticula.Platform.Tenancy;
using Graticula.Tiles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-204 condition 6, the rest of it: beside the datastore's share, one organisation holds at most its share of the
/// process's whole budget across every source, and of the tile cache — so another's work still runs and its tiles stay.
/// </summary>
[Collection("organisation count")]
public sealed class AnOrganisationsShareOfTheServerTests : IDisposable
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);
    private static readonly Guid Main = TenantScope.DefaultTenant;
    private static readonly Guid Venture = new("20000000-0000-0000-0000-000000000002");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gpq-share-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task One_organisation_cannot_take_the_whole_process_even_on_its_own_databases()
    {
        using ConnectionBudget budget = new(worker: 6, perSource: 0, Brief);
        List<ConnectionBudget.Lease> held = [];
        TenantScope.SetCount(2);

        try
        {
            // Two organisations: a share of ceil(6 * 2 / 3) = 4 of the process, across any sources.
            using (TenantScope.Enter(Venture))
            {
                for (int i = 0; i < 4; i++)
                {
                    held.Add(await budget.EnterAsync($"its own {i}", CancellationToken.None));
                }

                ConnectionBudgetFullException refused = await Assert.ThrowsAsync<ConnectionBudgetFullException>(
                    async () => await budget.EnterAsync("its own 5", CancellationToken.None));
                Assert.Contains("organization", refused.Message, StringComparison.Ordinal);
            }

            using (TenantScope.Enter(Main))
            {
                held.Add(await budget.EnterAsync("the main one's", CancellationToken.None));
            }
        }
        finally
        {
            foreach (ConnectionBudget.Lease lease in held)
            {
                lease.Dispose();
            }

            TenantScope.SetCount(1);
        }
    }

    [Fact]
    public async Task One_organisations_tiles_do_not_push_out_anothers()
    {
        using FileSystemTileCache cache = new(_root, 10_000, 10_000, TimeSpan.FromHours(1), TimeProvider.System, NullLoggerFactory.Instance);
        Guid mainLayer = Guid.NewGuid(), ventureLayer = Guid.NewGuid();
        byte[] tile = new byte[1_000];
        TenantScope.SetCount(2);

        try
        {
            using (TenantScope.Enter(Main))
            {
                for (int x = 0; x < 3; x++)
                {
                    await cache.WriteAsync(new TileCacheKey(mainLayer, "v1", new TileAddress(10, 0, x)), tile, CancellationToken.None);
                }
            }

            // The venture writes far past its share of ceil(10,000 * 2 / 3) = 6,667 bytes.
            using (TenantScope.Enter(Venture))
            {
                for (int x = 0; x < 12; x++)
                {
                    await cache.WriteAsync(new TileCacheKey(ventureLayer, "v1", new TileAddress(10, 1, x)), tile, CancellationToken.None);
                }
            }

            Assert.InRange(cache.HeldByTenant(Venture), 1, FileSystemTileCache.ShareOf(10_000, 2));
            Assert.Equal(3_000, cache.HeldByTenant(Main));
        }
        finally
        {
            TenantScope.SetCount(1);
        }
    }

    [Theory]
    [InlineData(10_000, 1, 10_000)]
    [InlineData(10_000, 2, 6_667)]
    [InlineData(10_000, 9, 2_000)]
    public void The_cache_share_is_the_same_rule(long budget, int organisations, long share) =>
        Assert.Equal(share, FileSystemTileCache.ShareOf(budget, organisations));
}
