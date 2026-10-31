using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// A service account's OAuth client secret is verified under the throttle a password is — ADR-200 condition 2,
/// <c>client_credentials</c>.
/// </summary>
public sealed class ClientSecretVerificationTests
{
    private static readonly IPAddress Address = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress OtherAddress = IPAddress.Parse("198.51.100.9");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryIdentityStore _store;
    private readonly Secrets _keys = new();

    public ClientSecretVerificationTests() => _store = new InMemoryIdentityStore(_time);

    private LoginService Service(LoginThrottle? throttle = null) =>
        new(_store, new FakePasswordHasher(), throttle ?? LoginThrottle.Default, TimeSpan.FromHours(12), _time);

    private Task<(LoginFailure Failure, Principal? Principal, Guid Secret, DateTimeOffset? ExpiresAt)> Verify(
        string clientId, string secret, IPAddress? address = null, LoginThrottle? throttle = null) =>
        Service(throttle).VerifyClientSecretAsync(clientId, secret, address ?? Address, _keys, CancellationToken.None);

    [Fact]
    public async Task The_right_secret_names_its_account_and_when_it_expires()
    {
        string secret = ClientSecretToken.Generate();
        DateTimeOffset expires = _time.GetUtcNow().AddDays(3);
        Guid id = _keys.Add("machine", secret, expires);

        var result = await Verify("machine", secret);

        Assert.Equal(LoginFailure.None, result.Failure);
        Assert.Equal("machine", result.Principal!.Name);
        Assert.Equal(PrincipalKind.Service, result.Principal.Kind);
        Assert.Equal(id, result.Secret);
        Assert.Equal(expires, result.ExpiresAt);
    }

    [Fact]
    public async Task Another_accounts_secret_is_the_same_refusal_as_a_wrong_one()
    {
        string secret = ClientSecretToken.Generate();
        _keys.Add("machine", secret, null);

        Assert.Equal(LoginFailure.InvalidCredentials, (await Verify("other", secret)).Failure);
        Assert.Equal(LoginFailure.InvalidCredentials, (await Verify("machine", ClientSecretToken.Generate())).Failure);
    }

    /// <summary>A key is a token and never a client secret: its prefix is not even looked up.</summary>
    [Fact]
    public async Task A_key_is_not_a_client_secret()
    {
        string key = ApiKeyToken.Generate();
        _keys.Add("machine", key, null);

        Assert.Equal(LoginFailure.InvalidCredentials, (await Verify("machine", key)).Failure);
        Assert.Equal(0, _keys.Lookups);
        Assert.False(ApiKeyToken.IsOne(ClientSecretToken.Generate()));
    }

    [Fact]
    public async Task Failures_against_one_account_are_throttled_after_its_budget()
    {
        string secret = ClientSecretToken.Generate();
        _keys.Add("machine", secret, null);
        LoginThrottle throttle = new(TimeSpan.FromMinutes(15), perAccount: 3, perAddress: 100);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(LoginFailure.InvalidCredentials, (await Verify("machine", "gcs_wrong", OtherAddress, throttle)).Failure);
        }

        Assert.Equal(LoginFailure.AccountThrottled, (await Verify("machine", "gcs_wrong", OtherAddress, throttle)).Failure);

        // The holder of the real secret is not locked out by somebody else's guesses.
        Assert.Equal(LoginFailure.None, (await Verify("machine", secret, Address, throttle)).Failure);
    }

    [Fact]
    public async Task An_address_over_its_budget_is_refused_before_anything_is_looked_up()
    {
        string secret = ClientSecretToken.Generate();
        _keys.Add("machine", secret, null);
        LoginThrottle throttle = new(TimeSpan.FromMinutes(15), perAccount: 3, perAddress: 5);

        for (int i = 0; i < 5; i++)
        {
            await Verify($"guess{i}", "gcs_wrong", Address, throttle);
        }

        int before = _keys.Lookups;
        Assert.Equal(LoginFailure.AddressThrottled, (await Verify("machine", secret, Address, throttle)).Failure);
        Assert.Equal(before, _keys.Lookups);
    }

    /// <summary>The secrets half of a key store, in memory; everything else is not this test's.</summary>
    private sealed class Secrets : IApiKeyStore
    {
        private readonly List<(Guid Id, string Account, byte[] Hash, DateTimeOffset? Expires)> _rows = [];

        public int Lookups { get; private set; }

        public Guid Add(string account, string text, DateTimeOffset? expires)
        {
            Guid id = Guid.NewGuid();
            _rows.Add((id, account, SessionToken.HashOf(text), expires));
            return id;
        }

        public Task<(Guid Secret, Principal Principal, DateTimeOffset? ExpiresAt)?> FindSecretAsync(
            string account, byte[] hash, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Lookups++;
            var row = _rows.FirstOrDefault(r => r.Account == account && r.Hash.SequenceEqual(hash) && (r.Expires is null || r.Expires > now));
            return Task.FromResult<(Guid, Principal, DateTimeOffset?)?>(row.Account is null
                ? null
                : (row.Id, new Principal(Guid.NewGuid(), PrincipalKind.Service, account, null, isDisabled: false), row.Expires));
        }

        public Task LinkSessionAsync(Guid session, Guid secret, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ServiceAccount?> CreateServiceAccountAsync(string name, string? description, string userType, IReadOnlyList<string> roles, Guid createdBy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceAccount>> ListServiceAccountsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteServiceAccountAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetDisabledAsync(string name, bool disabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiKeyInfo?> IssueAsync(string account, string keyName, byte[] hash, DateTimeOffset? expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApiKeyInfo>?> KeysAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(string account, Guid key, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(Guid Key, Principal Principal, DateTimeOffset? ExpiresAt)?> FindAsync(byte[] hash, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TouchAsync(Guid key, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiKeyInfo?> IssueSecretAsync(string account, string secretName, byte[] hash, DateTimeOffset? expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ApiKeyInfo>?> SecretsAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RevokeSecretAsync(string account, Guid secret, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
