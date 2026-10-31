using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Secrets;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-204 condition 11: every statement that runs per request keeps its index under the floor — read from the plan
/// of the store's own statement, as a floored role over a catalogue at the scale target, beside the plan of the same
/// call made by a role the floor does not bind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a plan and not a timing.</b> <c>FindServiceAsync</c> lost its index under the floor in the prototype and
/// nothing about it looked wrong from outside: PostgreSQL may not evaluate a function that is not leakproof — such as
/// <c>lower</c> — on a column before the policy has filtered the row, so a predicate written over one cannot be an
/// index condition, and the lookup becomes a scan of every service. At a thousand services that is a fraction of a
/// millisecond nobody notices; it is still every row on every request.
/// </para>
/// <para>
/// <b>The store's own statements, not copies.</b> The calls are the stores' methods; the plans are PostgreSQL's
/// <c>auto_explain</c>, loaded for the two roles this test makes and sent to the client as notices, which Npgsql
/// logs. A copy of the SQL here would go on passing after the store's statement changed.
/// </para>
/// <para>
/// <b>What fails:</b> a sequential scan, in a plan node that ran, of a floored table holding at least the scale target's
/// thousand rows, under the floor where the same call without it did not scan that table. A listing that reads most of a
/// table scans it either way, and that is not a lost index; a table of a few dozen rows — groups, data sources — is a
/// page or two read whichever way, and is reported rather than failed.
/// </para>
/// <para>
/// <b>Each statement on the pool the product runs it on.</b> A session is found on the store's root pool, which names
/// no organisation (ADR-220 §5.6), so <c>FindSessionAsync</c> runs as the floored role with no tenant; the rest on an
/// organisation's pool. A layer found by name (<c>FindAsync</c>) is an administrative act, not a request's, and is not
/// here; neither are writes.
/// </para>
/// <para>
/// <b>The control</b> is a statement written the way <c>FindServiceAsync</c> was in the prototype, through the same
/// harness: it must be caught, or the zero below proves nothing.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EveryPerRequestStatementKeepsItsIndexUnderTheFloorTests(ITestOutputHelper output) : PostgresFixture
{
    private static readonly Guid DefaultTenant = new("10000000-0000-0000-0000-000000000001");

    private static readonly Regex SeqScan = new(@"Seq Scan on (?:[A-Za-z0-9_]+\.)?([A-Za-z0-9_]+)", RegexOptions.Compiled);

    [Fact]
    public async Task No_per_request_statement_scans_a_floored_table_it_reaches_by_index_without_the_floor()
    {
        await MigrateAsync();

        SecretProtector secrets = new(1, new byte[32]);
        Guid source = await new PostgresAdminCatalog(DataSource, secrets).RegisterDataSourceAsync(
            "plan-source", "postgis", "Host=nowhere;Database=none", CancellationToken.None);

        Guid jv = Guid.NewGuid();
        await SeedAsync(source, jv);

        string run = Guid.NewGuid().ToString("n")[..8];
        string floored = $"zz_floored_{run}", unbound = $"zz_unbound_{run}";
        const string Password = "zz-plan-roles";

        await ExecuteAsync(DataSource, $"create role {floored} login nosuperuser nobypassrls password '{Password}'");
        await ExecuteAsync(DataSource, $"create role {unbound} login nosuperuser bypassrls password '{Password}'");

        try
        {
            foreach (string role in new[] { floored, unbound })
            {
                await ExecuteAsync(DataSource, $"grant usage on schema \"{SchemaName}\", public to {role}");
                await ExecuteAsync(DataSource, $"grant select on all tables in schema \"{SchemaName}\" to {role}");
                await ExecuteAsync(DataSource, $"alter role {role} set session_preload_libraries = 'auto_explain'");
                await ExecuteAsync(DataSource, $"alter role {role} set auto_explain.log_min_duration = 0");
                await ExecuteAsync(DataSource, $"alter role {role} set auto_explain.log_nested_statements = on");
                await ExecuteAsync(DataSource, $"alter role {role} set auto_explain.log_analyze = on");
                await ExecuteAsync(DataSource, $"alter role {role} set client_min_messages = 'log'");
            }

            HashSet<string> flooredTables = [];
            await using (NpgsqlCommand policies = DataSource.CreateCommand(
                "select tablename from pg_policies where schemaname = current_schema() and policyname = 'tenant_floor'"))
            await using (NpgsqlDataReader reader = await policies.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    flooredTables.Add(reader.GetString(0));
                }
            }

            Assert.Contains("service", flooredTables);

            // The tables at the scale target: a scan of one of these is every row on every request.
            HashSet<string> large = [];
            await using (NpgsqlCommand sizes = DataSource.CreateCommand(
                "select c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace "
                + "where n.nspname = current_schema() and c.relkind = 'r' and c.reltuples >= 1000"))
            await using (NpgsqlDataReader reader = await sizes.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    large.Add(reader.GetString(0));
                }
            }

            Assert.Contains("service", large);

            (string Service, string Layer, Guid LayerId, Guid Principal, byte[] Token)[] probes =
            [
                await ProbeAsync(DefaultTenant, "svc_0007"),
                await ProbeAsync(jv, "svc_0957"),
            ];

            List<string> lost = [];
            bool controlCaught = false;

            foreach ((Guid tenant, var probe) in new[] { (DefaultTenant, probes[0]), (jv, probes[1]) })
            {
                Notices underFloor = new(), atRoot = new(), without = new();
                await using NpgsqlDataSource floor = Source(floored, Password, tenant, underFloor);
                await using NpgsqlDataSource root = Source(floored, Password, null, atRoot);
                await using NpgsqlDataSource free = Source(unbound, Password, null, without);

                (string Name, bool OnRoot, Func<NpgsqlDataSource, Task> Call)[] statements =
                [
                    ("FindServiceAsync", false, ds => new PostgresLayerCatalog(ds, secrets).FindServiceAsync("hosted", probe.Service, CancellationToken.None)),
                    ("FindByIdAsync (layer by id)", false, ds => new PostgresLayerCatalog(ds, secrets).FindByIdAsync(probe.LayerId, CancellationToken.None)),
                    ("FindSessionAsync", true, ds => new PostgresIdentityStore(ds).FindSessionAsync(probe.Token, DateTimeOffset.UtcNow, CancellationToken.None)),
                    ("GrantsOfAsync", false, ds => new PostgresIdentityStore(ds).GrantsOfAsync(probe.Principal, CancellationToken.None)),
                    ("ListServicesAsync", false, ds => new PostgresLayerCatalog(ds, secrets).ListServicesAsync(CancellationToken.None)),
                    ("control: lower(name) as the prototype wrote it", false, ds => ControlAsync(ds, probe.Service)),
                ];

                foreach ((string name, bool onRoot, Func<NpgsqlDataSource, Task> call) in statements)
                {
                    string[] plain = await PlansAsync(free, without, call);
                    string[] floor2 = onRoot ? await PlansAsync(root, atRoot, call) : await PlansAsync(floor, underFloor, call);
                    Assert.True(plain.Length > 0 && floor2.Length > 0,
                        $"{name}: auto_explain sent no plan ({plain.Length} without the floor, {floor2.Length} under it); the harness is not measuring.");

                    HashSet<string> scannedFree = Scanned(plain);
                    string[] changed = [.. Scanned(floor2).Where(t => flooredTables.Contains(t) && !scannedFree.Contains(t)).Order()];
                    string[] lostHere = [.. changed.Where(large.Contains)];

                    output.WriteLine($"{(tenant == DefaultTenant ? "default" : "jv")} | {name} | scans without: [{string.Join(", ", scannedFree.Order())}] | newly scanned under the floor: [{string.Join(", ", changed)}] | of those at the scale target: [{string.Join(", ", lostHere)}]");

                    if (name.StartsWith("control", StringComparison.Ordinal))
                    {
                        controlCaught |= lostHere.Contains("service");
                    }
                    else if (lostHere.Length > 0)
                    {
                        lost.Add($"{name} as {(tenant == DefaultTenant ? "the default organisation" : "a second")}: {string.Join(", ", lostHere)}\n{string.Join("\n", floor2)}");
                    }
                }
            }

            Assert.True(controlCaught,
                "The control — lower(s.name) = lower(@name) under the floor, which scanned every service in the prototype — was not "
                + "caught, so a clean result from this harness proves nothing.");
            Assert.True(lost.Count == 0, "Per-request statements that scan a floored table they reach by index without the floor:\n"
                + string.Join("\n\n", lost));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();

            foreach (string role in new[] { floored, unbound })
            {
                await ExecuteAsync(DataSource, $"drop owned by {role}");
                await ExecuteAsync(DataSource, $"drop role if exists {role}");
            }
        }
    }

    /// <summary>A lookup the way the prototype's <c>FindServiceAsync</c> wrote it, which the floor turned into a scan.</summary>
    private static async Task ControlAsync(NpgsqlDataSource dataSource, string name)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "select s.id from service s where coalesce(lower(s.folder), '') = 'hosted' and lower(s.name) = lower(@name)");
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteScalarAsync();
    }

    /// <summary>The tables a plan scanned sequentially in nodes that ran — <c>log_analyze</c> marks the others.</summary>
    private static HashSet<string> Scanned(IEnumerable<string> plans) =>
        [.. plans.SelectMany(p => p.Split('\n'))
            .Where(line => !line.Contains("(never executed)", StringComparison.Ordinal))
            .SelectMany(line => SeqScan.Matches(line).Select(m => m.Groups[1].Value))];

    private static async Task<string[]> PlansAsync(NpgsqlDataSource dataSource, Notices notices, Func<NpgsqlDataSource, Task> call)
    {
        notices.Queue.Clear();
        await call(dataSource);
        return [.. notices.Queue.Where(l => l.Contains("plan:", StringComparison.Ordinal))];
    }

    private NpgsqlDataSource Source(string role, string password, Guid? tenant, Notices notices)
    {
        NpgsqlConnectionStringBuilder builder = new(DataSource.ConnectionString)
        {
            Username = role,
            Password = password,
            SearchPath = $"{SchemaName},public",
            Pooling = false,
        };

        if (tenant is { } t)
        {
            builder.Options = $"-c graticula.tenant={t:D}";
        }

        NpgsqlDataSourceBuilder made = new(builder.ConnectionString);
        made.UseLoggerFactory(notices);
        return made.Build();
    }

    /// <summary>
    /// The scale target of ADR-204 condition 1: a thousand services with three layers each, the default organisation
    /// holding 950 and a second 50; eleven hundred members with a session each; seventy groups, every member in two,
    /// some of the default organisation's in a group of the second that admits them; every item shared into a group.
    /// </summary>
    private async Task SeedAsync(Guid source, Guid jv)
    {
        await ExecuteAsync(DataSource, $"""
            insert into tenant (id, key, name, portal_id) values ('{jv}', 'plan{jv.ToString("n")[..8]}', 'Plan JV', 'ABCDEF0123456789');

            insert into data_source (id, name, kind, connection_secret, key_version, tenant_id)
              select gen_random_uuid(), 'plan-source-jv', kind, connection_secret, key_version, '{jv}' from data_source where id = '{source}';

            insert into principal (id, kind, name, tenant_id)
              select gen_random_uuid(), 'user', 'member' || i, case when i <= 1000 then '{DefaultTenant}'::uuid else '{jv}'::uuid end
                from generate_series(1, 1100) i;

            insert into principal_role (principal_id, role_name, tenant_id) select id, 'user', tenant_id from principal;

            insert into session (id, principal_id, token_hash, expires_at)
              select gen_random_uuid(), id, sha256(convert_to(name || '{jv}', 'UTF8')), now() + interval '1 day' from principal;

            insert into service (id, name, folder, kind, tenant_id)
              select gen_random_uuid(), 'svc_' || lpad(i::text, 4, '0'), 'hosted', 'FeatureServer',
                     case when i <= 950 then '{DefaultTenant}'::uuid else '{jv}'::uuid end
                from generate_series(1, 1000) i;

            insert into item (id, kind, title, owner_principal_id, sharing, service_id, tenant_id)
              select s.id, 'service', s.name,
                     (select p.id from principal p where p.tenant_id = s.tenant_id order by p.name limit 1),
                     'organization', s.id, s.tenant_id
                from service s;

            insert into layer (id, name, data_source_id, schema_name, table_name, srid, identity_column, service_id, layer_index, tenant_id)
              select gen_random_uuid(), s.name || '_l' || k,
                     (select d.id from data_source d where d.tenant_id = s.tenant_id limit 1),
                     'hosted', s.name || '_l' || k, 4326, 'objectid', s.id, k, s.tenant_id
                from service s cross join generate_series(0, 2) k;

            insert into sharing_group (id, name, owner_principal_id, admits_other_tenants, tenant_id)
              select gen_random_uuid(), 'group' || i,
                     (select p.id from principal p where p.tenant_id = t.id order by p.name limit 1),
                     i > 65, t.id
                from generate_series(1, 70) i
                cross join lateral (select case when i <= 60 then '{DefaultTenant}'::uuid else '{jv}'::uuid end as id) t;

            insert into sharing_group_member (group_id, principal_id, tenant_id)
              select distinct on (g.id, p.id) g.id, p.id, g.tenant_id
                from principal p
                cross join generate_series(0, 1) k
                join lateral (select g.* from sharing_group g where g.tenant_id = p.tenant_id
                               order by g.name offset (abs(hashtext(p.name || k)) % 10) limit 1) g on true;

            insert into sharing_group_member (group_id, principal_id, tenant_id)
              select g.id, p.id, g.tenant_id
                from sharing_group g
                join principal p on p.tenant_id = '{DefaultTenant}' and p.name like 'member1%'
               where g.admits_other_tenants
              on conflict do nothing;

            insert into item_group (group_id, item_id, tenant_id)
              select (select g.id from sharing_group g where g.tenant_id = i.tenant_id
                       order by g.name offset (abs(hashtext(i.title)) % 10) limit 1), i.id, i.tenant_id
                from item i;

            analyze;
            """);
    }

    private async Task<(string Service, string Layer, Guid LayerId, Guid Principal, byte[] Token)> ProbeAsync(Guid tenant, string service)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select l.name, l.id, p.id, x.token_hash from service s join layer l on l.service_id = s.id and l.layer_index = 1 "
            + "join principal p on p.tenant_id = s.tenant_id join session x on x.principal_id = p.id "
            + "where s.name = @name and s.tenant_id = @tenant order by p.name limit 1");
        command.Parameters.AddWithValue("name", service);
        command.Parameters.AddWithValue("tenant", tenant);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"The seed holds no {service} for {tenant}.");
        return (service, reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetFieldValue<byte[]>(3));
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Keeps what Npgsql logs of the notices the server sends — <c>auto_explain</c>'s plans among them.</summary>
    private sealed class Notices : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<string> Queue { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Queue.Enqueue(formatter(state, exception));
        }
    }
}
