using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The store answers which of a caller's groups confer editing, and which do not.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-036](../../docs/adr/ADR-036-groups.md) §4a as amended 2026-08-25.</b>
/// <c>SharedUpdateTests</c> in the platform suite decides what the rule means; this decides
/// whether the store actually implements it. **That gap is where the original defect lived**:
/// <c>item_update</c> was written, read back for display, and consulted by nothing — so a
/// test of the rule alone would have passed the whole time the setting did nothing.
/// </para>
/// <para>
/// <b>Both directions in one test, because the interesting failure is the permissive
/// one.</b> A query that returns too few groups makes an editor's edit refused, which
/// somebody reports within the hour. A query that returns too many hands editing to members
/// of every group they belong to, and nobody reports it at all.
/// </para>
/// </remarks>
public sealed class SharedUpdateGrantsTests : PostgresFixture
{
    [Fact]
    public async Task Only_a_group_with_shared_update_reaches_the_editable_set()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        PostgresIdentityStore identity = new(DataSource);

        (Guid owner, _) = await MemberAsync("zz_su_owner");
        (Guid member, string memberName) = await MemberAsync("zz_su_member");

        Dictionary<string, GroupItemUpdate> made = new(StringComparer.Ordinal)
        {
            ["zz_su_shared"] = GroupItemUpdate.AllItems,
            ["zz_su_own"] = GroupItemUpdate.OwnItems,
            ["zz_su_none"] = GroupItemUpdate.None,
        };

        Dictionary<string, Guid> ids = new(StringComparer.Ordinal);

        foreach ((string name, GroupItemUpdate update) in made)
        {
            (GroupChange change, Guid id) = await groups
                .CreateAsync(owner, name, name, null, update, CancellationToken.None)
                .ConfigureAwait(true);

            Assert.Equal(GroupChange.Done, change);
            ids[name] = id;

            Assert.Equal(
                GroupChange.Done,
                await groups.SetMemberAsync(
                    owner, false, name, memberName, false, CancellationToken.None));
        }

        (_, _, IReadOnlyList<Guid> inGroups, IReadOnlyList<Guid> editable) =
            await identity.GrantsOfAsync(member, CancellationToken.None);

        // All three confer reading — the change must not have narrowed that.
        foreach (Guid id in ids.Values)
        {
            Assert.Contains(id, inGroups);
        }

        // Exactly one confers editing.
        Assert.Contains(ids["zz_su_shared"], editable);
        Assert.DoesNotContain(ids["zz_su_own"], editable);
        Assert.DoesNotContain(ids["zz_su_none"], editable);
    }

    [Fact]
    public async Task A_principal_in_no_group_gets_two_empty_sets_rather_than_a_failure()
    {
        // The common case, and the one an aggregate over an empty join gets wrong by
        // returning null instead of an empty array.
        await MigrateAsync();

        PostgresIdentityStore identity = new(DataSource);
        (Guid alone, _) = await MemberAsync("zz_su_alone");

        (_, _, IReadOnlyList<Guid> inGroups, IReadOnlyList<Guid> editable) =
            await identity.GrantsOfAsync(alone, CancellationToken.None);

        Assert.Empty(inGroups);
        Assert.Empty(editable);
    }

    private async Task<(Guid Id, string Name)> MemberAsync(string name)
    {
        await using Npgsql.NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) "
            + "values (@id, 'user', @name, 'creator') "
            + "on conflict (name) do update set name = excluded.name returning id");

        Guid id = Guid.NewGuid();
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);

        object? made = await command.ExecuteScalarAsync(CancellationToken.None);

        return (made is Guid existing ? existing : id, name);
    }
}
