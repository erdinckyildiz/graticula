using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Tools;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Graticula.Host.Tests.Tools;

/// <summary>
/// The recovery command, against fakes that refuse everything it should not touch.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-137](../../../docs/open-questions.md), owner decision 2026-08-25.</b> A store with
/// accounts and no administrator ([D-14](../../../docs/architecture-debt.md)) is recovered by
/// a command rather than by re-arming setup. The command's whole value is that it is narrow,
/// so these tests are about what it refuses at least as much as about what it does.
/// </para>
/// <para>
/// <b>Both fakes throw on every member the command has no business calling</b>, which makes
/// the surface itself an assertion: a future edit that reaches for <c>ListMembersAsync</c>,
/// or for a session, fails here rather than passing quietly.
/// </para>
/// <para>
/// <b>The four refusals and the success path were also run against a real store</b> on
/// 2026-08-25 — a throwaway schema at migration 36 holding two principals and no
/// administrator. That is what these fakes stand in for; it is not what they prove, and the
/// distinction is why that run is recorded in the ADR rather than only here.
/// </para>
/// </remarks>
public sealed class AdminCreatorTests
{
    // 32 zero bytes, base64: valid AES-256 and obviously not a real key.
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    // Three unrelated words, which is what the refusal message recommends.
    private const string Good = "dokuz kirmizi degirmen";

    [Fact]
    public async Task A_password_is_required_and_the_variable_is_named()
    {
        Directory members = new();
        (int code, string said) = await RunAsync(new Store(), members, password: null);

        Assert.Equal(2, code);
        Assert.Contains("GRATICULA_ADMIN_PASSWORD", said, StringComparison.Ordinal);
        Assert.Empty(members.Calls);
    }

    [Fact]
    public async Task A_short_password_is_refused_before_anything_is_written()
    {
        // <b>The floor is the server's, not this command's.</b> ADR-015 §6a set it at 8 and
        // said why; a recovery path that asked for more than the login it recovers would be
        // a second number going stale on its own.
        Directory members = new();
        (int code, string said) = await RunAsync(new Store(), members, "kisa");

        Assert.Equal(2, code);
        Assert.Contains(
            $"at least {AuthEndpoints.MinimumPasswordLength} characters", said,
            StringComparison.Ordinal);

        Assert.Empty(members.Calls);
    }

    [Fact]
    public async Task A_password_at_the_floor_is_accepted()
    {
        // <b>The other half of the same claim.</b> Refusing something short proves a floor
        // exists; this proves it is the server's floor and not a stricter one nobody agreed.
        string atTheFloor = new('k', AuthEndpoints.MinimumPasswordLength);
        Directory members = new();
        (int code, _) = await RunAsync(new Store(), members, atTheFloor);

        Assert.Equal(0, code);
        Assert.Equal(["create"], members.Calls);
    }

    [Fact]
    public async Task A_common_password_is_refused_at_recovery_too()
    {
        // <b>The same list the ordinary password change uses.</b> A recovery at three in the
        // morning is exactly when somebody types the password they always type.
        Directory members = new();
        (int code, _) = await RunAsync(new Store(), members, "Password1234");

        Assert.Equal(2, code);
        Assert.Empty(members.Calls);
    }

    [Fact]
    public async Task A_store_that_already_has_an_administrator_is_refused()
    {
        // <b>The security property of the whole command.</b> One that also works on a healthy
        // store is a way to mint an administrator, and its user having been able to do it in
        // SQL anyway is not a reason to make it one call.
        Directory members = new();
        (int code, string said) = await RunAsync(
            new Store { HasAdministrator = true }, members, Good);

        Assert.Equal(3, code);
        Assert.Contains("already has an administrator", said, StringComparison.Ordinal);
        Assert.Empty(members.Calls);
    }

    [Fact]
    public async Task It_creates_the_account_and_grants_the_role()
    {
        Directory members = new();
        (int code, _) = await RunAsync(new Store(), members, Good);

        Assert.Equal(0, code);
        Assert.Equal("admin", members.CreatedName);
        Assert.Equal(Roles.Administrator, members.CreatedRole);
        Assert.Equal(UserTypes.Unrestricted, members.CreatedType);
        Assert.Equal(["create"], members.Calls);
    }

    [Fact]
    public async Task The_name_can_be_given()
    {
        Directory members = new();
        (int code, _) = await RunAsync(new Store(), members, Good, "--name", "kurtarma");

        Assert.Equal(0, code);
        Assert.Equal("kurtarma", members.CreatedName);
    }

