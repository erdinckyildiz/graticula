using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Webhooks;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary><see cref="IWebhookStore"/> over the platform store — migration 130, ADR-266.</summary>
/// <remarks>
/// <para>
/// <b>A webhook's reads and writes are its organisation's</b>, routed through the floor as every store's are (ADR-204).
/// <b>The sender's are nobody's</b>: it claims, records and prunes on the pool itself, which the floor's policy reads in
/// full, because a deployment of many organisations has one sender per node and not one per organisation.
/// </para>
/// <para>
/// <b>The claim is ADR-011 §3.2's statement</b> — select, lock with <c>skip locked</c>, update, in one — so two nodes
/// claiming at once take different rows and neither waits on the other, and a crash between the select and the update
/// is not a state. A lease rather than a status says who holds a row: a node that dies holding one leaves a lease that
/// lapses, and the row is due again.
/// </para>
/// </remarks>
public sealed class PostgresWebhookStore : IWebhookStore
{
    private const string Columns = """
        w.id, w.kind, w.service_id, w.number, w.name, w.url, w.events, w.secret is not null, w.active,
        w.owner_principal_id, p.name, w.modified_by, w.base_url, w.content_type, w.payload_format, w.max_retries,
        w.failures_allowed, w.failure_days, w.properties::text, w.deactivated_reason, w.created_at, w.modified_at
        """;

    private const string From = "webhook w left join principal p on p.id = w.owner_principal_id";

    private readonly NpgsqlDataSource _root;

