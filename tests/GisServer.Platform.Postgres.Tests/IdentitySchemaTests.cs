using System;
using System.Collections.Generic;
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
        // The constraint is what stops code inventing a role by writing it.
        // Roles.PrivilegesOf answers "nothing" for an unknown name, which is the
        // safe reading at read time — but the write should never be allowed.
        //
        // The name here was 'administrator' until ADR-018 made that a real role,
        // at which point the test passed by inserting a valid grant and proved
        // nothing. Now it is a name that will not become one.
        await MigrateAsync();
        Guid principal = await CreateUserAsync("deniz");

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal_role (principal_id, role_name) values (@p, 'not-a-role')");
        command.Parameters.AddWithValue("p", principal);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal("23503", error.SqlState);   // foreign_key_violation
    }

    [Fact]
    public async Task The_role_table_ships_the_five_portal_roles_ADR018_adopted()
    {
        // Third version of this assertion in two days, and each change was a
        // decision rather than a fix. It asserted EMPTY while Q-59 was open;
        // then four invented roles; now the five ArcGIS Portal defaults the
        // owner directed us to adopt. Rewritten rather than adjusted each time,
        // because editing a list would hide that the model changed underneath.
        await MigrateAsync();

        await using NpgsqlCommand command =
            DataSource.CreateCommand("select name from role order by name");

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(
            ["administrator", "data_editor", "publisher", "user", "viewer"],
            names);
    }

    [Fact]
    public async Task Anonymous_is_granted_nothing_so_a_fresh_server_is_closed()
    {
        // ADR-018 §3, and the most consequential single row in the schema — or
        // rather, the most consequential absence of one. Until this decision
        // every published layer was world-readable. A test that a row does NOT
        // exist is weak on its own, so it also checks that anonymous is present:
        // otherwise a migration that failed to seed the principal at all would
        // pass this.
        await MigrateAsync();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            select
              (select count(*) from principal where kind = 'anonymous'),
              (select count(*) from principal_role r
                 join principal p on p.id = r.principal_id
                where p.kind = 'anonymous')
            """);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Applying_every_migration_leaves_the_rollback_window_open()
    {
        // Migrations 2 and 3 are both expands, so a version-1 component must
        // still start against a fully migrated store. The applied version moved
        // from 2 to 3 when ADR-018 added the role seed; the minimum reader did
        // not move, and that is the number that matters (ADR-016 §4a).
        await MigrateAsync();

        SchemaStamp? stamp = await Store().ReadStampAsync(CancellationToken.None);

        Assert.Equal(PlatformMigrations.ComponentSchemaVersion, stamp!.Applied);
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
