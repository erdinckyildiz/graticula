using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Graticula.Platform.Tenancy;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>Runs alone: these tests change how many organisations the process believes there are.</summary>
[CollectionDefinition("organisation count", DisableParallelization = true)]
public sealed class OrganisationCountRunsAlone;

/// <summary>
/// ADR-204 condition 6: on the datastore every organisation shares, one organisation holds at most its share of the
/// source's permits, so another's requests still find one; on a source only one organisation uses, nothing changes.
/// </summary>
[Collection("organisation count")]
public sealed class AnOrganisationsShareOfTheDatastoreTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);
    private static readonly Guid Main = TenantScope.DefaultTenant;
    private static readonly Guid Venture = new("20000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData(24, 1, 24)]
    [InlineData(24, 2, 16)]
    [InlineData(24, 3, 12)]
    [InlineData(24, 10, 5)]
    [InlineData(24, 100, 2)]
    [InlineData(1, 5, 1)]
    public void The_share_is_two_parts_in_n_plus_one_and_never_below_two(int perSource, int organisations, int share) =>
        Assert.Equal(share, ConnectionBudget.ShareOf(perSource, organisations));

    [Fact]
    public async Task One_organisation_cannot_take_the_shared_datastore_and_the_other_still_gets_in()
    {
        using ConnectionBudget budget = new(worker: 0, perSource: 6, Brief, sharedByOrganisations: s => s == "datastore");
        List<ConnectionBudget.Lease> held = [];
        TenantScope.SetCount(2);

        try
        {
            // Two organisations: a share of ceil(6 * 2 / 3) = 4.
            using (TenantScope.Enter(Venture))
            {
                for (int i = 0; i < 4; i++)
                {
                    held.Add(await budget.EnterAsync("datastore", CancellationToken.None));
                }

                ConnectionBudgetFullException refused = await Assert.ThrowsAsync<ConnectionBudgetFullException>(
                    async () => await budget.EnterAsync("datastore", CancellationToken.None));
                Assert.Contains("organization", refused.Message, StringComparison.Ordinal);
            }

            // The source still has two of its six for the other organisation.
            using (TenantScope.Enter(Main))
            {
                held.Add(await budget.EnterAsync("datastore", CancellationToken.None));
            }

            // On its own database the venture is bound by the source alone.
            using (TenantScope.Enter(Venture))
            {
                for (int i = 0; i < 6; i++)
                {
                    held.Add(await budget.EnterAsync("its own", CancellationToken.None));
                }
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

        // Every share given back: the venture takes its four again.
        TenantScope.SetCount(2);

        try
        {
            using (TenantScope.Enter(Venture))
            {
                for (int i = 0; i < 4; i++)
                {
                    using ConnectionBudget.Lease again = await budget.EnterAsync("datastore", CancellationToken.None);
                }
            }
        }
        finally
        {
            TenantScope.SetCount(1);
        }
    }

    [Fact]
    public async Task With_one_organisation_the_datastore_is_bound_only_by_the_source()
    {
        using ConnectionBudget budget = new(worker: 0, perSource: 6, Brief, sharedByOrganisations: s => s == "datastore");
        List<ConnectionBudget.Lease> held = [];

        try
        {
            using (TenantScope.Enter(Main))
            {
                for (int i = 0; i < 6; i++)
                {
                    held.Add(await budget.EnterAsync("datastore", CancellationToken.None));
                }
            }
        }
        finally
        {
            foreach (ConnectionBudget.Lease lease in held)
            {
                lease.Dispose();
            }
        }

        Assert.Equal(6, held.Count);
    }
}
