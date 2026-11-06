using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The host forwards <c>IRequestLog</c> to the concrete log, so the container disposes it twice at shutdown; the second
/// call took the process down with <c>ObjectDisposedException</c> (2026-11-07). No database is needed: an empty queue
/// writes nothing.
/// </summary>
public sealed class TheRequestLogIsDisposedOnceTests
{
    [Fact]
    public async Task Disposing_the_request_log_twice_is_harmless()
    {
        await using NpgsqlDataSource unreachable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=nobody;Database=none");
        PostgresRequestLog log = new(unreachable);

        await log.DisposeAsync();
        await log.DisposeAsync();
    }
}
