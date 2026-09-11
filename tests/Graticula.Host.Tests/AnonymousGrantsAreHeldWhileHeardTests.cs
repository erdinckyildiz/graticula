using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// An anonymous caller's grants come from memory only while the store's announcements are heard.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-249](../../docs/architecture-debt.md), by owner decision 2026-09-11</b> — a hold that
/// hears about changes at once. What these pin is the half that makes the hold safe rather than
/// fast: an answer is served only while <see cref="AnonymousGrants.Listening"/> is true, a change
/// clears it, a read that an announcement overtakes is not kept, and a signed-in caller is never
/// answered from it.
/// </para>
/// <para>
/// <b>Counted at the store, because that is the cost.</b> Each test asserts how many times
/// <c>GrantsOfAsync</c> was asked, which is the round trip the pipeline-ceiling measurement named.
/// </para>
/// </remarks>
public sealed class AnonymousGrantsAreHeldWhileHeardTests
{
    [Fact]
    public async Task While_nothing_listens_every_anonymous_request_reads_the_store()
    {
        Store store = new();
        AnonymousGrants cache = new();
        Authentication authentication = new(store, TimeProvider.System, anonymous: cache);

        await authentication.ResolveAsync(Context(), CancellationToken.None);
        await authentication.ResolveAsync(Context(), CancellationToken.None);

        // The state before the subscription is up, after it drops, and on a server whose listener
        // never connected: the behaviour every build before the cache had.
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task While_the_store_is_heard_one_read_answers_every_anonymous_request()
    {
        Store store = new();
        AnonymousGrants cache = new();
        cache.Subscribed(true);
        Authentication authentication = new(store, TimeProvider.System, anonymous: cache);

        for (int i = 0; i < 5; i++)
        {
            RequestPrincipal resolved =
                await authentication.ResolveAsync(Context(), CancellationToken.None);

            Assert.True(resolved.Principal.IsAnonymous);
        }

        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task An_announcement_sends_the_next_request_back_to_the_store()
    {
        Store store = new();
        AnonymousGrants cache = new();
        cache.Subscribed(true);
        Authentication authentication = new(store, TimeProvider.System, anonymous: cache);

        await authentication.ResolveAsync(Context(), CancellationToken.None);

        // An operator grants anonymous a role by hand; migration 44's trigger announces it.
        store.Roles = [Roles.Viewer];
        cache.Changed();

        await authentication.ResolveAsync(Context(), CancellationToken.None);
        await authentication.ResolveAsync(Context(), CancellationToken.None);

        Assert.Equal(2, store.Reads);
        Assert.True(cache.TryGet(out AnonymousGrants.Snapshot? held));
        Assert.Equal([Roles.Viewer], held!.Roles);
    }

    [Fact]
    public async Task A_read_that_an_announcement_overtakes_is_not_kept()
    {
        // <b>The race the generation exists for.</b> The read starts, the change commits and is
        // announced while the read is still in flight, and the read then returns the old answer.
        // Kept, it would be served until the next change — which may be never.
        Store store = new();
        AnonymousGrants cache = new();
        cache.Subscribed(true);
        store.DuringRead = cache.Changed;
        Authentication authentication = new(store, TimeProvider.System, anonymous: cache);

        await authentication.ResolveAsync(Context(), CancellationToken.None);

        Assert.False(cache.TryGet(out _));

        store.DuringRead = null;
        await authentication.ResolveAsync(Context(), CancellationToken.None);
        await authentication.ResolveAsync(Context(), CancellationToken.None);

        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task Losing_the_subscription_stops_the_held_answer_at_once()
    {
        Store store = new();
        AnonymousGrants cache = new();
        cache.Subscribed(true);
        Authentication authentication = new(store, TimeProvider.System, anonymous: cache);

        await authentication.ResolveAsync(Context(), CancellationToken.None);

        cache.Subscribed(false);

        await authentication.ResolveAsync(Context(), CancellationToken.None);
        await authentication.ResolveAsync(Context(), CancellationToken.None);

        Assert.Equal(3, store.Reads);
    }

    [Fact]
    public async Task A_signed_in_caller_is_never_answered_from_it()
    {
        Store store = new();
        AnonymousGrants cache = new();
        cache.Subscribed(true);
        Authentication authentication = new(store, TimeProvider.System, anonymous: cache);

        for (int i = 0; i < 3; i++)
        {
            DefaultHttpContext context = Context();
            context.Request.Headers.Authorization = "Bearer a-token";

            RequestPrincipal resolved =
                await authentication.ResolveAsync(context, CancellationToken.None);

            Assert.False(resolved.Principal.IsAnonymous);
        }

        Assert.Equal(3, store.Reads);
        Assert.False(cache.TryGet(out _));
    }

    /// <summary>A request with the one service resolving a principal reads: the settings.</summary>
    private static DefaultHttpContext Context()
    {
        Dictionary<string, string?> values = new()
        {
            ["Graticula:PlatformStore"] = "Host=localhost;Database=gis",

            // 32 zero bytes, base64: valid AES-256 and obviously not a real key.
            ["Graticula:SecretKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        };

        ServiceCollection services = new();
        services.AddSingleton(HostSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()));

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    /// <summary>An identity store that counts grant lookups and knows one signed-in user.</summary>
    private sealed class Store : IIdentityStore
    {
        private static readonly Principal Ada =
            new(Guid.NewGuid(), PrincipalKind.User, "ada", "Ada", false);

        public int Reads { get; private set; }

        public IReadOnlyList<string> Roles { get; set; } = [];

        public Action? DuringRead { get; set; }

        public Task<(string UserType, IReadOnlyList<string> Roles, IReadOnlyList<Guid> Groups,
                     IReadOnlyList<Guid> EditableGroups)>
            GrantsOfAsync(Guid principalId, CancellationToken cancellationToken)
        {
            Reads++;
            DuringRead?.Invoke();

            return Task.FromResult((
                UserTypes.Unrestricted,
                principalId == Principal.AnonymousId ? Roles : (IReadOnlyList<string>)[Platform.Identity.Roles.Viewer],
                (IReadOnlyList<Guid>)[],
                (IReadOnlyList<Guid>)[]));
        }

        public Task<AuthenticatedSession?> FindSessionAsync(
            byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<AuthenticatedSession?>(
                new AuthenticatedSession(Guid.NewGuid(), Ada, now.AddHours(1)));

        public Task<bool> AnyPrincipalHoldingAsync(
            string role, CancellationToken cancellationToken) => throw Not();

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

        public Task GrantRoleAsync(
            Guid principalId, string role, Guid? grantedBy,
            CancellationToken cancellationToken) => throw Not();

        public Task RevokeRoleAsync(
            Guid principalId, string role, CancellationToken cancellationToken) => throw Not();

        private static NotSupportedException Not() => new("Not part of resolving a principal.");
    }
}