    /// <summary>The tenant's connections where the deployment holds several (ADR-204), otherwise the store's own.</summary>
    private TenantSource _dataSource => TenantDataSources.Route(_root);

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    public PostgresWebhookStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _root = dataSource;
    }

    /// <inheritdoc/>
    public async Task<WebhookDefinition?> CreateAsync(NewWebhook made, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(made);

        // A feature service's webhooks are numbered within it, as ArcGIS numbers them, and are at most ten. The count and
        // the number are taken in the statement that inserts, and the unique index on (service, number) refuses the
        // second of two made at once rather than giving both the same number.
        const string Sql = $"""
            with made as (
                insert into webhook
                  (id, kind, service_id, number, name, url, events, secret, secret_key_version, active,
                   owner_principal_id, base_url, content_type, payload_format, max_retries, failures_allowed,
                   failure_days, properties)
                select @id, @kind, @service,
                       case when @service is null then null
                            else coalesce((select max(number) from webhook where service_id = @service), 0) + 1 end,
                       @name, @url, @events, @secret, @version, @active, @owner, @base, @content, @format, @retries,
                       @failures, @days, @properties::jsonb
                 where @service is null
                    or (select count(*) from webhook where service_id = @service) < @per
                returning *
            )
            select {Columns} from made w left join principal p on p.id = w.owner_principal_id
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("kind", Wire(made.Kind));
        command.Parameters.Add(new NpgsqlParameter("service", NpgsqlDbType.Uuid) { Value = (object?)made.ServiceId ?? DBNull.Value });
        command.Parameters.AddWithValue("name", made.Name);
        command.Parameters.AddWithValue("url", made.Url);
        command.Parameters.AddWithValue("events", made.Events.ToArray());
        command.Parameters.Add(new NpgsqlParameter("secret", NpgsqlDbType.Bytea) { Value = (object?)made.Secret ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer) { Value = (object?)made.SecretKeyVersion ?? DBNull.Value });
        command.Parameters.AddWithValue("active", made.Active);
        command.Parameters.AddWithValue("owner", made.Owner);
        command.Parameters.AddWithValue("base", made.BaseUrl);
        command.Parameters.AddWithValue("content", made.ContentType);
        command.Parameters.AddWithValue("format", made.PayloadFormat);
        command.Parameters.AddWithValue("retries", made.MaxRetries);
        command.Parameters.AddWithValue("failures", made.FailuresAllowed);
        command.Parameters.AddWithValue("days", made.FailureDays);
        command.Parameters.AddWithValue("properties", made.Properties);
        command.Parameters.AddWithValue("per", IWebhookStore.PerService);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebhookDefinition>> ListAsync(
        WebhookKind kind, Guid? serviceId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} from {From} where w.kind = @kind "
            + "and (@service is null or w.service_id = @service) order by w.created_at, w.id");
        command.Parameters.AddWithValue("kind", Wire(kind));
        command.Parameters.Add(new NpgsqlParameter("service", NpgsqlDbType.Uuid) { Value = (object?)serviceId ?? DBNull.Value });

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<WebhookDefinition?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"select {Columns} from {From} where w.id = @id");
        command.Parameters.AddWithValue("id", id);

        return First(await ReadAllAsync(command, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<WebhookDefinition?> FindByNumberAsync(Guid serviceId, int number, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"select {Columns} from {From} where w.service_id = @service and w.number = @number");
        command.Parameters.AddWithValue("service", serviceId);
        command.Parameters.AddWithValue("number", number);

        return First(await ReadAllAsync(command, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<WebhookDefinition?> UpdateAsync(
        Guid id, WebhookUpdate update, Guid by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        // An activation clears the reason the server gave for a deactivation; a deactivation drops what was waiting,
        // because a webhook that is off sends nothing and a backlog sent the moment it is switched on again would be news
        // its receiver had been told it would not get.
        const string Sql = $"""
            with changed as (
                update webhook set
                    name               = coalesce(@name, name),
                    url                = coalesce(@url, url),
                    events             = coalesce(@events, events),
                    secret             = case when @secret is null then secret
                                              when octet_length(@secret) = 0 then null else @secret end,
                    secret_key_version = case when @secret is null then secret_key_version
                                              when octet_length(@secret) = 0 then null else @version end,
                    active             = coalesce(@active, active),
                    deactivated_reason = case when @active is null then deactivated_reason else null end,
                    content_type       = coalesce(@content, content_type),
                    payload_format     = coalesce(@format, payload_format),
                    max_retries        = coalesce(@retries, max_retries),
                    failures_allowed   = coalesce(@failures, failures_allowed),
                    failure_days       = coalesce(@days, failure_days),
                    properties         = coalesce(@properties::jsonb, properties),
                    modified_by        = @by,
                    modified_at        = now()
                 where id = @id
                returning *
            ),
            dropped as (
                update webhook_delivery d
                   set status = 'dropped', finished_at = now(), lease_until = null,
                       last_error = 'The webhook was deactivated before this was sent.'
                  from changed c
                 where d.webhook_id = c.id and d.status = 'pending' and not c.active
            )
            select {Columns} from changed w left join principal p on p.id = w.owner_principal_id
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("by", by);
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = (object?)update.Name ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("url", NpgsqlDbType.Text) { Value = (object?)update.Url ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("events", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = (object?)update.Events?.ToArray() ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("secret", NpgsqlDbType.Bytea) { Value = (object?)update.Secret ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer) { Value = (object?)update.SecretKeyVersion ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("active", NpgsqlDbType.Boolean) { Value = (object?)update.Active ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("content", NpgsqlDbType.Text) { Value = (object?)update.ContentType ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("format", NpgsqlDbType.Text) { Value = (object?)update.PayloadFormat ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("retries", NpgsqlDbType.Integer) { Value = (object?)update.MaxRetries ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("failures", NpgsqlDbType.Integer) { Value = (object?)update.FailuresAllowed ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("days", NpgsqlDbType.Integer) { Value = (object?)update.FailureDays ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("properties", NpgsqlDbType.Text) { Value = (object?)update.Properties ?? DBNull.Value });

        return First(await ReadAllAsync(command, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from webhook where id = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc/>
    public async Task<int> EnqueuePortalAsync(
        IReadOnlyList<string> uris, string eventName, string body, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(EnqueueSql("w.kind = 'portal'"));
        command.Parameters.AddWithValue("keys", uris.ToArray());

        return await EnqueueAsync(command, eventName, body, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> EnqueueServiceAsync(
        Guid serviceId, IReadOnlyList<string> changeTypes, string eventName, string body, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            EnqueueSql("w.kind = 'featureService' and w.service_id = @service"));
        command.Parameters.AddWithValue("keys", changeTypes.ToArray());
        command.Parameters.AddWithValue("service", serviceId);

        return await EnqueueAsync(command, eventName, body, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ServiceHasWebhooksAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        // The enqueue's own organisation clause, so an answer is never another organisation's.
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            select exists (
                select 1 from webhook w
                 where w.kind = 'featureService' and w.service_id = @service and w.active
                   and w.tenant_id = coalesce(graticula_tenant(), '10000000-0000-0000-0000-000000000001'))
            """);
        command.Parameters.AddWithValue("service", serviceId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>
    /// One row per active webhook of the organisation that listens for the event — <b>pending, or dropped when the
    /// webhook already holds <see cref="IWebhookStore.PendingBound"/> undelivered</b>, so a receiver that is down while
    /// a layer is edited a million times costs a thousand rows and a count, not a million.
    /// </summary>
    private static string EnqueueSql(string which) => $"""
        insert into webhook_delivery (id, webhook_id, event, body, status, finished_at, last_error)
        select gen_random_uuid(), w.id, @event, @body::jsonb,
               case when q.waiting < @bound then 'pending' else 'dropped' end,
               case when q.waiting < @bound then null else now() end,
               case when q.waiting < @bound then null
                    else 'More than ' || @bound || ' events were waiting for this webhook, so this one was not queued.' end
          from webhook w
          cross join lateral (
              select count(*) as waiting from (
                  select 1 from webhook_delivery d
                   where d.webhook_id = w.id and d.status = 'pending' limit @bound) l) q
         where {which} and w.active and w.events && @keys
           and w.tenant_id = coalesce(graticula_tenant(), '10000000-0000-0000-0000-000000000001')
        """;

    private static async Task<int> EnqueueAsync(
        NpgsqlCommand command, string eventName, string body, CancellationToken cancellationToken)
    {
        command.Parameters.AddWithValue("event", eventName);
        command.Parameters.AddWithValue("body", body);
        command.Parameters.AddWithValue("bound", IWebhookStore.PendingBound);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(
        string worker, int count, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        // <b>The sender's statement, on the pool itself</b> — every organisation's due rows. A row is due when its time
        // has come and nobody holds a live lease on it; `skip locked` gives each row to one claimant, and the lease, set
        // in the same statement, keeps it theirs until it lapses.
        const string Claim = """
            with taken as (
                select d.id from webhook_delivery d
                 where d.status = 'pending' and d.next_attempt_at <= now()
                   and (d.lease_until is null or d.lease_until < now())
                 order by d.next_attempt_at
                 limit @count
                 for update skip locked
            )
            update webhook_delivery d
               set claimed_by = @worker, lease_until = now() + @lease, attempts = d.attempts + 1
              from taken
             where d.id = taken.id
            returning d.id, d.webhook_id, d.event, d.body::text, d.attempts, d.tenant_id
            """;

        List<(Guid Id, Guid Webhook, string Event, string Body, int Attempts, Guid Tenant)> taken = [];

        await using (NpgsqlCommand command = _root.CreateCommand(Claim))
        {
            command.Parameters.AddWithValue("count", count);
            command.Parameters.AddWithValue("worker", worker);
            command.Parameters.AddWithValue("lease", lease);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                taken.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetGuid(5)));
            }
        }

        if (taken.Count == 0)
        {
            return [];
        }

        // What each webhook is, its sealed secret, and its owner's standing now — read once per webhook, after the claim,
        // so a role taken away since the event was raised is the role the sender sees.
        const string Standing = $"""
            select {Columns}, w.secret, w.secret_key_version,
                   p.user_type, p.disabled_at is not null,
                   coalesce(array(select r.role_name from principal_role r where r.principal_id = w.owner_principal_id), array[]::text[]),
                   coalesce(i.owner_principal_id = w.owner_principal_id, false)
              from {From}
              left join item i on i.id = w.service_id
             where w.id = any(@ids)
            """;

        Dictionary<Guid, (WebhookDefinition Hook, byte[]? Secret, int? Version, OwnerStanding Standing)> hooks = [];

        await using (NpgsqlCommand command = _root.CreateCommand(Standing))
        {
            command.Parameters.AddWithValue("ids", taken.Select(t => t.Webhook).Distinct().ToArray());

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                WebhookDefinition hook = Read(reader);
                hooks[hook.Id] = (
                    hook,
                    reader.IsDBNull(22) ? null : reader.GetFieldValue<byte[]>(22),
                    reader.IsDBNull(23) ? null : reader.GetInt32(23),
                    new OwnerStanding(
                        reader.IsDBNull(24) ? null : reader.GetString(24),
                        reader.GetFieldValue<string[]>(26),
                        reader.GetBoolean(25),
                        reader.GetBoolean(27)));
            }
        }

        List<ClaimedDelivery> claimed = [];

        foreach ((Guid id, Guid webhook, string name, string body, int attempts, Guid tenant) in taken)
        {
            // A webhook removed between the claim and this read took its deliveries with it.
            if (hooks.TryGetValue(webhook, out var found))
            {
                claimed.Add(new ClaimedDelivery(id, found.Hook, name, body, attempts, tenant, found.Secret, found.Version, found.Standing));
            }
        }

        return claimed;
    }

    /// <inheritdoc/>
    public async Task DeliveredAsync(Guid id, string worker, int status, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _root.CreateCommand(
            "update webhook_delivery set status = 'delivered', last_status = @status, last_error = null, "
            + "finished_at = now(), lease_until = null where id = @id and claimed_by = @worker and status = 'pending'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("status", status);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> FailedAsync(
        Guid id, string worker, int? status, string reason, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        // <b>Failed attempts, counted in the webhook's window, against its policy</b> — ArcGIS's deactivationPolicy,
        // numberOfFailures within daysInPast. Each attempt was counted when it was claimed, so the statement's own
        // snapshot already holds this one: a delivery that was in the end accepted counts every attempt but its last.
        const string Sql = """
            with failed as (
                update webhook_delivery
                   set status      = case when @retry is null then 'failed' else 'pending' end,
                       next_attempt_at = coalesce(@retry, next_attempt_at),
                       finished_at = case when @retry is null then now() else null end,
                       lease_until = null, last_status = @status, last_error = left(@error, 1024)
                 where id = @id and claimed_by = @worker and status = 'pending'
                returning webhook_id
            ),
            counted as (
                select w.id, w.failures_allowed,
                       (select coalesce(sum(case when d.status = 'delivered' then d.attempts - 1 else d.attempts end), 0)
                          from webhook_delivery d
                         where d.webhook_id = w.id and d.status <> 'dropped'
                           and d.created_at > now() - make_interval(days => w.failure_days)) as failures
                  from webhook w
                 where w.id in (select webhook_id from failed)
            )
            update webhook w
               set active = false, modified_at = now(),
                   deactivated_reason = 'Deactivated after ' || c.failures || ' failed deliveries in '
                                        || w.failure_days || ' days.'
              from counted c
             where w.id = c.id and w.active and c.failures >= c.failures_allowed
            returning w.id
            """;

        Guid? deactivated = null;

        await using (NpgsqlCommand command = _root.CreateCommand(Sql))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("worker", worker);
            command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Integer) { Value = (object?)status ?? DBNull.Value });
            command.Parameters.AddWithValue("error", reason);
            command.Parameters.Add(new NpgsqlParameter("retry", NpgsqlDbType.TimestampTz) { Value = (object?)retryAt ?? DBNull.Value });

            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid hook)
            {
                deactivated = hook;
            }
        }

        if (deactivated is not { } off)
        {
            return false;
        }

        await using NpgsqlCommand drop = _root.CreateCommand(
            "update webhook_delivery set status = 'dropped', finished_at = now(), lease_until = null, "
            + "last_error = 'The webhook was deactivated before this was sent.' where webhook_id = @hook and status = 'pending'");
        drop.Parameters.AddWithValue("hook", off);
        await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc/>
    public async Task DropAsync(Guid id, string worker, string why, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _root.CreateCommand(
            "update webhook_delivery set status = 'dropped', finished_at = now(), lease_until = null, "
            + "last_error = left(@why, 1024) where id = @id and claimed_by = @worker and status = 'pending'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("why", why);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WebhookDeliveryRecord>> RecentAsync(
        Guid webhookId, int count, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select id, event, status, attempts, last_status, last_error, created_at, finished_at "
            + "from webhook_delivery where webhook_id = @hook order by created_at desc, id limit @count");
        command.Parameters.AddWithValue("hook", webhookId);
        command.Parameters.AddWithValue("count", Math.Clamp(count, 1, 200));

        List<WebhookDeliveryRecord> recent = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            recent.Add(new WebhookDeliveryRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return recent;
    }

    /// <inheritdoc/>
    public async Task<Guid?> ServiceIdAsync(string name, string? folder, CancellationToken cancellationToken)
    {
        // The lookup key the catalogue keeps and indexes (migration 116), so this is the read the services are found by.
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select id from service where lookup_key = coalesce(lower(@folder), '') || chr(31) || lower(@name) limit 1");
        command.Parameters.AddWithValue("name", name);
        command.Parameters.Add(new NpgsqlParameter("folder", NpgsqlDbType.Text) { Value = (object?)folder ?? DBNull.Value });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <inheritdoc/>
    public async Task<Guid?> GroupIdAsync(string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select id from sharing_group where lower(name) = lower(@name) limit 1");
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <inheritdoc/>
    public async Task<int> PruneAsync(TimeSpan keep, int perWebhook, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _root.CreateCommand(
            """
            delete from webhook_delivery d
             where d.status <> 'pending'
               and (d.finished_at < now() - @keep
                    or d.id in (select id from (
                                    select id, row_number() over (partition by webhook_id order by created_at desc) as r
                                      from webhook_delivery where status <> 'pending') ranked
                                 where ranked.r > @per))
            """);
        command.Parameters.AddWithValue("keep", keep);
        command.Parameters.AddWithValue("per", perWebhook);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Wire(WebhookKind kind) => kind switch
    {
        WebhookKind.Portal => "portal",
        WebhookKind.FeatureService => "featureService",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a webhook kind."),
    };

    private static WebhookKind KindOf(string wire) => wire switch
    {
        "portal" => WebhookKind.Portal,
        "featureService" => WebhookKind.FeatureService,
        _ => throw new InvalidOperationException($"The store holds a webhook of kind '{wire}', which this build does not know."),
    };

    private static async Task<IReadOnlyList<WebhookDefinition>> ReadAllAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<WebhookDefinition> all = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            all.Add(Read(reader));
        }

        return all;
    }

    private static WebhookDefinition? First(IReadOnlyList<WebhookDefinition> all) => all.Count == 0 ? null : all[0];

    private static WebhookDefinition Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        KindOf(reader.GetString(1)),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetFieldValue<string[]>(6),
        reader.GetBoolean(7),
        reader.GetBoolean(8),
        reader.GetGuid(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetGuid(11),
        reader.GetString(12),
        reader.GetString(13),
        reader.GetString(14),
        reader.GetInt32(15),
        reader.GetInt32(16),
        reader.GetInt32(17),
        reader.GetString(18),
        reader.IsDBNull(19) ? null : reader.GetString(19),
        reader.GetFieldValue<DateTimeOffset>(20),
        reader.GetFieldValue<DateTimeOffset>(21));
}
