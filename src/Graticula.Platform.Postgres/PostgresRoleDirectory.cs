using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Roles and what they grant, over <c>role</c> and <c>role_privilege</c>.
/// </summary>
/// <remarks>
/// <b>Every rule ADR-035 states is enforced here rather than at the endpoint.</b> They are rules
/// about what the store may contain, and an endpoint is one caller.
/// </remarks>
public sealed class PostgresRoleDirectory : IRoleDirectory
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the directory.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresRoleDirectory(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RoleGrant>> ListAsync(CancellationToken cancellationToken)
    {
        // <b>One statement, because three round trips over three tables is how a screen comes to
        // show a role's privileges beside somebody else's member count.</b>
        const string Sql = """
            select r.name,
                   r.description,
                   coalesce(array_agg(p.privilege) filter (where p.privilege is not null), '{}'),
                   (select count(*) from principal_role pr where pr.role_name = r.name)
              from role r
              left join role_privilege p on p.role_name = r.name
             group by r.name, r.description
             order by r.name
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);

        List<RoleGrant> answer = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            string[] names = reader.GetFieldValue<string[]>(2);

            ImmutableHashSet<Privilege>.Builder held = ImmutableHashSet.CreateBuilder<Privilege>();

            foreach (string privilege in names)
            {
                // Unknown names are dropped rather than surfaced: the same reading
                // `PostgresRoleGrants` gives, and a screen must not offer a tick for a privilege
                // this build cannot enforce.
                if (Roles.TryParsePrivilege(privilege, out Privilege parsed))
                {
                    held.Add(parsed);
                }
            }

            answer.Add(new RoleGrant(
                name,
                reader.GetString(1),
                held.ToImmutable(),
                BuiltIn: Roles.All.Contains(name, StringComparer.Ordinal),
                Members: (int)reader.GetInt64(3)));
        }

        // <b>Built-in roles first, in ADR-018 §3c's order of authority.</b> Alphabetical would put
        // `administrator` above `viewer` by accident and a custom role in the middle of them, and the
        // five are the ones a reader uses as reference points.
        return
        [
            .. answer
                .OrderBy(r => r.BuiltIn ? Roles.All.IndexOf(r.Name) : int.MaxValue)
                .ThenBy(r => r.Name, StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc/>
    public async Task<(RoleChange Outcome, string? Detail)> CreateAsync(
        string name,
        string description,
        IReadOnlyList<string> privileges,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(privileges);

        if (IsAdministrator(name))
        {
            return (RoleChange.Administrator, null);
        }

        (RoleChange check, string? why, ImmutableHashSet<Privilege> wanted) = Read(privileges);

        if (check != RoleChange.Done)
        {
            return (check, why);
        }

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand insert = new(
            "insert into role (name, description) values (@name, @description) "
            + "on conflict (name) do nothing",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("description", description ?? string.Empty);

            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return (RoleChange.Exists, null);
            }
        }

        await WriteAsync(connection, transaction, name, wanted, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return (RoleChange.Done, null);
    }

    /// <inheritdoc/>
    public async Task<(RoleChange Outcome, string? Detail)> SetPrivilegesAsync(
        string name,
        IReadOnlyList<string> privileges,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(privileges);

        // <b>ADR-035 §4b, refused before anything is read.</b> The check short-circuits for an
        // administrator anyway, so an accepted edit here would change nothing and say it had — which
        // is worse than a refusal, because somebody would believe it.
        if (IsAdministrator(name))
        {
            return (RoleChange.Administrator, null);
        }

        (RoleChange check, string? why, ImmutableHashSet<Privilege> wanted) = Read(privileges);

        if (check != RoleChange.Done)
        {
            return (check, why);
        }

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand exists = new(
            "select 1 from role where name = @name for update", connection, transaction))
        {
            exists.Parameters.AddWithValue("name", name);

            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return (RoleChange.Absent, null);
            }
        }

        await using (NpgsqlCommand clear = new(
            "delete from role_privilege where role_name = @name", connection, transaction))
        {
            clear.Parameters.AddWithValue("name", name);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync(connection, transaction, name, wanted, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return (RoleChange.Done, null);
    }

    /// <inheritdoc/>
    public async Task<RoleChange> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (IsAdministrator(name))
        {
            return RoleChange.Administrator;
        }

        // <b>Built-in roles are not removable either, and that is not in ADR-035's text.</b> It
        // follows from the seed: a built-in role removed here comes back on the next migration of a
        // fresh store, so the two would disagree about what exists. Their *privileges* stay
        // editable — the owner's rule is about the administrator, not about being built in.
        if (Roles.All.Contains(name, StringComparer.Ordinal))
        {
            return RoleChange.BuiltIn;
        }

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand held = new(
            "select count(*) from principal_role where role_name = @name", connection, transaction))
        {
            held.Parameters.AddWithValue("name", name);

            object? count = await held.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (count is long n && n > 0)
            {
                return RoleChange.StillHeld;
            }
        }

        await using (NpgsqlCommand remove = new(
            "delete from role where name = @name", connection, transaction))
        {
            remove.Parameters.AddWithValue("name", name);

            if (await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return RoleChange.Absent;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return RoleChange.Done;
    }

    private static bool IsAdministrator(string name) =>
        string.Equals(name, Roles.Administrator, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a submitted privilege list, refusing an unknown name or a missing prerequisite.
    /// </summary>
    /// <remarks>
    /// <b>Implications are not applied here, deliberately.</b> A role that grants
    /// <c>features:fullEdit</c> is stored with exactly that and passes an <c>features:edit</c> check
    /// at resolution time (ADR-035 §4e). Adding the narrower row here would show two ticks for one
    /// decision on the screen that reads it back.
    /// </remarks>
    private static (RoleChange Outcome, string? Detail, ImmutableHashSet<Privilege> Wanted) Read(
        IReadOnlyList<string> privileges)
    {
        ImmutableHashSet<Privilege>.Builder wanted = ImmutableHashSet.CreateBuilder<Privilege>();

        foreach (string name in privileges)
        {
            if (!Roles.TryParsePrivilege(name, out Privilege privilege))
            {
                return (RoleChange.UnknownPrivilege, name, []);
            }

            wanted.Add(privilege);
        }

        ImmutableHashSet<Privilege> set = wanted.ToImmutable();

        foreach (Privilege privilege in set)
        {
            if (!Roles.Prerequisites.TryGetValue(privilege, out ImmutableArray<Privilege> needs))
            {
                continue;
            }

            foreach (Privilege need in needs)
            {
                // The wider privilege satisfies the prerequisite: a role holding `features:fullEdit`
                // meets a requirement for `features:edit` without a row for it.
                if (set.Contains(need) || Satisfied(set, need))
                {
                    continue;
                }

                return (
                    RoleChange.MissingPrerequisite,
                    $"{Roles.NameOf(privilege)} requires {Roles.NameOf(need)}",
                    []);
            }
        }

        return (RoleChange.Done, null, set);
    }

    /// <summary>Whether something in the set already contains this privilege.</summary>
    private static bool Satisfied(ImmutableHashSet<Privilege> set, Privilege need)
    {
        foreach (Privilege held in set)
        {
            if (Roles.Implies.TryGetValue(held, out ImmutableArray<Privilege> narrower)
                && narrower.Contains(need))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        ImmutableHashSet<Privilege> privileges,
        CancellationToken cancellationToken)
    {
        foreach (Privilege privilege in privileges)
        {
            await using NpgsqlCommand add = new(
                "insert into role_privilege (role_name, privilege) values (@role, @privilege)",
                connection,
                transaction);

            add.Parameters.AddWithValue("role", role);
            add.Parameters.AddWithValue("privilege", Roles.NameOf(privilege));

            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