    [Fact]
    public async Task A_name_that_exists_is_repaired_rather_than_refused()
    {
        // <b>A partial restore leaves the account and loses the grant.</b> Refusing it would
        // send somebody to SQL for the case this exists to take away from them.
        Directory members = new() { NameTaken = true };
        (int code, string said) = await RunAsync(new Store(), members, Good);

        Assert.Equal(0, code);
        Assert.Equal(["create", "password", "role:" + Roles.Administrator], members.Calls);
        Assert.Contains("already existed", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grant_that_does_not_take_is_reported_rather_than_swallowed()
    {
        // <b>Password set, role not.</b> The account can sign in and can do nothing, which
        // looks like success from the shell unless the command says otherwise.
        Directory members = new() { NameTaken = true, GrantFails = true };
        (int code, string said) = await RunAsync(new Store(), members, Good);

        Assert.Equal(5, code);
        Assert.Contains("can do nothing", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_success_message_names_the_route_that_answers()
    {
        // <b>Measured, not assumed.</b> The first draft sent the operator to the members
        // screen; the account it creates carries a must-change credential, so that screen is
        // one of the things that answers 403 until the password is replaced. Signing in as
        // the recovered administrator against a real store is how that was found.
        (int _, string created) = await RunAsync(new Store(), new Directory(), Good);
        (int _, string repaired) = await RunAsync(
            new Store(), new Directory { NameTaken = true }, Good);

        foreach (string said in new[] { created, repaired })
        {
            Assert.Contains("/rest/auth/password", said, StringComparison.Ordinal);
            Assert.DoesNotContain("members screen", said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_password_never_reaches_the_output()
    {
        // <b>Every path, including the failures.</b> A recovery password may have been typed
        // as an argument, and a command that echoes it back has put it in the terminal
        // scrollback of a machine somebody else can read.
        string[] said =
        [
            (await RunAsync(new Store(), new Directory(), Good)).Output,
            (await RunAsync(new Store { HasAdministrator = true }, new Directory(), Good)).Output,
            (await RunAsync(new Store(), new Directory { NameTaken = true }, Good)).Output,
            (await RunAsync(
                new Store(), new Directory { NameTaken = true, GrantFails = true }, Good)).Output,
            (await RunAsync(new Store(), new Directory(), null, "--password", Good)).Output,
        ];

        foreach (string one in said)
        {
            Assert.False(string.IsNullOrWhiteSpace(one), "the command said nothing at all");
            Assert.DoesNotContain(Good, one, StringComparison.Ordinal);
        }
    }

    /// <summary>Runs the command with the console captured and the variable set.</summary>
    /// <remarks>
    /// <b>Console and environment are process-global</b>, and xunit runs the methods of one
    /// class in sequence, so this is safe within the class. Another class writing to the
    /// console concurrently would land text in this buffer; every assertion here is
    /// <c>Contains</c>, or is about a string no other test knows.
    /// </remarks>
    private static async Task<(int Code, string Output)> RunAsync(
        IIdentityStore store, IMemberDirectory members, string? password, params string[] extra)
    {
        ServiceCollection services = new();

        services.AddSingleton(HostSettings.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graticula:PlatformStore"] = "Host=localhost;Database=gis",
                ["Graticula:SecretKey"] = Key,
            })
            .Build()));

        services.AddSingleton(store);
        services.AddSingleton(members);
        services.AddSingleton<IPasswordHasher>(new Hasher());

        StringWriter captured = new();
        TextWriter wasOut = Console.Out;
        TextWriter wasError = Console.Error;
        string? wasVariable = Environment.GetEnvironmentVariable("GRATICULA_ADMIN_PASSWORD");

        try
        {
            Console.SetOut(captured);
            Console.SetError(captured);
            Environment.SetEnvironmentVariable("GRATICULA_ADMIN_PASSWORD", password);

            int code = await AdminCreator.RunAsync(
                services.BuildServiceProvider(),
                ["tools", "admincreator", .. extra],
                CancellationToken.None)
                .ConfigureAwait(false);

            return (code, captured.ToString());
        }
        finally
        {
            Console.SetOut(wasOut);
            Console.SetError(wasError);
            Environment.SetEnvironmentVariable("GRATICULA_ADMIN_PASSWORD", wasVariable);
        }
    }

    private static NotSupportedException Not() =>
        new("The recovery command has no business calling this.");

    /// <summary>An identity store that answers one question and refuses the rest.</summary>
    private sealed class Store : IIdentityStore
    {
        public bool HasAdministrator { get; init; }

        public Task<bool> AnyPrincipalHoldingAsync(
            string role, CancellationToken cancellationToken) =>
            Task.FromResult(HasAdministrator
                && string.Equals(role, Roles.Administrator, StringComparison.Ordinal));

        public Task<AuthenticatedSession?> FindSessionAsync(
            byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken) => throw Not();

        public Task<(Principal Principal, PasswordHash? Credential)?> FindForLoginAsync(
            string name, CancellationToken cancellationToken) => throw Not();

        public Task<FailureCounts> CountRecentFailuresAsync(
            string name, IPAddress? address, DateTimeOffset since,
            CancellationToken cancellationToken) => throw Not();

        public Task RecordAttemptAsync(
            string name, IPAddress? address, bool succeeded,
            CancellationToken cancellationToken) => throw Not();

        public Task<Guid> CreateSessionAsync(
            Guid principalId, byte[] tokenHash, DateTimeOffset expiresAt, IPAddress? address,
            CancellationToken cancellationToken) => throw Not();

        public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw Not();

        public Task<int> RevokeOtherSessionsAsync(
            Guid principalId, Guid? keep, CancellationToken cancellationToken) => throw Not();

        public Task SetPasswordAsync(
            Guid principalId, PasswordHash hash, CancellationToken cancellationToken) => throw Not();

        public Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken) => throw Not();

        public Task<Principal> CreateUserAsync(
            string name, string? displayName, PasswordHash password,
            CancellationToken cancellationToken) => throw Not();

        public Task<IReadOnlyList<string>> RolesOfAsync(
            Guid principalId, CancellationToken cancellationToken) => throw Not();

        public Task<(string UserType, IReadOnlyList<string> Roles, IReadOnlyList<Guid> Groups)>
            GrantsOfAsync(Guid principalId, CancellationToken cancellationToken) => throw Not();

        public Task GrantRoleAsync(
            Guid principalId, string role, Guid? grantedBy,
            CancellationToken cancellationToken) => throw Not();

        public Task RevokeRoleAsync(
            Guid principalId, string role, CancellationToken cancellationToken) => throw Not();
    }

    /// <summary>A member directory that records the three calls the command may make.</summary>
    private sealed class Directory : IMemberDirectory
    {
        public List<string> Calls { get; } = [];

        public bool NameTaken { get; init; }

        public bool GrantFails { get; init; }

        public string? CreatedName { get; private set; }

        public string? CreatedRole { get; private set; }

        public string? CreatedType { get; private set; }

        public Task<Principal?> CreateMemberAsync(
            string name, string? displayName, PasswordHash password, string role, string userType,
            CancellationToken cancellationToken)
        {
            Calls.Add("create");
            CreatedName = name;
            CreatedRole = role;
            CreatedType = userType;

            return Task.FromResult<Principal?>(NameTaken
                ? null
                : new Principal(Guid.NewGuid(), PrincipalKind.User, name, displayName, false));
        }

        public Task<bool> SetPasswordAsync(
            string name, PasswordHash password, CancellationToken cancellationToken)
        {
            Calls.Add("password");
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>?> SetRoleAsync(
            string name, string? role, CancellationToken cancellationToken)
        {
            Calls.Add("role:" + role);
            return Task.FromResult<IReadOnlyList<string>?>(GrantFails ? null : [role!]);
        }

        public Task<IReadOnlyList<Member>> ListMembersAsync(CancellationToken cancellationToken) =>
            throw Not();

        public Task<bool?> SetDisabledAsync(
            string name, bool disabled, CancellationToken cancellationToken) => throw Not();

        public Task<MemberHoldings?> HoldingsOfAsync(
            string name, CancellationToken cancellationToken) => throw Not();

        public Task<MemberRemoval> TransferAndRemoveAsync(
            string name, string transferTo, CancellationToken cancellationToken) => throw Not();

        public Task<MemberRemoval> RemoveAsync(string name, CancellationToken cancellationToken) =>
            throw Not();

        public Task<int> TransferOwnershipAsync(
            string current, string receiver, CancellationToken cancellationToken) => throw Not();
    }

    /// <summary>A hasher with a constant answer, so the password cannot leak through it.</summary>
    private sealed class Hasher : IPasswordHasher
    {
        public PasswordHash Hash(string password) => new("test", "{}", [1, 2, 3]);

        public bool Verify(string password, PasswordHash stored) => throw Not();

        public bool NeedsRehash(PasswordHash stored) => throw Not();
    }
}
