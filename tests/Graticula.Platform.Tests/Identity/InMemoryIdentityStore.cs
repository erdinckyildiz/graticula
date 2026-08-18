using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// An <see cref="IIdentityStore"/> in a dictionary.
/// </summary>
/// <remarks>
/// Exists so the login sequence — which is a security property, not a query —
/// can be tested exhaustively without a container. The PostgreSQL implementation
/// is tested separately against a real database; what is checked here is the
/// ordering of steps, which no amount of SQL correctness would establish.
/// </remarks>
internal sealed class InMemoryIdentityStore : IIdentityStore
{
    private readonly Dictionary<string, (Principal Principal, PasswordHash? Credential)> _principals =
        new(StringComparer.Ordinal);

    private readonly List<(string Name, IPAddress? Address, DateTimeOffset At, bool Succeeded)> _attempts = [];
    private readonly Dictionary<byte[], (Guid Id, Guid PrincipalId, DateTimeOffset Expires)> _sessions =
        new(ByteArrayComparer.Instance);

    private readonly HashSet<Guid> _revoked = [];
    private readonly Dictionary<Guid, HashSet<string>> _roles = [];

    public InMemoryIdentityStore(TimeProvider time) => Time = time;

    /// <summary>The clock, shared with the service under test.</summary>
    public TimeProvider Time { get; }

    /// <summary>Passwords written by <see cref="SetPasswordAsync"/>, for rehash assertions.</summary>
    public List<PasswordHash> PasswordsWritten { get; } = [];

    /// <summary>Adds a principal with a password.</summary>
    public void Add(Principal principal, PasswordHash? credential) =>
        _principals[principal.Name] = (principal, credential);

