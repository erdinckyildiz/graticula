using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// An owner is a principal that exists, and the schema is what says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-66](../../docs/architecture-debt.md): the one owner column nothing reads had a foreign
/// key; the two that are read had none.</b> <c>layer.owner_principal_id</c> — vestigial since
/// migration 11 moved ownership onto the service — carried
/// <c>references principal on delete set null</c>. <c>service.owner_principal_id</c> and
/// <c>folder.owner_principal_id</c>, which the catalogue reports and the console displays,
/// carried none, measured against the live schema on 2026-08-18 with both counts zero.
/// </para>
/// <para>
/// <b>A dangling owner is invisible, which is what makes it worth a constraint.</b> The catalogue
/// reports an owner name by joining to <c>principal</c>, so an orphaned service reports no owner —
/// indistinguishable from one published before ownership was recorded. Nothing refuses, nothing
/// logs, and the service keeps serving.
/// </para>
/// <para>
/// <b>[ADR-015](../../docs/adr/ADR-015-authentication.md) §6c makes a member delete require a
/// disposition, and this is the floor under that rule rather than a second copy of it.</b> The row
/// said as much: the immediate risk is handled in the one place that could trigger it, and the key
/// is still owed, because the next writer to delete a principal will not have read §6c.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class OwnerKeyTests : PostgresFixture
{
    /// <summary>
    /// Deleting a principal leaves the services it owned pointing at nobody, not at a ghost.
    /// </summary>
    /// <remarks>
    /// <b>The delete goes straight at the table, which is the point.</b> Every path this server
    /// offers already asks for a disposition; what is under test is what happens when somebody
    /// reaches past those paths — a migration in a later release, a hand-written statement during
    /// an incident. Before the key, this left a service owned by a principal that does not exist.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_principal_leaves_no_service_owned_by_a_ghost()
    {
        await MigrateAsync();

        Guid owner = await PrincipalAsync("zz_owner_key_user");
        Guid service = await ServiceAsync("zz_owner_key_service", owner);

        await using (NpgsqlCommand delete = DataSource.CreateCommand(
            "delete from principal where id = @id"))
        {
            delete.Parameters.AddWithValue("id", owner);
            await delete.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand read = DataSource.CreateCommand(
            "select owner_principal_id is null from service where id = @id");

        read.Parameters.AddWithValue("id", service);

        Assert.True(
            (bool?)await read.ExecuteScalarAsync(),
            "The service still points at the principal that was deleted. That is D-66: the "
            + "catalogue reports an owner by joining to principal, so this service now reports no "
            + "owner and looks exactly like one published before ownership was recorded.");
    }

    /// <summary>
    /// A folder's owner is held to the same rule, and it never had a key at all.
    /// </summary>
    /// <remarks>
    /// <b>Migration 18 created <c>folder</c> with the same column and no key.</b> The layer key
    /// was written when the layer was the owned thing and was never moved; the folder's was never
    /// written.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_principal_leaves_no_folder_owned_by_a_ghost()
    {
        await MigrateAsync();

        Guid owner = await PrincipalAsync("zz_owner_key_folder_user");

        await using (NpgsqlCommand make = DataSource.CreateCommand(
            "insert into folder (name, owner_principal_id) values ('zz_owner_key_folder', @o)"))
        {
            make.Parameters.AddWithValue("o", owner);
            await make.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand delete = DataSource.CreateCommand(
            "delete from principal where id = @id"))
        {
            delete.Parameters.AddWithValue("id", owner);
            await delete.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand read = DataSource.CreateCommand(
            "select owner_principal_id is null from folder where name = 'zz_owner_key_folder'");

        Assert.True((bool?)await read.ExecuteScalarAsync());
    }

    /// <summary>
    /// An owner that never existed cannot be written in the first place.
    /// </summary>
    /// <remarks>
    /// <b>The other half, and the one <c>on delete set null</c> does not cover.</b> A delete is
    /// not the only way to get a dangling id: a writer that invents one, or copies a principal id
    /// between two deployments, produces the same invisible orphan without deleting anything.
    /// </remarks>
    [Fact]
    public async Task A_service_cannot_be_written_with_an_owner_that_does_not_exist()
    {
        await MigrateAsync();

        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(
            () => ServiceAsync("zz_owner_key_invented", Guid.NewGuid()));

        // foreign_key_violation
        Assert.Equal("23503", refused.SqlState);
    }

    private async Task<Guid> PrincipalAsync(string name)
    {
        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name) values (@id, 'user', @name)");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);

        await command.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<Guid> ServiceAsync(string name, Guid owner)
    {
        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into service (id, name, owner_principal_id) values (@id, @name, @owner)");

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("owner", owner);

        await command.ExecuteNonQueryAsync();

        return id;
    }
}
