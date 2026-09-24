using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// OpenID Connect providers and the accounts they name, in the platform store — migration 56, ADR-088.
/// </summary>
public sealed class PostgresIdentityProviderStore : IIdentityProviderStore
{
    private const string Select = """
        select ip.id, ip.name, ip.issuer, ip.client_id, ip.scopes, ip.username_claim, ip.auto_create,
               ip.default_role, ip.default_user_type, ip.enabled, ip.client_secret is not null,
               (select count(*)::int from external_identity e where e.provider_id = ip.id)
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
                 auto_create, default_role, default_user_type, enabled)
            values (@name, @issuer, @client, @secret, @version, @scopes, @claim, @auto, @role, @type, @enabled)
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
                    reader.GetBoolean(9)),
                reader.GetBoolean(10),
                reader.GetInt32(11)));
        }

        return read;
    }
}