    /// <summary>Records failures directly, to set up a throttle state.</summary>
    public void AddFailures(string name, IPAddress? address, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _attempts.Add((name, address, Time.GetUtcNow(), false));
        }
    }

    /// <summary>Every attempt recorded, in order.</summary>
    public IReadOnlyList<(string Name, IPAddress? Address, DateTimeOffset At, bool Succeeded)> Attempts
        => _attempts;

    public Task<AuthenticatedSession?> FindSessionAsync(
        byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(tokenHash, out (Guid Id, Guid PrincipalId, DateTimeOffset Expires) session)
            || _revoked.Contains(session.Id)
            || session.Expires <= now)
        {
            return Task.FromResult<AuthenticatedSession?>(null);
        }

        Principal principal = _principals.Values.First(p => p.Principal.Id == session.PrincipalId).Principal;

        return Task.FromResult<AuthenticatedSession?>(
            principal.IsDisabled ? null : new AuthenticatedSession(session.Id, principal, session.Expires));
    }

    public Task<(Principal Principal, PasswordHash? Credential)?> FindForLoginAsync(
        string name, CancellationToken cancellationToken) =>
        Task.FromResult<(Principal Principal, PasswordHash? Credential)?>(
            _principals.TryGetValue(name, out (Principal Principal, PasswordHash? Credential) found)
                ? found
                : null);

    public Task<FailureCounts> CountRecentFailuresAsync(
        string name, IPAddress? address, DateTimeOffset since, CancellationToken cancellationToken)
    {
        List<(string Name, IPAddress? Address, DateTimeOffset At, bool Succeeded)> window =
            _attempts.Where(a => !a.Succeeded && a.At >= since).ToList();

        return Task.FromResult(new FailureCounts(
            window.Count(a => string.Equals(a.Name, name, StringComparison.Ordinal)),
            address is null ? 0 : window.Count(a => Equals(a.Address, address))));
    }

    public Task RecordAttemptAsync(
        string name, IPAddress? address, bool succeeded, CancellationToken cancellationToken)
    {
        _attempts.Add((name, address, Time.GetUtcNow(), succeeded));
        return Task.CompletedTask;
    }

    public Task<Guid> CreateSessionAsync(
        Guid principalId,
        byte[] tokenHash,
        DateTimeOffset expiresAt,
        IPAddress? address,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        _sessions[tokenHash] = (id, principalId, expiresAt);
        return Task.FromResult(id);
    }

    public Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        _revoked.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task SetPasswordAsync(Guid principalId, PasswordHash hash, CancellationToken cancellationToken)
    {
        PasswordsWritten.Add(hash);

        string name = _principals.First(p => p.Value.Principal.Id == principalId).Key;
        _principals[name] = (_principals[name].Principal, hash);

        return Task.CompletedTask;
    }

    public Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_principals.Values.Any(p => p.Principal.Kind == PrincipalKind.User));

    public Task<Principal> CreateUserAsync(
        string name, string? displayName, PasswordHash password, CancellationToken cancellationToken)
    {
        Principal principal = new(Guid.NewGuid(), PrincipalKind.User, name, displayName, isDisabled: false);
        Add(principal, password);
        return Task.FromResult(principal);
    }

    public Task<int> RevokeOtherSessionsAsync(
        Guid principalId, Guid? keep, CancellationToken cancellationToken)
    {
        int revoked = 0;

        foreach ((Guid id, Guid owner, DateTimeOffset _) in _sessions.Values)
        {
            if (owner == principalId && id != keep && _revoked.Add(id))
            {
                revoked++;
            }
        }

        return Task.FromResult(revoked);
    }

    public Task<IReadOnlyList<string>> RolesOfAsync(
        Guid principalId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
            _roles.TryGetValue(principalId, out HashSet<string>? granted)
                ? [.. granted.Order(StringComparer.Ordinal)]
                : []);

    public Task GrantRoleAsync(
        Guid principalId, string role, Guid? grantedBy, CancellationToken cancellationToken)
    {
        if (!_roles.TryGetValue(principalId, out HashSet<string>? granted))
        {
            granted = new HashSet<string>(StringComparer.Ordinal);
            _roles[principalId] = granted;
        }

        granted.Add(role);
        return Task.CompletedTask;
    }

    public Task RevokeRoleAsync(Guid principalId, string role, CancellationToken cancellationToken)
    {
        if (_roles.TryGetValue(principalId, out HashSet<string>? granted))
        {
            granted.Remove(role);
        }

        return Task.CompletedTask;
    }

    /// <summary>User types by principal. Defaults to no ceiling.</summary>
    public Dictionary<Guid, string> UserTypes { get; } = [];

    /// <summary>Which groups a principal is in, for the tests that care — ADR-036.</summary>
    public Dictionary<Guid, HashSet<Guid>> GroupsOf { get; } = [];

    public Task<(string UserType, IReadOnlyList<string> Roles, IReadOnlyList<Guid> Groups)>
        GrantsOfAsync(Guid principalId, CancellationToken cancellationToken) =>
        Task.FromResult((
            UserTypes.TryGetValue(principalId, out string? t)
                ? t
                : Graticula.Platform.Identity.UserTypes.Unrestricted,
            (IReadOnlyList<string>)(_roles.TryGetValue(principalId, out HashSet<string>? g)
                ? [.. g.Order(StringComparer.Ordinal)]
                : []),
            (IReadOnlyList<Guid>)(GroupsOf.TryGetValue(principalId, out HashSet<Guid>? inGroups)
                ? [.. inGroups]
                : [])));

    public Task<bool> AnyPrincipalHoldingAsync(string role, CancellationToken cancellationToken) =>
        Task.FromResult(_roles.Values.Any(r => r.Contains(role)));

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => BitConverter.ToInt32(obj, 0);
    }
}

/// <summary>
/// A password hasher that is not a password hasher.
/// </summary>
/// <remarks>
/// Reverses the password and calls it a hash. <b>Deliberately trivial</b>: these
/// tests are about the login sequence, and using real Argon2id would make every
/// case cost tens of milliseconds while testing nothing extra about the ordering.
/// Argon2id itself is tested where it lives.
/// </remarks>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    /// <summary>Parameters this hasher considers current. Change it to force a rehash.</summary>
    public string CurrentParameters { get; set; } = "v1";

    /// <summary>How many times <see cref="Verify"/> was called, for timing assertions.</summary>
    public int VerifyCalls { get; private set; }

    public PasswordHash Hash(string password) =>
        new("fake", CurrentParameters, System.Text.Encoding.UTF8.GetBytes(Reverse(password)));

    public bool Verify(string password, PasswordHash stored)
    {
        VerifyCalls++;
        return System.Text.Encoding.UTF8.GetString(stored.Hash) == Reverse(password);
    }

    public bool NeedsRehash(PasswordHash stored) =>
        !string.Equals(stored.Parameters, CurrentParameters, StringComparison.Ordinal);

    private static string Reverse(string value)
    {
        char[] characters = value.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }
}
