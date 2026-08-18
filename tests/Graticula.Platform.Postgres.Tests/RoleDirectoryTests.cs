using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Editing what a role grants, and every rule ADR-035 says the store must keep.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against a real database, because every rule here is a rule about rows.</b> The prerequisite
/// check, the administrator refusal and the still-held refusal are all enforced in the directory, and
/// a fake would only prove the directory agrees with itself.
/// </para>
/// <para>
/// <b>Each test creates its own role and removes it.</b> The five built-in roles are shared with the
/// server this suite runs beside, and a test that edited `publisher` and died would leave a
/// deployment with a role nobody meant.
/// </para>
/// </remarks>
public sealed class RoleDirectoryTests : PostgresFixture
{
    private const string Probe = "zz_role_directory_probe";

    /// <summary>The seed in the store is exactly what the code says it should be.</summary>
    /// <remarks>
    /// <b>ADR-035 condition 1, the half that needs a database.</b> Migration 25 writes
    /// <see cref="Roles.Grants"/> into <c>role_privilege</c>; if the two ever diverge, an upgrading
    /// deployment silently gets different grants from a fresh one. `RolePrivilegeCatalogueTests`
    /// pins the code's side; this pins the store's.
    /// </remarks>
    [Fact]
    public async Task The_stored_seed_matches_the_compiled_grants_role_for_role()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);

        IReadOnlyList<RoleGrant> stored = await directory.ListAsync(CancellationToken.None);

        Dictionary<string, ImmutableHashSet<Privilege>> byName =
            stored.ToDictionary(r => r.Name, r => r.Privileges, StringComparer.Ordinal);

        foreach (string role in Roles.All)
        {
            Assert.True(
                byName.ContainsKey(role),
                $"'{role}' is one of ADR-018 §3c's five and the store does not have it. Migration "
                + "25's seed and Roles.All disagree.");

            Assert.Equal(Roles.PrivilegesOf(role), byName[role]);
        }
    }

    /// <summary>A privilege granted without its prerequisite is refused, by name.</summary>
    /// <remarks>
    /// <b>ADR-035 §4e and condition 6.</b> These dependencies used to live in the shape of
    /// `BuildGrants()`'s nesting — `publisher` held `content:create` because it was built on `user` —
    /// and flattening the sets into rows deleted that. A role that publishes and cannot create is
    /// what this refuses.
    /// </remarks>
    [Fact]
    public async Task A_privilege_without_its_prerequisite_is_refused_and_the_refusal_names_it()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);

        (RoleChange outcome, string? detail) = await directory.CreateAsync(
            Probe, "probe", ["content:publishFeatures"], CancellationToken.None);

        Assert.Equal(RoleChange.MissingPrerequisite, outcome);

        Assert.Contains("content:create", detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("content:publishFeatures", detail ?? string.Empty, StringComparison.Ordinal);

        // Nothing was written: a refused create must not leave the role behind with no privileges.
        IReadOnlyList<RoleGrant> after = await directory.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(Probe, after.Select(r => r.Name));
    }

    /// <summary>The wider privilege satisfies a prerequisite for the narrower.</summary>
    /// <remarks>
    /// <b>Where the two rules meet, and the case a naive check gets wrong.</b> If a prerequisite is
    /// checked by set membership alone, a role holding `features:fullEdit` is refused for lacking
    /// `features:edit` — which it contains. That refusal would be unanswerable: ticking the narrower
    /// one is exactly what §4e says the store must not hold.
    /// </remarks>
    [Fact]
    public async Task A_prerequisite_is_met_by_a_privilege_that_contains_it()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);

        try
        {
            (RoleChange outcome, string? detail) = await directory.CreateAsync(
                Probe,
                "probe",
                ["content:create", "features:fullEdit", "admin:manageAllContent"],
                CancellationToken.None);

            Assert.True(
                outcome == RoleChange.Done,
                $"A role holding the wider privileges was refused: {outcome} {detail}");
        }
        finally
        {
            await directory.RemoveAsync(Probe, CancellationToken.None);
        }
    }

    /// <summary>An unknown privilege name is refused, with the name.</summary>
    [Fact]
    public async Task An_unknown_privilege_is_refused()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);

        (RoleChange outcome, string? detail) = await directory.CreateAsync(
            Probe, "probe", ["content:inventedThisMorning"], CancellationToken.None);

        Assert.Equal(RoleChange.UnknownPrivilege, outcome);
        Assert.Equal("content:inventedThisMorning", detail);
    }

    /// <summary>The administrator cannot be edited or removed, at the store.</summary>
    /// <remarks>
    /// <b>ADR-035 condition 2, the write half.</b> The check short-circuits for an administrator
    /// anyway, so an accepted edit would change nothing and report success — which is worse than a
    /// refusal, because somebody would believe it and stop looking.
    /// </remarks>
    [Fact]
    public async Task The_administrator_role_refuses_every_write()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);

        (RoleChange edit, _) = await directory.SetPrivilegesAsync(
            Roles.Administrator, ["content:create"], CancellationToken.None);

        Assert.Equal(RoleChange.Administrator, edit);

        Assert.Equal(
            RoleChange.Administrator,
            await directory.RemoveAsync(Roles.Administrator, CancellationToken.None));

        // <b>And it still holds its seed.</b> A refusal that had already deleted the rows would be
        // the worst of both.
        IReadOnlyList<RoleGrant> after = await directory.ListAsync(CancellationToken.None);

        Assert.Equal(
            Roles.PrivilegesOf(Roles.Administrator),
            after.Single(r => r.Name == Roles.Administrator).Privileges);
    }

    /// <summary>A built-in role's privileges are editable and its existence is not.</summary>
    /// <remarks>
    /// <b>Two different rules, and the refusals say different things.</b> The owner's rule is about
    /// the administrator; being built in only stops removal, because migration 25's seed would
    /// recreate the role on a fresh store and the two would then disagree about what exists.
    /// </remarks>
    [Fact]
    public async Task A_built_in_role_can_be_edited_and_cannot_be_removed()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);

        ImmutableHashSet<Privilege> before = Roles.PrivilegesOf(Roles.DataEditor);

        try
        {
            (RoleChange outcome, string? detail) = await directory.SetPrivilegesAsync(
                Roles.DataEditor, ["features:edit", "content:create"], CancellationToken.None);

            Assert.True(outcome == RoleChange.Done, $"{outcome} {detail}");

            Assert.Equal(
                RoleChange.BuiltIn,
                await directory.RemoveAsync(Roles.DataEditor, CancellationToken.None));
        }
        finally
        {
            // Put the deployment back. This suite shares its store with a running server.
            await directory.SetPrivilegesAsync(
                Roles.DataEditor,
                [.. before.Select(Roles.NameOf)],
                CancellationToken.None);
        }

        Assert.Equal(
            before,
            (await directory.ListAsync(CancellationToken.None))
                .Single(r => r.Name == Roles.DataEditor).Privileges);
    }

    /// <summary>Creating the same role twice is refused rather than merged.</summary>
    [Fact]
    public async Task A_role_that_already_exists_is_refused()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);

        try
        {
            (RoleChange first, _) = await directory.CreateAsync(
                Probe, "probe", ["content:create"], CancellationToken.None);

            Assert.Equal(RoleChange.Done, first);

            (RoleChange again, _) = await directory.CreateAsync(
                Probe, "different description", [], CancellationToken.None);

            Assert.Equal(RoleChange.Exists, again);

            // <b>And the first one's privileges survived.</b> A create that partly applied would be
            // an edit nobody asked for.
            Assert.Contains(
                Privilege.ContentCreate,
                (await directory.ListAsync(CancellationToken.None))
                    .Single(r => r.Name == Probe).Privileges);
        }
        finally
        {
            await directory.RemoveAsync(Probe, CancellationToken.None);
        }
    }

    /// <summary>
    /// The running server's grants follow a write immediately.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that found the defect worth having it.</b> <c>RefreshAsync</c> shared its
    /// body with <c>EnsureFreshAsync</c> and kept the freshness check inside it, so an explicit
    /// refresh **returned without reading** whenever the held answer was younger than thirty seconds
    /// — always, immediately after a request. A revocation therefore took up to thirty seconds to
    /// take effect while the endpoint reported success.
    /// </remarks>
    [Fact]
    public async Task An_edited_role_is_in_force_before_the_write_returns()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);
        using PostgresRoleGrants grants = new(DataSource, NullLogger<PostgresRoleGrants>.Instance);

        try
        {
            (RoleChange made, string? why) = await directory.CreateAsync(
                Probe, "probe", ["content:create"], CancellationToken.None);

            Assert.True(made == RoleChange.Done, $"{made} {why}");

            // Read once, so the held answer is fresh and the guard that used to defeat the refresh
            // would be armed.
            await grants.EnsureFreshAsync(CancellationToken.None);
            Assert.Equal<IEnumerable<Privilege>>([Privilege.ContentCreate], grants.PrivilegesOf(Probe));

            (RoleChange widened, string? detail) = await directory.SetPrivilegesAsync(
                Probe,
                ["content:create", "content:publishFeatures"],
                CancellationToken.None);

            Assert.True(widened == RoleChange.Done, $"{widened} {detail}");

            await grants.RefreshAsync(CancellationToken.None);

            Assert.Contains(Privilege.ContentPublishFeatures, grants.PrivilegesOf(Probe));

            // And the revocation, which is the direction where staleness is unsafe.
            await directory.SetPrivilegesAsync(
                Probe, ["content:create"], CancellationToken.None);

            await grants.RefreshAsync(CancellationToken.None);

            Assert.DoesNotContain(Privilege.ContentPublishFeatures, grants.PrivilegesOf(Probe));
        }
        finally
        {
            await directory.RemoveAsync(Probe, CancellationToken.None);
        }
    }

    /// <summary>A privilege name the build does not know is dropped, not fatal.</summary>
    /// <remarks>
    /// <b>The upgrade case, written by hand because no API can produce it.</b> A store written by a
    /// newer version carries privileges this build has never heard of; refusing to start would make
    /// a rolling upgrade impossible, so the row is ignored and logged.
    /// </remarks>
    [Fact]
    public async Task A_privilege_this_build_does_not_know_is_ignored()
    {
        await MigrateAsync();

        PostgresRoleDirectory directory = new(DataSource);
        using PostgresRoleGrants grants = new(DataSource, NullLogger<PostgresRoleGrants>.Instance);

        try
        {
            await directory.CreateAsync(
                Probe, "probe", ["content:create"], CancellationToken.None);

            await using Npgsql.NpgsqlCommand insert = DataSource.CreateCommand(
                "insert into role_privilege (role_name, privilege) values (@r, 'future:thing')");

            insert.Parameters.AddWithValue("r", Probe);
            await insert.ExecuteNonQueryAsync(CancellationToken.None);

            await grants.RefreshAsync(CancellationToken.None);

            // The known one survives; the unknown one is simply absent.
            Assert.Equal<IEnumerable<Privilege>>(
                [Privilege.ContentCreate], grants.PrivilegesOf(Probe));

            // And the directory, which the screen reads, does the same rather than throwing.
            IReadOnlyList<RoleGrant> listed = await directory.ListAsync(CancellationToken.None);

            Assert.Equal<IEnumerable<Privilege>>(
                [Privilege.ContentCreate], listed.Single(r => r.Name == Probe).Privileges);
        }
        finally
        {
            await directory.RemoveAsync(Probe, CancellationToken.None);
        }
    }
}
