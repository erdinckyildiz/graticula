using System;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Schema;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Checks that the identity schema enforces what ADR-015 promises, against a
/// real PostgreSQL. Constraints that only exist in a migration string are
/// constraints nobody has run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IdentitySchemaTests : PostgresFixture
{
    [Fact]
    public async Task Anonymous_exists_as_a_principal_rather_than_as_a_null_check()
    {
        // ADR-015 §2a. If this row is missing, every authorization path grows an
        // "if no user" branch, which is where the bugs live.
        await MigrateAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select kind from principal where name = 'anonymous'");

        Assert.Equal("anonymous", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task An_unknown_principal_kind_is_refused()
    {
        await MigrateAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name) values (gen_random_uuid(), 'wizard', 'merlin')");

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal("23514", error.SqlState);   // check_violation
    }

    [Fact]
    public async Task Two_sessions_cannot_share_a_token_hash()
    {
        await MigrateAsync();
        Guid principal = await CreateUserAsync("ayse");

        await InsertSessionAsync(principal, hash: [1, 2, 3]);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertSessionAsync(principal, hash: [1, 2, 3]));

        Assert.Equal("23505", error.SqlState);   // unique_violation
    }

    [Fact]
    public async Task A_session_cannot_expire_before_it_was_created()
    {
        await MigrateAsync();
        Guid principal = await CreateUserAsync("bora");

        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            insert into session (id, principal_id, token_hash, created_at, expires_at)
            values (gen_random_uuid(), @p, '\x01', now(), now() - interval '1 hour')
            """);
        command.Parameters.AddWithValue("p", principal);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Disabling_a_principal_takes_its_sessions_with_it()
    {
        // ADR-015 §3's central claim is revocation that works. Deleting a
        // principal must not leave orphaned live sessions behind.
        await MigrateAsync();
        Guid principal = await CreateUserAsync("cem");
        await InsertSessionAsync(principal, hash: [9]);

        await using (NpgsqlCommand delete = DataSource.CreateCommand("delete from principal where id = @p"))
        {
            delete.Parameters.AddWithValue("p", principal);
            await delete.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand count = DataSource.CreateCommand("select count(*) from session");
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_role_grant_cannot_reference_a_role_that_does_not_exist()
    {
        // Q-59 has not decided what the roles are, so the table ships empty.
        // The constraint is what stops code inventing one by writing it.
        await MigrateAsync();
        Guid principal = await CreateUserAsync("deniz");

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal_role (principal_id, role_name) values (@p, 'administrator')");
        command.Parameters.AddWithValue("p", principal);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal("23503", error.SqlState);   // foreign_key_violation
    }

    [Fact]
    public async Task The_role_table_ships_empty_because_Q59_is_open()
    {
        await MigrateAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand("select count(*) from role");

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Applying_identity_leaves_the_rollback_window_open()
    {
        // Migration 2 is an expand, so a version-1 component must still start.
        await MigrateAsync();

        SchemaStamp? stamp = await Store().ReadStampAsync(CancellationToken.None);

        Assert.Equal(new SchemaVersion(2), stamp!.Applied);
        Assert.Equal(new SchemaVersion(1), stamp.MinimumReader);
        Assert.True(SchemaCompatibility.Check("server", new SchemaVersion(1), stamp).IsCompatible);
    }

    private async Task<Guid> CreateUserAsync(string name)
    {
        Guid id = Guid.NewGuid();
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name) values (@id, 'user', @name)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task InsertSessionAsync(Guid principal, byte[] hash)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            insert into session (id, principal_id, token_hash, expires_at)
            values (gen_random_uuid(), @p, @h, now() + interval '1 hour')
            """);
        command.Parameters.AddWithValue("p", principal);
        command.Parameters.AddWithValue("h", hash);
        await command.ExecuteNonQueryAsync();
    }
}
