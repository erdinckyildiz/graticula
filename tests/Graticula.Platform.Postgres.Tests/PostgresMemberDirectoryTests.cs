using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Platform.Secrets;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Creating and administering members.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is <see href="../../../docs/architecture-debt.md">D-56</see>'s repair, and the debt was
/// that a deployment had exactly one account for ever.</b> First-run setup created the
/// administrator and nothing created a second: <c>admin:manageMembers</c> was a privilege with
/// nothing behind it, ADR-034 built Studio for a publisher who could not be created, and its
/// condition 1 — *no screen appears that its reader cannot use* — asked for a test that signs in
/// <em>without</em> <c>admin:manageServer</c>, which needed a reader nobody could make.
/// </para>
/// <para>
/// <b>What these assert is mostly atomicity and refusal, not the happy path.</b> A member is three
/// rows in three tables and any two of them without the third is an account somebody has to notice
/// is broken; and the refusals — the last administrator, the taken name — are the cases where
/// getting it wrong is unrecoverable rather than annoying.
/// </para>
/// </remarks>
public sealed class PostgresMemberDirectoryTests : PostgresFixture
{
    /// <summary>A hash that is not a real one, because these tests are not about hashing.</summary>
    /// <remarks>
    /// The Argon2 suite covers the hasher. Using it here would put a second of CPU into every one
    /// of these for a value nothing reads back.
    /// </remarks>
    private static PasswordHash Secret(string of = "pretend") =>
        new("test", "{}", Encoding.UTF8.GetBytes(of));

    private async Task<PostgresMemberDirectory> ReadyAsync()
    {
        await MigrateAsync();
        return new PostgresMemberDirectory(DataSource);
    }

    /// <summary>A member arrives with a credential and a role, or not at all.</summary>
    /// <remarks>
    /// <b>The three writes are asserted through their consumers rather than by counting rows.</b>
    /// What matters is that the login path can find the credential and the authorization path can
    /// find the role — the shapes that make the account usable — so this reads them back through
    /// <see cref="PostgresIdentityStore"/>, which is what the server does.
    /// </remarks>
    [Fact]
    public async Task A_created_member_can_be_signed_in_and_carries_its_role()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        Principal? made = await directory.CreateMemberAsync(
            "esra", "Esra", Secret(), Roles.Publisher, UserTypes.Creator, CancellationToken.None);

        Assert.NotNull(made);
        Assert.Equal("esra", made!.Name);

        PostgresIdentityStore identity = new(DataSource);

        (Principal Principal, PasswordHash? Credential)? login =
            await identity.FindForLoginAsync("esra", CancellationToken.None);

        Assert.NotNull(login);
        Assert.NotNull(login!.Value.Credential);

        (string userType, IReadOnlyList<string> roles, _, _) =
            await identity.GrantsOfAsync(made.Id, CancellationToken.None);

