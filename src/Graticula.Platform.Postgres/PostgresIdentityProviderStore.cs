using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// OpenID Connect providers, directories and SAML providers, and the accounts they name, in the platform store —
/// migrations 56, 57 and 58; ADR-088, ADR-089, ADR-090.
/// </summary>
public sealed class PostgresIdentityProviderStore : IIdentityProviderStore
{
    private const string Select = """
        select ip.id, ip.name, ip.issuer, ip.client_id, ip.scopes, ip.username_claim, ip.auto_create,
               ip.default_role, ip.default_user_type, ip.enabled, ip.client_secret is not null,
               (select count(*)::int from external_identity e where e.provider_id = ip.id),
               ip.kind, ip.groups_claim, ip.ldap::text, ip.saml::text
          from identity_provider ip
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Reads and writes providers in a platform store.</summary>
    /// <param name="dataSource">The store.</param>
    public PostgresIdentityProviderStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IdentityProvider>> ListAsync(CancellationToken cancellationToken) =>
        await ReadAsync($"{Select} order by lower(ip.name)", null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IdentityProvider?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        (await ReadAsync($"{Select} where ip.id = @id", id, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc/>
    public async Task<(byte[] Secret, int KeyVersion)?> SecretOfAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select client_secret, key_version from identity_provider where id = @id and client_secret is not null");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetFieldValue<byte[]>(0), reader.GetInt32(1))
            : null;
    }

    /// <inheritdoc/>
    public async Task<IdentityProvider?> CreateAsync(
        IdentityProviderSettings settings, (byte[] Secret, int KeyVersion)? secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            insert into identity_provider
                (name, issuer, client_id, client_secret, key_version, scopes, username_claim,
                 auto_create, default_role, default_user_type, enabled, kind, groups_claim, ldap, saml)
            values (@name, @issuer, @client, @secret, @version, @scopes, @claim, @auto, @role, @type, @enabled,
                    @kind, @groups, @ldap::jsonb, @saml::jsonb)
            on conflict ((lower(name))) do nothing
            returning id
            """);
        Bind(command, settings);
        command.Parameters.Add(new NpgsqlParameter("secret", NpgsqlDbType.Bytea) { Value = (object?)secret?.Secret ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer) { Value = (object?)secret?.KeyVersion ?? DBNull.Value });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id
            ? await FindAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(
        Guid id,
        IdentityProviderSettings settings,
        (byte[] Secret, int KeyVersion)? secret,
        CancellationToken cancellationToken,
        bool clearSecret = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string secretSet = clearSecret
            ? "client_secret = null, key_version = null,"
            : secret is null ? string.Empty : "client_secret = @secret, key_version = @version,";

        await using NpgsqlCommand command = _dataSource.CreateCommand($"""
            update identity_provider
               set name = @name, issuer = @issuer, client_id = @client, scopes = @scopes, username_claim = @claim,
                   auto_create = @auto, default_role = @role, default_user_type = @type, enabled = @enabled,
                   kind = @kind, groups_claim = @groups, ldap = @ldap::jsonb, saml = @saml::jsonb,
                   {secretSet}
                   updated_at = now()
             where id = @id
               and not exists (select 1 from identity_provider o where lower(o.name) = lower(@name) and o.id <> @id)
            """);
        Bind(command, settings);
        command.Parameters.AddWithValue("id", id);

        if (!clearSecret && secret is { } sealedSecret)
        {
            command.Parameters.AddWithValue("secret", NpgsqlDbType.Bytea, sealedSecret.Secret);
            command.Parameters.AddWithValue("version", sealedSecret.KeyVersion);
        }

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<int> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand count = _dataSource.CreateCommand(
            "select count(*)::int from external_identity where provider_id = @id");
        count.Parameters.AddWithValue("id", id);

        int accounts = (int)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (accounts > 0)
        {
            return accounts;
        }

        await using NpgsqlCommand delete = _dataSource.CreateCommand("delete from identity_provider where id = @id");
        delete.Parameters.AddWithValue("id", id);

        try
        {
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // An account bound between the count and the delete: the foreign key decides, and it is counted again.
            return Math.Max(1, accounts);
        }
    }

    /// <inheritdoc/>
    public async Task<Principal?> FindPrincipalAsync(
        Guid providerId, string subject, string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        // <b>The subject first</b>: it is the claim a provider never reuses. The name is only how an account made
        // ahead of a first sign-in is found, and only one with no subject yet — so a name the provider later
        // gives somebody else never reaches an account already bound to another person.
        await using (NpgsqlCommand bind = new("""
            update external_identity set subject = @subject
             where provider_id = @provider and subject is null and lower(username) = lower(@username)
               and not exists (select 1 from external_identity o where o.provider_id = @provider and o.subject = @subject)
            """, connection, transaction))
        {
            bind.Parameters.AddWithValue("provider", providerId);
            bind.Parameters.AddWithValue("subject", subject);
            bind.Parameters.AddWithValue("username", username);
            await bind.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Principal? found = null;

        await using (NpgsqlCommand read = new("""
            select p.id, p.kind, p.name, p.display_name, p.disabled_at is not null
              from external_identity e join principal p on p.id = e.principal_id
             where e.provider_id = @provider and e.subject = @subject
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("provider", providerId);
            read.Parameters.AddWithValue("subject", subject);

            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found = new Principal(
                    reader.GetGuid(0),
                    reader.GetString(1) == "user" ? PrincipalKind.User : PrincipalKind.Service,
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetBoolean(4));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <inheritdoc/>
    public async Task<Principal?> CreateMemberAsync(
        Guid providerId,
        string? subject,
        string username,
        string accountName,
        string? displayName,
        string role,
        string userType,
        CancellationToken cancellationToken,
        bool numberIfTaken = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        Guid id = Guid.NewGuid();
        string? name = null;

        // <b>The account's name here is the first free of the one asked and its numbered variants</b>, because it
        // is this server's namespace, where a local account may already have it — and ADR-088 keeps the two apart
        // rather than joining them by name.
        for (int n = 1; n <= (numberIfTaken ? 1000 : 1) && name is null; n++)
        {
            string candidate = n == 1 ? accountName : string.Create(CultureInfo.InvariantCulture, $"{accountName}{n}");

            await using NpgsqlCommand principal = new("""
                insert into principal (id, kind, name, display_name, user_type)
                values (@id, 'user', @name, @display, @type)
                on conflict (name) do nothing
                """, connection, transaction);
            principal.Parameters.AddWithValue("id", id);
            principal.Parameters.AddWithValue("name", candidate);
            principal.Parameters.Add(new NpgsqlParameter("display", NpgsqlDbType.Text) { Value = (object?)displayName ?? DBNull.Value });
            principal.Parameters.AddWithValue("type", userType);

            if (await principal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                name = candidate;
            }
        }

        if (name is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using (NpgsqlCommand grant = new(
            "insert into principal_role (principal_id, role_name) values (@principal, @role)", connection, transaction))
        {
            grant.Parameters.AddWithValue("principal", id);
            grant.Parameters.AddWithValue("role", role);
            await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand link = new("""
            insert into external_identity (principal_id, provider_id, subject, username)
            values (@principal, @provider, @subject, @username)
            on conflict do nothing
            """, connection, transaction))
        {
            link.Parameters.AddWithValue("principal", id);
            link.Parameters.AddWithValue("provider", providerId);
            link.Parameters.Add(new NpgsqlParameter("subject", NpgsqlDbType.Text) { Value = (object?)subject ?? DBNull.Value });
            link.Parameters.AddWithValue("username", username);

            if (await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                // The provider already names an account by this subject or name: that one, not a second.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Principal(id, PrincipalKind.User, name, displayName, isDisabled: false);
    }

    /// <inheritdoc/>
    public async Task<(Guid ProviderId, string Username)?> ExternalOfAsync(Guid principalId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select provider_id, username from external_identity where principal_id = @id limit 1");
        command.Parameters.AddWithValue("id", principalId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, ExternalMember>> ExternalMembersAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            select e.principal_id, ip.name, e.username,
                   -- <b>Locked only while a mapping still gives roles</b> — design review 2026-09-24: the flag alone
                   -- stayed set after the mapping that set it was removed, and the account could never be changed.
                   e.role_managed and exists (select 1 from group_mapping gm
                                               where gm.provider_id = e.provider_id and gm.role_name is not null)
              from external_identity e join identity_provider ip on ip.id = e.provider_id
            """);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<Guid, ExternalMember> read = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            read[reader.GetGuid(0)] = new ExternalMember(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
        }

        return read;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GroupMapping>> MappingsAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            select m.external_group, m.role_name, m.group_id, g.name
              from group_mapping m left join sharing_group g on g.id = m.group_id
             where m.provider_id = @provider
             order by lower(m.external_group)
            """);
        command.Parameters.AddWithValue("provider", providerId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<GroupMapping> read = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            read.Add(new GroupMapping(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return read;
    }

    /// <inheritdoc/>
    public async Task SetMappingsAsync(Guid providerId, IReadOnlyList<GroupMapping> mappings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand clear = new("delete from group_mapping where provider_id = @provider", connection, transaction))
        {
            clear.Parameters.AddWithValue("provider", providerId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (GroupMapping mapping in mappings)
        {
            await using NpgsqlCommand add = new("""
                insert into group_mapping (provider_id, external_group, role_name, group_id)
                values (@provider, @group, @role, @target)
                """, connection, transaction);
            add.Parameters.AddWithValue("provider", providerId);
            add.Parameters.AddWithValue("group", mapping.ExternalGroup.Trim());
            add.Parameters.Add(new NpgsqlParameter("role", NpgsqlDbType.Text) { Value = (object?)mapping.Role ?? DBNull.Value });
            add.Parameters.Add(new NpgsqlParameter("target", NpgsqlDbType.Uuid) { Value = (object?)mapping.GroupId ?? DBNull.Value });
            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MappingApplied> ApplyMappingsAsync(
        Guid principalId,
        Guid providerId,
        IReadOnlyCollection<string> groups,
        Func<string, int> rank,
        string defaultRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(rank);

        IReadOnlyList<GroupMapping> mappings = await MappingsAsync(providerId, cancellationToken).ConfigureAwait(false);

        // With no mapping that gives a role, the role is Members' again, and the account says so from this sign-in.
        if (!mappings.Any(m => m.Role is not null))
        {
            await using NpgsqlCommand released = _dataSource.CreateCommand(
                "update external_identity set role_managed = false where principal_id = @p and provider_id = @provider and role_managed");
            released.Parameters.AddWithValue("p", principalId);
            released.Parameters.AddWithValue("provider", providerId);
            await released.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (mappings.Count == 0)
        {
            return new MappingApplied(null, [], []);
        }

        // <b>A group is matched by its whole name or, for a DN, by its first part</b> — AD's memberOf gives
        // `CN=GIS-Admins,OU=Groups,DC=contoso,DC=com`, and an operator will write GIS-Admins.
        HashSet<string> held = new(StringComparer.OrdinalIgnoreCase);

        foreach (string group in groups)
        {
            held.Add(group.Trim());

            if (FirstPart(group) is { } first)
            {
                held.Add(first);
            }
        }

        List<GroupMapping> matched = [.. mappings.Where(m => held.Contains(m.ExternalGroup.Trim()))];

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string? role = null;

        // <b>Roles only when a mapping gives roles at all</b>: a provider mapped to groups alone leaves roles to Members.
        if (mappings.Any(m => m.Role is not null))
        {
            role = matched.Where(m => m.Role is not null).Select(m => m.Role!).OrderByDescending(rank).FirstOrDefault()
                ?? defaultRole;

            // <b>The last administrator is not demoted by a sign-in</b> — the refusal Members makes by hand, for the
            // same reason: a server nobody can administer has no way back.
            bool lastAdministrator = !string.Equals(role, "administrator", StringComparison.Ordinal)
                && await ScalarAsync<bool>(connection, transaction, """
                    select exists (select 1 from principal_role where principal_id = @p and role_name = 'administrator')
                       and not exists (select 1 from principal_role r join principal x on x.id = r.principal_id
                                        where r.role_name = 'administrator' and r.principal_id <> @p and x.disabled_at is null)
                    """, principalId, cancellationToken).ConfigureAwait(false);

            if (!lastAdministrator)
            {
                await ExecuteAsync(connection, transaction,
                    "delete from principal_role where principal_id = @p", principalId, cancellationToken).ConfigureAwait(false);

                await using NpgsqlCommand grant = new(
                    "insert into principal_role (principal_id, role_name) values (@p, @role)", connection, transaction);
                grant.Parameters.AddWithValue("p", principalId);
                grant.Parameters.AddWithValue("role", role);
                await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                role = "administrator";
            }

            await using NpgsqlCommand managed = new(
                "update external_identity set role_managed = true where principal_id = @p and provider_id = @provider",
                connection, transaction);
            managed.Parameters.AddWithValue("p", principalId);
            managed.Parameters.AddWithValue("provider", providerId);
            await managed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // <b>Membership of every group here a mapping names, as the matched groups say</b> — and only those. A
        // group no mapping names is the Groups screen's, and a manager is left a manager.
        HashSet<Guid> named = [.. mappings.Where(m => m.GroupId is not null).Select(m => m.GroupId!.Value)];
        HashSet<Guid> wanted = [.. matched.Where(m => m.GroupId is not null).Select(m => m.GroupId!.Value)];
        Dictionary<Guid, string> names = mappings.Where(m => m.GroupId is not null)
            .GroupBy(m => m.GroupId!.Value).ToDictionary(g => g.Key, g => g.First().GroupName ?? g.Key.ToString());

        List<string> joined = [];
        List<string> left = [];

        foreach (Guid group in named)
        {
            if (wanted.Contains(group))
            {
                await using NpgsqlCommand join = new("""
                    insert into sharing_group_member (group_id, principal_id, membership)
                    values (@g, @p, 'member') on conflict do nothing
                    """, connection, transaction);
                join.Parameters.AddWithValue("g", group);
                join.Parameters.AddWithValue("p", principalId);

                if (await join.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    joined.Add(names[group]);
                }
            }
            else
            {
                await using NpgsqlCommand leave = new("""
                    delete from sharing_group_member where group_id = @g and principal_id = @p and membership = 'member'
                    """, connection, transaction);
                leave.Parameters.AddWithValue("g", group);
                leave.Parameters.AddWithValue("p", principalId);

                if (await leave.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    left.Add(names[group]);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MappingApplied(role, joined, left);
    }

    /// <summary>The value of a DN's first part — <c>GIS-Admins</c> of <c>CN=GIS-Admins,OU=…</c> — or null.</summary>
    private static string? FirstPart(string group)
    {
        int equals = group.IndexOf('=', StringComparison.Ordinal);
        int comma = group.IndexOf(',', StringComparison.Ordinal);

        return equals > 0 && comma > equals ? group[(equals + 1)..comma].Trim() : null;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid principal, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("p", principal);
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid principal, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("p", principal);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(NpgsqlCommand command, IdentityProviderSettings settings)
    {
        command.Parameters.AddWithValue("name", settings.Name.Trim());
        command.Parameters.AddWithValue("issuer", settings.Issuer.Trim());
        command.Parameters.AddWithValue("client", settings.ClientId.Trim());
        command.Parameters.AddWithValue("scopes", settings.Scopes.Trim());
        command.Parameters.AddWithValue("claim", settings.UsernameClaim.Trim());
        command.Parameters.AddWithValue("auto", settings.AutoCreate);
        command.Parameters.AddWithValue("role", settings.DefaultRole);
        command.Parameters.AddWithValue("type", settings.DefaultUserType);
        command.Parameters.AddWithValue("enabled", settings.Enabled);
        command.Parameters.AddWithValue("kind", settings.Kind);
        command.Parameters.AddWithValue("groups", settings.GroupsClaim);
        command.Parameters.Add(new NpgsqlParameter("ldap", NpgsqlDbType.Text)
        {
            Value = settings.Ldap is null ? DBNull.Value : JsonSerializer.Serialize(settings.Ldap),
        });
        command.Parameters.Add(new NpgsqlParameter("saml", NpgsqlDbType.Text)
        {
            Value = settings.Saml is null ? DBNull.Value : JsonSerializer.Serialize(settings.Saml),
        });
    }

    /// <inheritdoc/>
    public async Task SetSamlMetadataAsync(Guid id, string metadata, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            update identity_provider
               set saml = saml || jsonb_build_object('Metadata', @metadata::text, 'FetchedAt', @at::timestamptz)
             where id = @id and saml is not null
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("metadata", metadata);
        command.Parameters.AddWithValue("at", fetchedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> ConsumeAssertionAsync(
        Guid providerId, string assertionId, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assertionId);

        // What has expired is swept by the next sign-in rather than by a schedule: a row outlives its assertion by
        // at most the time until somebody signs in again, and needs no second thing running to go away.
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            with swept as (delete from saml_assertion_used where until < now())
            insert into saml_assertion_used (provider_id, assertion_id, until)
            values (@provider, @assertion, @until)
            on conflict do nothing
            """);
        command.Parameters.AddWithValue("provider", providerId);
        command.Parameters.AddWithValue("assertion", assertionId);
        command.Parameters.AddWithValue("until", until);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<List<IdentityProvider>> ReadAsync(string sql, Guid? id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);

        if (id is { } value)
        {
            command.Parameters.AddWithValue("id", value);
        }

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<IdentityProvider> read = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            read.Add(new IdentityProvider(
                reader.GetGuid(0),
                new IdentityProviderSettings(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetBoolean(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetBoolean(9),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.IsDBNull(14) ? null : JsonSerializer.Deserialize<LdapSettings>(reader.GetString(14)),
                    reader.IsDBNull(15) ? null : JsonSerializer.Deserialize<SamlSettings>(reader.GetString(15))),
                reader.GetBoolean(10),
                reader.GetInt32(11)));
        }

        return read;
    }
}
