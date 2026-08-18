using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// What each role grants, read from <c>role_privilege</c> and held in memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held rather than read per request, because it is read on every request.</b> Authorization
/// resolves a principal's privileges on each call; a round trip for that would put the identity
/// store on the hot path of everything. The set changes when an administrator edits a role, which is
/// rare and which this process learns about immediately.
/// </para>
/// <para>
/// <b>Refreshed on write, and on a lifetime as a backstop.</b> The endpoint that edits a role calls
/// <see cref="RefreshAsync"/>, so in a single process a revocation takes effect on the next request.
/// The lifetime is for the deployment ADR-007 allows but does not require — more than one process
/// against one store — where the other process has no way to be told. **Thirty seconds, chosen for
/// the direction of the risk:** a stale grant during that window is a privilege that still works
/// after being revoked, which is worse than one that does not work yet, so the window is short
/// rather than convenient.
/// </para>
/// <para>
/// <b>An unreachable store does not fall back to the compiled table.</b> That would resurrect
/// exactly the grants a deployment had edited away, which is the one direction ADR-018 refuses. What
/// happens instead is that the last known answer keeps being used, and if there has never been one
/// the answer is empty — a principal with no privileges, which is the same conservative reading
/// <see cref="Roles.PrivilegesOf"/> gives an unknown role.
/// </para>
/// </remarks>
public sealed partial class PostgresRoleGrants : IRoleGrants, IDisposable
{
    /// <summary>How long a held answer may be used without a write telling us otherwise.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresRoleGrants> _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _reading = new(1, 1);

    private ImmutableDictionary<string, ImmutableHashSet<Privilege>> _held =
        ImmutableDictionary<string, ImmutableHashSet<Privilege>>.Empty;

    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private bool _everRead;

    /// <summary>Creates the source.</summary>
    /// <param name="dataSource">The platform store.</param>
    /// <param name="log">Where an ignored privilege name is reported.</param>
    /// <param name="clock">The clock, injected so the lifetime is testable.</param>
    public PostgresRoleGrants(
        NpgsqlDataSource dataSource,
        ILogger<PostgresRoleGrants> log,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(log);

        _dataSource = dataSource;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public ImmutableHashSet<Privilege> PrivilegesOf(string role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return _held.TryGetValue(role, out ImmutableHashSet<Privilege>? privileges)
            ? privileges
            : [];
    }

    /// <inheritdoc/>
    public ImmutableDictionary<string, ImmutableHashSet<Privilege>> All() => _held;

    /// <summary>
    /// Reads the grants if they have never been read or the lifetime has passed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>Called from the authentication path, which runs on every request.</b> The common case is
    /// a comparison against the clock and nothing else. The read is serialised so a burst of
    /// concurrent requests arriving on a cold cache issues one query rather than one each — the
    /// same reasoning <c>ServiceContexts</c> gives for its <c>Lazy</c>.
    /// </remarks>
    /// <returns>Nothing.</returns>
    public async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (_everRead && _clock.GetUtcNow() - _readAt < Lifetime)
        {
            return;
        }

        await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // <b>Checked again inside the gate, and this is the only place that check belongs.</b>
            // Everybody who queued behind a read wanted the answer it produced, not another read of
            // their own. Putting it in `RefreshAsync` instead made the forced refresh a no-op.
            if (_everRead && _clock.GetUtcNow() - _readAt < Lifetime)
            {
                return;
            }

            await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Always reads. This is the forced path and it must not consult the clock.</b>
    /// <para>
    /// The first version shared one method with <see cref="EnsureFreshAsync"/> and kept the
    /// freshness check inside it, so an explicit refresh **returned without reading** whenever the
    /// held answer was younger than <see cref="Lifetime"/> — which is always the case immediately
    /// after a request, and a request is what precedes an administrator editing a role. The whole
    /// point of the call was defeated by the guard that made the other call cheap.
    /// </para>
    /// <para>
    /// <b>Measured, not reviewed into existence.</b> A member was given the `user` role,
    /// `content:publishFeatures` was added to that role, and the member still got 403 — three times.
    /// Nothing in the code read wrong; the two callers wanted opposite things from one method.
    /// </para>
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <summary>Reads the grants. The caller holds the gate.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        {
            Dictionary<string, ImmutableHashSet<Privilege>.Builder> building =
                new(StringComparer.Ordinal);

            const string Sql = "select role_name, privilege from role_privilege";

            await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string role = reader.GetString(0);
                string name = reader.GetString(1);

                if (!Roles.TryParsePrivilege(name, out Privilege privilege))
                {
                    // <b>Ignored and logged, not refused.</b> A row written by a newer version must
                    // not stop this one from starting, which is the direction the schema handshake
                    // takes for the same reason. Logged at warning because a name nobody
                    // understands is either an upgrade in progress or a typo somebody made by hand.
                    Log.UnknownPrivilege(_log, name, role);
                    continue;
                }

                if (!building.TryGetValue(role, out ImmutableHashSet<Privilege>.Builder? held))
                {
                    held = ImmutableHashSet.CreateBuilder<Privilege>();
                    building[role] = held;
                }

                held.Add(privilege);
            }

            ImmutableDictionary<string, ImmutableHashSet<Privilege>>.Builder answer =
                ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<Privilege>>(
                    StringComparer.Ordinal);

            foreach ((string role, ImmutableHashSet<Privilege>.Builder held) in building)
            {
                answer[role] = held.ToImmutable();
            }

            _held = answer.ToImmutable();
            _readAt = _clock.GetUtcNow();
            _everRead = true;
        }
    }

    /// <summary>Releases the gate that serialises reads.</summary>
    /// <remarks>
    /// <b>Present because CA1001 is right.</b> Nothing disposes this in the host — it is a singleton
    /// that lives as long as the process — but a type owning a `SemaphoreSlim` and offering no way to
    /// release it is a type that cannot be used from a test that creates several.
    /// </remarks>
    public void Dispose() => _reading.Dispose();

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1300,
            Level = LogLevel.Warning,
            Message = "Role '{Role}' grants '{Privilege}', which this build does not know. It is "
                + "being ignored. Either the store was written by a newer version, or the row was "
                + "written by hand.")]
        public static partial void UnknownPrivilege(
            ILogger logger, string privilege, string role);
    }
}
