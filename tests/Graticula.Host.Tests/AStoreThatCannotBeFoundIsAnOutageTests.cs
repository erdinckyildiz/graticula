using System;
using System.Threading.Tasks;
using Graticula.Host;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The showcase's outage of 2026-11-07, at its root: a store whose name cannot be resolved fails in a form the
/// background loops wait out.
/// </summary>
public sealed class AStoreThatCannotBeFoundIsAnOutageTests
{
    [Fact]
    public async Task Opening_a_store_whose_name_does_not_resolve_is_an_outage()
    {
        // .invalid is reserved never to resolve (RFC 2606), so this asks the resolver and fails without a network.
        await using NpgsqlDataSource store = NpgsqlDataSource.Create(
            "Host=graticula-store.invalid;Port=5432;Username=nobody;Password=nothing;Timeout=5;Pooling=false");

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using NpgsqlConnection connection = await store.OpenConnectionAsync();
        });

        Assert.True(StoreOutage.Is(failure), $"An unresolvable store failed as {failure.GetType().FullName}, which StoreOutage does not know.");
    }

    [Fact]
    public void A_fault_in_what_was_asked_is_not_an_outage()
    {
        Assert.False(StoreOutage.Is(new ArgumentException("a bad parameter")));
        Assert.False(StoreOutage.Is(new FormatException("not a number")));
    }
}