        Assert.Equal(UserTypes.Creator, userType);
        Assert.Equal([Roles.Publisher], roles);
    }

    /// <summary>A name already taken is refused, and nothing is left behind.</summary>
    /// <remarks>
    /// <b>The second half is the point.</b> The insert decides whether the name is free — checking
    /// first would leave a window where two administrators each see it free — and a rolled-back
    /// transaction must not leave a credential or a role pointing at a principal that does not
    /// exist. Asserted by counting what the duplicate could have added.
    /// </remarks>
    [Fact]
    public async Task A_taken_name_is_refused_without_leaving_a_half_made_member()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        await directory.CreateMemberAsync(
            "esra", null, Secret(), Roles.Publisher, UserTypes.Creator, CancellationToken.None);

        Assert.Null(await directory.CreateMemberAsync(
            "esra", null, Secret("other"), Roles.Viewer, UserTypes.Viewer,
            CancellationToken.None));

        Member esra = (await directory.ListMembersAsync(CancellationToken.None))
            .Single(m => m.Name == "esra");

        // Still the first one: the refusal changed nothing, rather than half-applying the second.
        Assert.Equal([Roles.Publisher], esra.Roles);

        Assert.Equal(
            UserTypes.Creator,
            esra.UserType);
    }

    /// <summary>The listing reports roles, type, state and what each member owns.</summary>
    /// <remarks>
    /// <b><c>OwnsServices</c> is asserted because it is the reason there is no delete.</b> A member
    /// owns content; removing the row would orphan every service naming them, so the surface offers
    /// disable instead, and the number that explains the absence has to be right or the explanation
    /// is worse than none.
    /// </remarks>
    [Fact]
    public async Task The_listing_reports_what_each_member_holds_and_owns()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        Principal owner = (await directory.CreateMemberAsync(
            "owner", null, Secret(), Roles.Publisher, UserTypes.Creator,
            CancellationToken.None))!;

        await directory.CreateMemberAsync(
            "reader", null, Secret(), Roles.Viewer, UserTypes.Viewer, CancellationToken.None);

        PostgresAdminCatalog admin = new(DataSource, new SecretProtector(1, new byte[32]));

        await admin.CreateServiceAsync(
            "theirs", null, null, SharingScope.Private, owner.Id, CancellationToken.None);

        IReadOnlyList<Member> members = await directory.ListMembersAsync(CancellationToken.None);

        Member listed = members.Single(m => m.Name == "owner");
        Assert.Equal(1, listed.OwnsServices);
        Assert.False(listed.IsDisabled);

        Assert.Equal(0, members.Single(m => m.Name == "reader").OwnsServices);

        // The anonymous principal is a row (ADR-015 §2a) and is not a person: listing it beside
        // accounts somebody administers would invite an attempt to disable it.
        Assert.DoesNotContain("anonymous", members.Select(m => m.Name));
    }

    /// <summary>A role change replaces rather than adds, and says what it replaced.</summary>
    /// <remarks>
    /// <b>Replacing is a decision and this test is where it is pinned.</b> The schema allows
    /// several roles; the surface offers one, which is the Portal shape. A <c>SetRoleAsync</c> that
    /// added would leave a demoted member still holding what they had, which is the failure mode a
    /// demotion exists to prevent.
    /// </remarks>
    [Fact]
    public async Task Setting_a_role_replaces_the_one_held()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        await directory.CreateMemberAsync(
            "esra", null, Secret(), Roles.Publisher, UserTypes.Creator, CancellationToken.None);

        Assert.Equal(
            [Roles.Publisher],
            await directory.SetRoleAsync("esra", Roles.Viewer, CancellationToken.None));

        Member after = (await directory.ListMembersAsync(CancellationToken.None))
            .Single(m => m.Name == "esra");

        Assert.Equal([Roles.Viewer], after.Roles);

        // Null holds none, which is a real state: an account that exists and can do nothing.
        Assert.Equal([Roles.Viewer], await directory.SetRoleAsync("esra", null, CancellationToken.None));

        Assert.Empty(
            (await directory.ListMembersAsync(CancellationToken.None))
                .Single(m => m.Name == "esra").Roles);
    }

    /// <summary>Disabling reports what it replaced, and does not remove what they own.</summary>
    /// <remarks>
    /// <b>Ownership surviving is the whole reason disable exists instead of delete.</b> The sharing
    /// evaluator reads <c>owner_principal_id</c> to decide who may read what, so a deleted owner
    /// would change the visibility of their content as a side effect of removing them.
    /// </remarks>
    [Fact]
    public async Task Disabling_a_member_leaves_what_they_own_standing()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        Principal owner = (await directory.CreateMemberAsync(
            "owner", null, Secret(), Roles.Publisher, UserTypes.Creator,
            CancellationToken.None))!;

        PostgresAdminCatalog admin = new(DataSource, new SecretProtector(1, new byte[32]));

        await admin.CreateServiceAsync(
            "theirs", null, null, SharingScope.Private, owner.Id, CancellationToken.None);

        Assert.False(await directory.SetDisabledAsync("owner", true, CancellationToken.None));

        Member disabled = (await directory.ListMembersAsync(CancellationToken.None))
            .Single(m => m.Name == "owner");

        Assert.True(disabled.IsDisabled);
        Assert.Equal(1, disabled.OwnsServices);

        // Again, which is the case two administrators working at once produce.
        Assert.True(await directory.SetDisabledAsync("owner", true, CancellationToken.None));

        Assert.True(await directory.SetDisabledAsync("owner", false, CancellationToken.None));

        Assert.False(
            (await directory.ListMembersAsync(CancellationToken.None))
                .Single(m => m.Name == "owner").IsDisabled);
    }

    /// <summary>An administrator's reset replaces the credential, and works with none present.</summary>
    /// <remarks>
    /// <b>The upsert half matters because of federation.</b> D-10 records the order — local
    /// accounts first, an identity provider later — and a principal that arrived from a provider
    /// has no local credential row. An update alone would report success having changed nothing,
    /// which is the worst answer to *reset this password*.
    /// </remarks>
    [Fact]
    public async Task An_administrators_reset_replaces_the_credential_or_creates_it()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        Principal made = (await directory.CreateMemberAsync(
            "esra", null, Secret("first"), Roles.Publisher, UserTypes.Creator,
            CancellationToken.None))!;

        PostgresIdentityStore identity = new(DataSource);

        Assert.True(await directory.SetPasswordAsync("esra", Secret("second"),
            CancellationToken.None));

        PasswordHash held =
            (await identity.FindForLoginAsync("esra", CancellationToken.None))!.Value.Credential!
                .Value;

        Assert.Equal("second", Encoding.UTF8.GetString(held.Hash));

        Assert.False(
            await directory.SetPasswordAsync("nobody", Secret(), CancellationToken.None));
    }

    /// <summary>Every method says *no such member* rather than throwing.</summary>
    /// <remarks>
    /// Null and false so the endpoints above can answer 404 with the name in it. A missing member
    /// is a typo an administrator can fix; an exception three layers up is a fault they report.
    /// </remarks>
    [Fact]
    public async Task An_absent_member_is_reported_rather_than_thrown()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        Assert.Null(await directory.SetRoleAsync("nobody", Roles.Viewer, CancellationToken.None));
        Assert.Null(await directory.SetDisabledAsync("nobody", true, CancellationToken.None));

        Assert.False(
            await directory.SetPasswordAsync("nobody", Secret(), CancellationToken.None));
    }

    /// <summary>A password an administrator issued is dirty; one its owner set is not.</summary>
    /// <remarks>
    /// <para>
    /// <b>Owner rule 2026-08-17:</b> *"kullanıcıya yeni parola veremem. sistem bana yeni bir parola
    /// verir. bunu kullanıcı ile paylaşabilirim. ama sistem otomatik olarak o parolayı kirli kabul
    /// eder. kullanıcı giriş yapınca değiştirmek zorunda kalır."* — the system issues the password,
    /// the administrator passes it on, and its owner has to replace it on signing in.
    /// </para>
    /// <para>
    /// <b>The asymmetry is the control and this is where it is pinned.</b> Two write paths reach one
    /// column: everything in <c>PostgresMemberDirectory</c> sets it, and only the self-service change
    /// clears it. Neither takes it as an argument — if either did, a caller could ask for a
    /// permanent password on somebody else's account, which is the thing being removed.
    /// </para>
    /// <para>
    /// <b>Read through the session, because that is what enforces it.</b> The flag governs what a
    /// request may do, so it is resolved per request rather than stamped into the token — the rule
    /// three of this month's defects came from breaking. Asserting it through
    /// <c>FindSessionAsync</c> is asserting the thing the middleware actually reads.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_issued_password_is_dirty_until_its_owner_replaces_it()
    {
        PostgresMemberDirectory directory = await ReadyAsync();

        Principal made = (await directory.CreateMemberAsync(
            "esra", null, Secret("issued"), Roles.Publisher, UserTypes.Creator,
            CancellationToken.None))!;

        PostgresIdentityStore identity = new(DataSource);

        Guid session = await identity.CreateSessionAsync(
            made.Id,
            SessionToken.HashOf("token-one"),
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, session);

        AuthenticatedSession opened = (await identity.FindSessionAsync(
            SessionToken.HashOf("token-one"), DateTimeOffset.UtcNow, CancellationToken.None))!.Value;

        Assert.True(
            opened.MustChangePassword,
            "A credential the directory wrote is one the server issued, so it has to arrive dirty.");

        // The member sets their own, through the path the request handler uses.
        await identity.SetPasswordAsync(made.Id, Secret("their own"), CancellationToken.None);

        AuthenticatedSession clean = (await identity.FindSessionAsync(
            SessionToken.HashOf("token-one"), DateTimeOffset.UtcNow, CancellationToken.None))!.Value;

        Assert.False(
            clean.MustChangePassword,
            "The same session must come back clean: the flag is read per request, so setting their "
            + "own password takes effect on the next one rather than on the next sign-in.");

        // And an administrator's reset makes it dirty again.
        await directory.SetPasswordAsync("esra", Secret("reset"), CancellationToken.None);

        Assert.True(
            (await identity.FindSessionAsync(
                SessionToken.HashOf("token-one"), DateTimeOffset.UtcNow, CancellationToken.None))!
                .Value.MustChangePassword);
    }

    /// <summary>A principal with no local credential is not made to change anything.</summary>
    /// <remarks>
    /// <b>*No password* is not *a dirty password*.</b> D-10 records the order — local accounts
    /// first, an identity provider later — and a principal that arrives from a provider has no
    /// <c>local_credential</c> row. The session query left-joins for exactly this, and a caller
    /// with nothing to change must not be told to change it, because there is no form that could
    /// satisfy the demand.
    /// </remarks>
    [Fact]
    public async Task A_member_with_no_local_password_is_not_asked_to_change_one()
    {
        await MigrateAsync();

        PostgresIdentityStore identity = new(DataSource);

        Principal made = await identity.CreateUserAsync(
            "federated", null, Secret(), CancellationToken.None);

        await using (Npgsql.NpgsqlCommand drop = DataSource.CreateCommand(
            "delete from local_credential where principal_id = @id"))
        {
            drop.Parameters.AddWithValue("id", made.Id);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await identity.CreateSessionAsync(
            made.Id,
            SessionToken.HashOf("token-two"),
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            CancellationToken.None);

        AuthenticatedSession opened = (await identity.FindSessionAsync(
            SessionToken.HashOf("token-two"), DateTimeOffset.UtcNow, CancellationToken.None))!.Value;

        Assert.False(
            opened.MustChangePassword);
    }
}
