using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The startup check that says whether row-level security binds the role the platform store is reached as —
/// ADR-204 condition 9.
/// </summary>
/// <remarks>
/// <b>Against a real PostgreSQL, because the answer is the database's.</b> The three roles are made here and
/// dropped afterwards: the connection's own (a superuser in CI, as in every compose deployment shipped before
/// 2026-10-19), one with <c>BYPASSRLS</c>, and one with neither. The first two must be warned about by name and
/// reason; the third must not be.
/// </remarks>
public sealed class PlatformStoreRoleTests
{
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionVariable)
        ?? throw new InvalidOperationException(
            $"{ConnectionVariable} is not set, so nothing can be asked of the database. Set it or "
            + "filter this suite out deliberately; do not let it pass by default.");

    [Fact]
    public async Task The_connection_s_own_role_is_reported_as_the_database_describes_it()
    {
        bool super;
        bool bypass;
        await using (NpgsqlDataSource own = NpgsqlDataSource.Create(Connection))
        await using (NpgsqlCommand command = own.CreateCommand(
            "select rolsuper, rolbypassrls from pg_roles where rolname = current_user"))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            super = reader.GetBoolean(0);
            bypass = reader.GetBoolean(1);
        }

        (bool? bound, List<string> said) = await ReportAsync(Connection);

        Assert.Equal(!(super || bypass), bound);
        Assert.Equal(super || bypass ? 1 : 0, said.Count);
    }

    [Theory]
    [InlineData("nobypassrls", true, null)]
    [InlineData("bypassrls", false, "has BYPASSRLS")]
    public async Task A_role_is_warned_about_exactly_when_the_floor_would_not_bind_it(
        string attribute, bool expected, string? reason)
    {
        string role = "zz_store_role_" + Guid.NewGuid().ToString("n")[..10];
        const string Password = "zz-store-role";

        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(Connection);
        await ExecuteAsync(admin, $"create role {role} login nosuperuser {attribute} password '{Password}'");

        try
        {
            NpgsqlConnectionStringBuilder as_ = new(Connection) { Username = role, Password = Password, Pooling = false };

            (bool? bound, List<string> said) = await ReportAsync(as_.ConnectionString);

            Assert.Equal(expected, bound);

            if (reason is null)
            {
                Assert.Empty(said);
            }
            else
            {
                string line = Assert.Single(said);
                Assert.Contains($"'{role}', which {reason}", line, StringComparison.Ordinal);
                Assert.Contains("ADR-204 condition 9", line, StringComparison.Ordinal);
            }
        }
        finally
        {
            await ExecuteAsync(admin, $"drop role if exists {role}");
        }
    }

    /// <summary>
    /// The startup refusal: several organisations over a role the floor does not bind, and nothing else. One organisation
    /// over a superuser is every deployment shipped before 2026-10-19 and must still start; a database that would not say
    /// is not a reason to stop.
    /// </summary>
    [Theory]
    [InlineData(false, 2, true)]
    [InlineData(false, 7, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 7, false)]
    [InlineData(null, 7, false)]
    public void A_server_refuses_to_start_only_over_several_organisations_and_an_unbound_role(
        bool? bound, int organisations, bool refused) =>
        Assert.Equal(refused, PlatformStoreRole.RefusesToServe(bound, organisations));

    private static async Task<(bool? Bound, List<string> Said)> ReportAsync(string connection)
    {
        Recorder recorder = new();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(connection);
        bool? bound = await PlatformStoreRole.ReportAsync(source, recorder, CancellationToken.None);
        return (bound, recorder.Lines);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using NpgsqlCommand command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>An <see cref="ILogger"/> that keeps the warnings it was told.</summary>
    private sealed class Recorder : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (logLevel >= LogLevel.Warning)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
