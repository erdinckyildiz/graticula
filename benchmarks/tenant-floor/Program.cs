// ADR-204 condition 1 — the tenant floor, measured. Disposable: this is an experiment, never promoted.
//
//   setup    a fresh database owned by tf_app, which is NOT a superuser, and the shipped migrations applied
//            through the product's own SchemaMigrator
//   seed     ten tenants and 1,000 services (the scale target), groups, three that admit other tenants;
//            the tenant columns and the floor's policies, which is the prototype itself
//   check    what each tenant sees, against an answer computed without the floor; fail-closed; superuser
//            and owner bypass; the pool carrying a setting from one request into the next, with a control
//   time     the real ListServicesAsync and three list reads, three ways, interleaved

using System.Diagnostics;
using Graticula.Platform.Postgres;
using Graticula.Platform.Schema;
using Graticula.Platform.Secrets;
using Npgsql;

string super = args.Length > 0 ? args[0] : "Host=localhost;Port=55499;Username=gis;Password=gis;Database=gistest";
string stage = args.Length > 1 ? args[1] : "setup";

const string Db = "tenantfloor";
const string App = "tf_app";        // owns the schema; the floor is FORCEd on it
const string Bypass = "tf_bypass";  // BYPASSRLS: "today", and the control for Alternative C

string Cs(string role, string? tenant = null, int pool = 24, bool reset = true)
{
    NpgsqlConnectionStringBuilder b = new(super)
    {
        Database = Db, Username = role, Password = "tf", SearchPath = "gisserver",
        MaxPoolSize = pool, NoResetOnClose = !reset,
    };
    if (tenant is not null) b.Options = "-c graticula.tenant=" + tenant;
    return b.ConnectionString;
}

string Su(string? tenant = null)
{
    NpgsqlConnectionStringBuilder b = new(super) { Database = Db, SearchPath = "gisserver" };
    if (tenant is not null) b.Options = "-c graticula.tenant=" + tenant;
    return b.ConnectionString;
}

async Task Exec(string cs, string sql)
{
    await using NpgsqlConnection c = new(cs);
    await c.OpenAsync();
    await using NpgsqlCommand cmd = new(sql, c) { CommandTimeout = 600 };
    await cmd.ExecuteNonQueryAsync();
}

async Task<long> Count(NpgsqlDataSource ds, string sql)
{
    await using NpgsqlCommand cmd = ds.CreateCommand(sql);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

async Task<string?> Scalar(NpgsqlConnection c, string sql)
{
    await using NpgsqlCommand cmd = new(sql, c);
    object? o = await cmd.ExecuteScalarAsync();
    return o is null or DBNull ? null : o.ToString();
}

string[] Tenants = ["default", "jv1", "jv2", "jv3", "jv4", "jv5", "jv6", "jv7", "jv8", "jv9"];
string[] Floored = ["service", "item", "layer", "group_layer", "principal", "sharing_group", "sharing_group_member", "item_group"];

if (stage == "setup")
{
    await Exec(super, $"drop database if exists {Db} with (force)");
    foreach (string role in new[] { App, Bypass })
        await Exec(super, $"do $$ begin if not exists (select from pg_roles where rolname = '{role}') then create role {role} login nosuperuser password 'tf'; end if; end $$");
    await Exec(super, $"alter role {Bypass} bypassrls");
    await Exec(super, $"create database {Db} owner {App}");
    NpgsqlConnectionStringBuilder su = new(super) { Database = Db };
    await Exec(su.ConnectionString, "create extension if not exists postgis");
    await Exec(su.ConnectionString, $"create schema gisserver authorization {App}");

    await using NpgsqlDataSource ds = NpgsqlDataSource.Create(Cs(App));
    var report = await new SchemaMigrator(new PostgresPlatformSchemaStore(ds), PlatformMigrations.All)
        .ApplyAsync(CancellationToken.None);
    Console.WriteLine($"migrated to {report.To}");
    return;
}

if (stage == "seed")
{
    string app = Cs(App);
    // The prototype's schema change: a column, an index, a flag, and the reach table a trigger would keep.
    foreach (string t in Floored)
        await Exec(app, $"alter table {t} add column tenant_id text not null default 'default'; create index {t}_by_tenant on {t} (tenant_id)");
    await Exec(app, """
        alter table sharing_group add column admits_other_tenants boolean not null default false;
        create table tenant_group_reach (tenant text not null, group_id uuid not null references sharing_group (id) on delete cascade,
                                         primary key (tenant, group_id));
        update principal set tenant_id = '*' where name = 'anonymous';
        insert into data_source (id, name, kind, connection_secret, key_version, is_datastore)
          values (gen_random_uuid(), 'datastore', 'postgis', '\x00', 1, true);
        """);

    // Ten tenants, 1,000 services: the default holds 550, each JV 50. Twenty members each, five groups each.
    await Exec(app, """
        create temp table t (tenant text, services int);
        insert into t values ('default', 550), ('jv1', 50), ('jv2', 50), ('jv3', 50), ('jv4', 50),
                             ('jv5', 50), ('jv6', 50), ('jv7', 50), ('jv8', 50), ('jv9', 50);

        insert into principal (id, kind, name, user_type, tenant_id)
          select gen_random_uuid(), 'user', t.tenant || '_u' || n, 'creator', t.tenant
            from t, generate_series(1, 20) n;

        insert into service (id, name, folder, kind, tenant_id)
          select gen_random_uuid(), t.tenant || '_s' || n, null, 'FeatureServer', t.tenant
            from t, generate_series(1, t.services) n;

        insert into item (id, kind, title, owner_principal_id, sharing, service_id, tenant_id)
          select s.id, 'service', s.name,
                 (select p.id from principal p where p.tenant_id = s.tenant_id
                   order by md5(p.id::text || s.id::text) limit 1),
                 (array['private','organization','public','group'])[1 + (abs(hashtext(s.name)) % 4)],
                 s.id, s.tenant_id
            from service s;

        insert into layer (id, name, data_source_id, schema_name, table_name, geometry_column, srid, identity_column,
                           object_id_column, geometry_type, service_id, layer_index, tenant_id)
          select gen_random_uuid(), s.name || '_l' || n, d.id, 'hosted', s.name || '_l' || n, 'geom', 4326, 'id', 'id',
                 'Polygon', s.id, n, s.tenant_id
            from service s, data_source d, generate_series(0, 2) n;
        update service set next_layer_index = 3;

        insert into group_layer (id, service_id, layer_index, name, tenant_id)
          select gen_random_uuid(), s.id, 3, 'Grup', s.tenant_id from service s;

        insert into sharing_group (id, name, owner_principal_id, tenant_id)
          select gen_random_uuid(), t.tenant || '_g' || n,
                 (select id from principal p where p.tenant_id = t.tenant order by p.name limit 1), t.tenant
            from t, generate_series(1, 5) n;

        insert into sharing_group_member (group_id, principal_id, membership, tenant_id)
          select g.id, p.id, 'member', g.tenant_id
            from sharing_group g join principal p on p.tenant_id = g.tenant_id
           where right(p.name, 1) in ('1','2','3','4','5');

        insert into item_group (group_id, item_id, tenant_id)
          select g.id, i.id, g.tenant_id
            from sharing_group g join item i on i.tenant_id = g.tenant_id and i.sharing = 'group'
           where right(g.name, 1) = '1';
        """);

    // Three groups that admit other tenants: default+jv1+jv2, default+jv3, jv4+jv5 (owned by jv4).
    await Exec(app, """
        insert into sharing_group (id, name, owner_principal_id, tenant_id, admits_other_tenants)
          select gen_random_uuid(), x.name, (select id from principal where name = x.owner), x.tenant, true
            from (values ('cross_a', 'default_u1', 'default'), ('cross_b', 'default_u1', 'default'),
                         ('cross_c', 'jv4_u1', 'jv4')) x(name, owner, tenant);

        insert into sharing_group_member (group_id, principal_id, membership, tenant_id)
          select g.id, p.id, 'member', p.tenant_id
            from sharing_group g join principal p
              on (g.name = 'cross_a' and p.tenant_id in ('default','jv1','jv2'))
              or (g.name = 'cross_b' and p.tenant_id in ('default','jv3'))
              or (g.name = 'cross_c' and p.tenant_id in ('jv4','jv5'))
           where right(p.name, 2) in ('u1', 'u2', 'u3');

        -- Each member tenant puts some of its own into the group: 20 from the owner, 5 from each guest.
        insert into item_group (group_id, item_id, tenant_id)
          select g.id, i.id, i.tenant_id
            from sharing_group g
            join lateral (select distinct p.tenant_id from sharing_group_member m join principal p on p.id = m.principal_id
                           where m.group_id = g.id) mt on true
            join lateral (select i.id, i.tenant_id from item i where i.tenant_id = mt.tenant_id
                           order by md5(i.id::text || g.name)
                           limit case when mt.tenant_id = g.tenant_id then 20 else 5 end) i on true
           where g.admits_other_tenants
          on conflict do nothing;

        insert into tenant_group_reach (tenant, group_id)
          select distinct p.tenant_id, m.group_id
            from sharing_group_member m join principal p on p.id = m.principal_id
            join sharing_group g on g.id = m.group_id
           where g.admits_other_tenants or p.tenant_id = g.tenant_id;
        """);

    // The floor. Each table admits its own tenant's rows, and what a group that admits this tenant reaches.
    // Nothing consults principal from a policy that principal's own policy depends on, so there is no cycle.
    const string Cur = "current_setting('graticula.tenant', true)";
    await Exec(app, $"""
        create policy floor on sharing_group using (
          tenant_id = {Cur}
          or exists (select 1 from tenant_group_reach r where r.group_id = sharing_group.id and r.tenant = {Cur}));
        create policy floor on sharing_group_member using (
          exists (select 1 from sharing_group g where g.id = sharing_group_member.group_id));
        create policy floor on item_group using (
          exists (select 1 from sharing_group g where g.id = item_group.group_id));
        create policy floor on item using (
          tenant_id = {Cur}
          or exists (select 1 from item_group ig join tenant_group_reach r on r.group_id = ig.group_id
                      where ig.item_id = item.id and r.tenant = {Cur}));
        create policy floor on service using (
          tenant_id = {Cur} or exists (select 1 from item i where i.service_id = service.id));
        create policy floor on layer using (
          tenant_id = {Cur} or exists (select 1 from service s where s.id = layer.service_id));
        create policy floor on group_layer using (
          tenant_id = {Cur} or exists (select 1 from service s where s.id = group_layer.service_id));
        create policy floor on principal using (
          tenant_id in ({Cur}, '*')
          or exists (select 1 from sharing_group_member m where m.principal_id = principal.id));
        """);
    foreach (string t in Floored)
        await Exec(app, $"alter table {t} enable row level security; alter table {t} force row level security");
    await Exec(app, $"grant usage on schema gisserver to {Bypass}; grant select on all tables in schema gisserver to {Bypass}");
    await Exec(app, "analyze");
    Console.WriteLine("seeded");
    return;
}

// The first floor nests: layer asks service, service asks item, item asks item_group, item_group asks
// sharing_group — each under its own policy, per row, and ListServicesAsync asks layer four times a row.
// "flatten" replaces it with policies that consult only reach tables nothing floors, so each is one index
// probe. The reach tables are what a trigger on sharing_group_member and item_group would keep.
if (stage == "flatten")
{
    string app = Cs(App);
    const string Cur = "current_setting('graticula.tenant', true)";
    foreach (string t in Floored) await Exec(app, $"drop policy if exists floor on {t}");
    await Exec(app, """
        drop table if exists tenant_item_reach, tenant_principal_reach;
        create table tenant_item_reach (tenant text not null, item_id uuid not null, primary key (tenant, item_id));
        create table tenant_principal_reach (tenant text not null, principal_id uuid not null, primary key (tenant, principal_id));
        """);
    // Filled by a connection the floor does not bind. Filled as tf_app, with the old policies dropped and RLS
    // still forced, both tables came out EMPTY and every tenant lost what was shared with it: whatever keeps
    // these tables (a trigger, in the product) must run as a role that sees across tenants.
    await Exec(Su(), """
        insert into tenant_item_reach
          select distinct r.tenant, ig.item_id from item_group ig join tenant_group_reach r on r.group_id = ig.group_id;
        insert into tenant_principal_reach
          select distinct r.tenant, m.principal_id from sharing_group_member m join tenant_group_reach r on r.group_id = m.group_id;
        """);
    await Exec(app, $"""
        create policy floor on sharing_group using (tenant_id = {Cur}
          or exists (select 1 from tenant_group_reach r where r.tenant = {Cur} and r.group_id = sharing_group.id));
        create policy floor on sharing_group_member using (tenant_id = {Cur}
          or exists (select 1 from tenant_group_reach r where r.tenant = {Cur} and r.group_id = sharing_group_member.group_id));
        create policy floor on item_group using (tenant_id = {Cur}
          or exists (select 1 from tenant_group_reach r where r.tenant = {Cur} and r.group_id = item_group.group_id));
        create policy floor on item using (tenant_id = {Cur}
          or exists (select 1 from tenant_item_reach r where r.tenant = {Cur} and r.item_id = item.id));
        create policy floor on service using (tenant_id = {Cur}
          or exists (select 1 from tenant_item_reach r where r.tenant = {Cur} and r.item_id = service.id));
        create policy floor on layer using (tenant_id = {Cur}
          or exists (select 1 from tenant_item_reach r where r.tenant = {Cur} and r.item_id = layer.service_id));
        create policy floor on group_layer using (tenant_id = {Cur}
          or exists (select 1 from tenant_item_reach r where r.tenant = {Cur} and r.item_id = group_layer.service_id));
        create policy floor on principal using (tenant_id in ({Cur}, '*')
          or exists (select 1 from tenant_principal_reach r where r.tenant = {Cur} and r.principal_id = principal.id));
        """);
    // Under a policy, a predicate that applies a non-leakproof function to a column - lower(name) - cannot
    // be pushed into an index scan, so the per-request lookup scanned every service. A stored key compared
    // with = is leakproof and keeps the index (measured: seq scan of 1,000 rows before, index scan after).
    await Exec(app, """
        alter table service add column if not exists lookup_key text
          generated always as (coalesce(lower(folder), '') || chr(31) || lower(name)) stored;
        create unique index if not exists service_lookup_key on service (tenant_id, lookup_key);
        """);
    await Exec(app, $"grant select on all tables in schema gisserver to {Bypass}");
    await Exec(app, "analyze");
    Console.WriteLine("flattened");
    return;
}

// What a tenant may see, computed without the floor, from the same facts: its own rows, and the items in
// groups that admit it.
string Expected(string tenant) => $"""
    select count(*) from service s
     where s.tenant_id = '{tenant}'
        or exists (select 1 from item_group ig join tenant_group_reach r on r.group_id = ig.group_id
                    where ig.item_id = s.id and r.tenant = '{tenant}')
    """;

if (stage == "check")
{
    int failures = 0;
    void Expect(bool ok, string what) { Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what); if (!ok) failures++; }

    await using NpgsqlDataSource bypass = NpgsqlDataSource.Create(Cs(Bypass));
    Console.WriteLine("Isolation — services each tenant sees under the floor, against the answer without it:");
    foreach (string tenant in Tenants)
    {
        await using NpgsqlDataSource ds = NpgsqlDataSource.Create(Cs(App, tenant));
        long seen = await Count(ds, "select count(*) from service");
        long expected = await Count(bypass, Expected(tenant));
        long foreign = await Count(ds, $"""
            select count(*) from service s where s.tenant_id <> '{tenant}'
               and not exists (select 1 from item_group ig join tenant_group_reach r on r.group_id = ig.group_id
                                where ig.item_id = s.id and r.tenant = '{tenant}')
            """);
        long layers = await Count(ds, "select count(*) from layer");
        long people = await Count(ds, $"select count(*) from principal where tenant_id not in ('{tenant}', '*')");
        long groups = await Count(ds, $"select count(*) from sharing_group where tenant_id <> '{tenant}'");
        Expect(seen == expected && foreign == 0 && layers == 3 * seen,
            $"{tenant,-8} services {seen,4} (expected {expected,4}), unreachable foreign {foreign}, layers {layers,4}, "
            + $"other tenants' members {people,2}, other tenants' groups {groups}");
    }

    Console.WriteLine("Fail-closed — no tenant on the connection:");
    await using (NpgsqlDataSource none = NpgsqlDataSource.Create(Cs(App)))
        Expect(await Count(none, "select count(*) from service") == 0
               && await Count(none, "select count(*) from principal where name <> 'anonymous'") == 0,
            "a connection with no graticula.tenant reads no services and no members");

    Console.WriteLine("Who the floor does not bind:");
    await using (NpgsqlDataSource su = NpgsqlDataSource.Create(Su("jv6")))
    {
        long n = await Count(su, "select count(*) from service");
        Console.WriteLine($"  note  a SUPERUSER connection with tenant jv6 reads {n} services — the floor does not apply, FORCE or not");
    }
    await Exec(Cs(App), "alter table service no force row level security");
    await using (NpgsqlDataSource owner = NpgsqlDataSource.Create(Cs(App, "jv6")))
    {
        long n = await Count(owner, "select count(*) from service");
        Console.WriteLine($"  note  the OWNER without FORCE, tenant jv6, reads {n} services");
    }
    await Exec(Cs(App), "alter table service force row level security");
    await using (NpgsqlDataSource owner = NpgsqlDataSource.Create(Cs(App, "jv6")))
        Expect(await Count(owner, "select count(*) from service") == 50, "the owner WITH FORCE, tenant jv6, reads its 50");

    Console.WriteLine("The pool — a request that changes the setting, and the next request on the same connection:");
    foreach (bool reset in new[] { true, false })
    {
        await using NpgsqlDataSource ds = NpgsqlDataSource.Create(Cs(App, "jv1", pool: 1, reset: reset));
        int leaked = 0, rounds = 2000;
        for (int i = 0; i < rounds; i++)
        {
            await using (NpgsqlConnection c = await ds.OpenConnectionAsync())
            {
                switch (i % 4)
                {
                    case 0: await Scalar(c, "select set_config('graticula.tenant', 'jv2', false)"); break;
                    case 1: await Scalar(c, "set graticula.tenant = 'jv2'"); break;
                    case 2:
                        await using (NpgsqlTransaction tx = await c.BeginTransactionAsync())
                        {
                            await Scalar(c, "set graticula.tenant = 'jv2'");
                            await tx.CommitAsync();
                        }
                        break;
                    case 3:
                        await using (NpgsqlTransaction tx = await c.BeginTransactionAsync())
                        {
                            await Scalar(c, "set graticula.tenant = 'jv2'");
                            try { await Scalar(c, "select 1/0"); } catch (PostgresException) { }
                            // abandoned without rollback: the connection goes back to the pool mid-failure
                        }
                        break;
                }
            }
            await using (NpgsqlConnection c = await ds.OpenConnectionAsync())
                if (await Scalar(c, "select current_setting('graticula.tenant', true)") != "jv1") leaked++;
        }
        if (reset) Expect(leaked == 0, $"reset on close (the default): {leaked} of {rounds} next requests saw another tenant");
        else Expect(leaked > 0, $"CONTROL, reset off: {leaked} of {rounds} next requests saw another tenant — the test can see a leak");
    }

    Console.WriteLine("Two tenants' pools under concurrency, 16 callers, 4 connections each:");
    {
        await using NpgsqlDataSource a = NpgsqlDataSource.Create(Cs(App, "jv1", pool: 4));
        await using NpgsqlDataSource b = NpgsqlDataSource.Create(Cs(App, "jv2", pool: 4));
        long wrong = 0;
        long jv1 = await Count(bypass, Expected("jv1")), jv2 = await Count(bypass, Expected("jv2"));
        await Parallel.ForEachAsync(Enumerable.Range(0, 16), async (k, _) =>
        {
            NpgsqlDataSource ds = k % 2 == 0 ? a : b;
            long want = k % 2 == 0 ? jv1 : jv2;
            for (int i = 0; i < 300; i++)
                if (await Count(ds, "select count(*) from service") != want) Interlocked.Increment(ref wrong);
        });
        Expect(wrong == 0, $"{16 * 300} reads, {wrong} answered with another tenant's catalogue");
    }

    Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} FAILED");
    return;
}

if (stage == "time")
{
    int rounds = args.Length > 2 ? int.Parse(args[2]) : 400;
    SecretProtector secrets = new(1, new byte[32]);

    // Alternative C's control: the same predicate the floor applies, written into the statement.
    static string Where(string t) => $"""
        (s.tenant_id = '{t}' or exists (select 1 from item_group ig join tenant_group_reach r on r.group_id = ig.group_id
                                         where ig.item_id = s.id and r.tenant = '{t}'))
        """;
    const string ListSql = """
        select s.id, s.name, s.folder, s.kind, i.sharing, s.status, i.description, p.name,
               (select count(*) from layer l where l.service_id = s.id),
               (select count(*) from group_layer g where g.service_id = s.id),
               (select l.name from layer l where l.service_id = s.id order by l.layer_index limit 1),
               (select l.layer_index from layer l where l.service_id = s.id order by l.layer_index limit 1),
               s.updated_at, s.created_at
        from service s
        join item i on i.service_id = s.id
        left join principal p on p.id = i.owner_principal_id
        {WHERE}
        order by coalesce(s.folder, ''), lower(s.name)
        """;

    async Task<int> Rows(NpgsqlDataSource ds, string sql)
    {
        await using NpgsqlCommand cmd = ds.CreateCommand(sql);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        int n = 0;
        while (await r.ReadAsync()) n++;
        return n;
    }

    foreach (string tenant in new[] { "default", "jv1" })
    {
        await using NpgsqlDataSource today = NpgsqlDataSource.Create(Cs(Bypass));
        await using NpgsqlDataSource floor = NpgsqlDataSource.Create(Cs(App, tenant));
        PostgresAdminCatalog todayCatalog = new(today, secrets), floorCatalog = new(floor, secrets);

        var reads = new (string Name, Func<Task<int>> Today, Func<Task<int>> AppWhere, Func<Task<int>> Floor)[]
        {
            ("ListServicesAsync",
                async () => (await todayCatalog.ListServicesAsync(CancellationToken.None)).Count,
                () => Rows(today, ListSql.Replace("{WHERE}", "where " + Where(tenant))),
                async () => (await floorCatalog.ListServicesAsync(CancellationToken.None)).Count),
            ("items",
                () => Rows(today, "select id, title, sharing, owner_principal_id from item"),
                () => Rows(today, "select s.id, s.title, s.sharing, s.owner_principal_id from item s where " + Where(tenant)),
                () => Rows(floor, "select id, title, sharing, owner_principal_id from item")),
            ("members",
                () => Rows(today, "select id, name from principal"),
                () => Rows(today, $"select id, name from principal p where p.tenant_id in ('{tenant}','*') or exists (select 1 from sharing_group_member m join tenant_group_reach r on r.group_id = m.group_id where m.principal_id = p.id and r.tenant = '{tenant}')"),
                () => Rows(floor, "select id, name from principal")),
            ("groups",
                () => Rows(today, "select id, name from sharing_group"),
                () => Rows(today, $"select id, name from sharing_group g where g.tenant_id = '{tenant}' or exists (select 1 from tenant_group_reach r where r.group_id = g.id and r.tenant = '{tenant}')"),
                () => Rows(floor, "select id, name from sharing_group")),
        };

        Console.WriteLine($"tenant {tenant} — {rounds} interleaved rounds, one caller, ms");
        Console.WriteLine($"  {"read",-18} {"rows today/C/floor",-20} {"today p50/p95",-16} {"C p50/p95",-16} {"floor p50/p95",-16} floor÷C p95");
        foreach (var r in reads)
        {
            List<double> t1 = [], t2 = [], t3 = [];
            int n1 = 0, n2 = 0, n3 = 0;
            for (int i = 0; i < 30; i++) { await r.Today(); await r.AppWhere(); await r.Floor(); }
            for (int i = 0; i < rounds; i++)
            {
                // Rotate the order so no mode always runs on a warm cache left by another.
                var order = (i % 3) switch { 0 => new[] { 0, 1, 2 }, 1 => new[] { 1, 2, 0 }, _ => new[] { 2, 0, 1 } };
                foreach (int m in order)
                {
                    long start = Stopwatch.GetTimestamp();
                    int n = m switch { 0 => await r.Today(), 1 => await r.AppWhere(), _ => await r.Floor() };
                    double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    (m switch { 0 => t1, 1 => t2, _ => t3 }).Add(ms);
                    if (m == 0) n1 = n; else if (m == 1) n2 = n; else n3 = n;
                }
            }
            static double P(List<double> v, double q) { v.Sort(); return v[(int)Math.Min(v.Count - 1, Math.Round(q * (v.Count - 1)))]; }
            Console.WriteLine($"  {r.Name,-18} {$"{n1}/{n2}/{n3}",-20} {$"{P(t1, .5):F2}/{P(t1, .95):F2}",-16} {$"{P(t2, .5):F2}/{P(t2, .95):F2}",-16} {$"{P(t3, .5):F2}/{P(t3, .95):F2}",-16} {P(t3, .95) / P(t2, .95):F2}");
        }
    }
    return;
}

// ADR-204 condition 1, the half the first run left owed: what one pool per tenant does to the connection
// budget, against one shared pool that is told the tenant. The load is the per-request catalogue read
// (D-30's lookup, one service by name), 64 callers for a few seconds, half of them on one hot tenant.
//
// Other sessions share this cluster, so the budget is a role's CONNECTION LIMIT rather than
// max_connections: running out fails the same way (53300) without taking anybody else's connections.
if (stage == "pools")
{
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 6;
    int Callers = args.Length > 3 ? int.Parse(args[3]) : 64;
    const int Budget = 24;
    await Exec(super, $"alter role {App} connection limit 36; alter role {Bypass} connection limit 26");

    // Every caller asks for one of its tenant's own services by name, as a request does before it serves.
    string[] callerTenant = Enumerable.Range(0, Callers)
        .Select(k => k < Callers / 2 ? "default" : Tenants[1 + k % 9]).ToArray();
    // Today's lookup, as FindServiceAsync writes it to match migration 15's index.
    const string LookupToday = """
        select s.id, s.name, s.kind, i.sharing, i.owner_principal_id
          from service s join item i on i.service_id = s.id
         where coalesce(lower(s.folder), '') = coalesce(lower(null::text), '') and lower(s.name) = lower(@name)
        """;
    // Under the floor: the stored key, and the host's tenant said outright - a service is served at its own
    // tenant's host, and a reachable foreign service of the same name would otherwise be a second answer.
    const string Lookup = """
        select s.id, s.name, s.kind, i.sharing, i.owner_principal_id
          from service s join item i on i.service_id = s.id
         where s.tenant_id = current_setting('graticula.tenant', true)
           and s.lookup_key = coalesce(lower(null::text), '') || chr(31) || lower(@name)
        """;
    // Alternative C: the same key and the same tenant, written into the statement, no policy.
    const string LookupC = """
        select s.id, s.name, s.kind, i.sharing, i.owner_principal_id
          from service s join item i on i.service_id = s.id
         where s.tenant_id = @tenant
           and s.lookup_key = coalesce(lower(null::text), '') || chr(31) || lower(@name)
        """;
    string NameFor(string tenant, int i) => $"{tenant}_s{1 + i % (tenant == "default" ? 550 : 50)}";

    async Task<(int peak, long ok, long refused, long wrong, double p50, double p95, double hotP95)> Run(
        string label, Func<string, NpgsqlDataSource> source, Func<NpgsqlDataSource, string, string, Task<int>> read,
        string appName)
    {
        int peak = 0; bool done = false;
        await using NpgsqlDataSource monitor = NpgsqlDataSource.Create(Su());
        Task sampler = Task.Run(async () =>
        {
            while (!Volatile.Read(ref done))
            {
                await using NpgsqlCommand c = monitor.CreateCommand(
                    $"select count(*) from pg_stat_activity where application_name like '{appName}%'");
                peak = Math.Max(peak, (int)(long)(await c.ExecuteScalarAsync())!);
                await Task.Delay(25);
            }
        });

        long ok = 0, refused = 0, wrong = 0;
        List<double>[] times = Enumerable.Range(0, Callers).Select(_ => new List<double>()).ToArray();
        DateTime stop = DateTime.UtcNow.AddSeconds(seconds);
        await Parallel.ForEachAsync(Enumerable.Range(0, Callers), new ParallelOptions { MaxDegreeOfParallelism = Callers },
            async (k, _) =>
            {
                string tenant = callerTenant[k];
                NpgsqlDataSource ds = source(tenant);
                for (int i = 0; DateTime.UtcNow < stop; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    try
                    {
                        int rows = await read(ds, tenant, NameFor(tenant, i * 7 + k));
                        times[k].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        Interlocked.Increment(ref ok);
                        if (rows != 1) Interlocked.Increment(ref wrong);
                    }
                    catch (Exception e) when (e is PostgresException { SqlState: "53300" } || e.InnerException is PostgresException { SqlState: "53300" })
                    {
                        Interlocked.Increment(ref refused);
                    }
                }
            });
        Volatile.Write(ref done, true);
        await sampler;

        static double P(List<double> v, double q) => v.Count == 0 ? double.NaN : v.OrderBy(x => x).ElementAt((int)Math.Round(q * (v.Count - 1)));
        List<double> all = times.SelectMany(t => t).ToList();
        List<double> hot = times.Take(Callers / 2).SelectMany(t => t).ToList();
        return (peak, ok, refused, wrong, P(all, .5), P(all, .95), P(hot, .95));
    }

    async Task<int> Plain(NpgsqlDataSource ds, string tenant, string name)
    {
        await using NpgsqlCommand cmd = ds.CreateCommand(Lookup);
        cmd.Parameters.AddWithValue("name", name);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        int n = 0; while (await r.ReadAsync()) n++;
        return n;
    }

    async Task<int> Today(NpgsqlDataSource ds, string tenant, string name)
    {
        await using NpgsqlCommand cmd = ds.CreateCommand(LookupToday);
        cmd.Parameters.AddWithValue("name", name);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        int n = 0; while (await r.ReadAsync()) n++;
        return n;
    }

    async Task<int> ByHand(NpgsqlDataSource ds, string tenant, string name)
    {
        await using NpgsqlCommand cmd = ds.CreateCommand(LookupC);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("name", name);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        int n = 0; while (await r.ReadAsync()) n++;
        return n;
    }

    // The tenant set on a connection when it is taken: one more round trip, then the statement.
    async Task<int> SetOnOpen(NpgsqlDataSource ds, string tenant, string name)
    {
        await using NpgsqlConnection c = await ds.OpenConnectionAsync();
        await using (NpgsqlCommand set = new("select set_config('graticula.tenant', @t, false)", c))
        {
            set.Parameters.AddWithValue("t", tenant);
            await set.ExecuteScalarAsync();
        }
        await using NpgsqlCommand cmd = new(Lookup, c);
        cmd.Parameters.AddWithValue("name", name);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        int n = 0; while (await r.ReadAsync()) n++;
        return n;
    }

    // The tenant sent in the same round trip as the statement: a batch of two, the first answer skipped.
    async Task<int> Batched(NpgsqlDataSource ds, string tenant, string name)
    {
        await using NpgsqlBatch batch = ds.CreateBatch();
        NpgsqlBatchCommand set = new("select set_config('graticula.tenant', $1, false)");
        set.Parameters.Add(new NpgsqlParameter { Value = tenant });
        NpgsqlBatchCommand q = new(Lookup.Replace("@name", "$1"));
        q.Parameters.Add(new NpgsqlParameter { Value = name });
        batch.BatchCommands.Add(set);
        batch.BatchCommands.Add(q);
        await using NpgsqlDataReader r = await batch.ExecuteReaderAsync();
        await r.NextResultAsync();
        int n = 0; while (await r.ReadAsync()) n++;
        return n;
    }

    NpgsqlDataSource Named(string cs, string app, int pool, int prepare = 0)
    {
        NpgsqlConnectionStringBuilder b = new(cs) { ApplicationName = app, MaxPoolSize = pool, MaxAutoPrepare = prepare };
        return NpgsqlDataSource.Create(b.ConnectionString);
    }

    var modes = new List<(string Label, Func<Task<(int, long, long, long, double, double, double)>> Go)>();

    modes.Add(("today: one pool of 24, no tenant", async () =>
    {
        await using NpgsqlDataSource ds = Named(Cs(Bypass), "tf-today", Budget);
        return await Run("today", _ => ds, Today, "tf-today");
    }));
    modes.Add(("C: one pool of 24, tenant written in", async () =>
    {
        await using NpgsqlDataSource ds = Named(Cs(Bypass), "tf-byhand", Budget);
        return await Run("byhand", _ => ds, ByHand, "tf-byhand");
    }));
    modes.Add(("a pool per tenant, 24 each", async () =>
    {
        Dictionary<string, NpgsqlDataSource> pools = Tenants.ToDictionary(t => t, t => Named(Cs(App, t), "tf-each24-" + t, Budget));
        try { return await Run("each24", t => pools[t], Plain, "tf-each24"); }
        finally { foreach (var p in pools.Values) await p.DisposeAsync(); }
    }));
    modes.Add(("a pool per tenant, 24 shared out (3 each, hot 6)", async () =>
    {
        Dictionary<string, NpgsqlDataSource> pools = Tenants.ToDictionary(t => t,
            t => Named(Cs(App, t), "tf-split-" + t, t == "default" ? 6 : 2));
        try { return await Run("split", t => pools[t], Plain, "tf-split"); }
        finally { foreach (var p in pools.Values) await p.DisposeAsync(); }
    }));
    modes.Add(("one pool of 24, tenant set on open", async () =>
    {
        await using NpgsqlDataSource ds = Named(Cs(App), "tf-onopen", Budget);
        return await Run("onopen", _ => ds, SetOnOpen, "tf-onopen");
    }));
    modes.Add(("one pool of 24, tenant batched in", async () =>
    {
        await using NpgsqlDataSource ds = Named(Cs(App), "tf-batch", Budget);
        return await Run("batch", _ => ds, Batched, "tf-batch");
    }));

    // The same three with plans kept: Npgsql's automatic preparation, which the product does not switch on.
    // A statement under a policy is planned with the policy, every call, unless the plan is kept.
    modes.Add(("C, plans kept", async () =>
    {
        await using NpgsqlDataSource ds = Named(Cs(Bypass), "tf-byhandp", Budget, 32);
        return await Run("byhandp", _ => ds, ByHand, "tf-byhandp");
    }));
    modes.Add(("a pool per tenant shared out, plans kept", async () =>
    {
        Dictionary<string, NpgsqlDataSource> pools = Tenants.ToDictionary(t => t,
            t => Named(Cs(App, t), "tf-splitp-" + t, t == "default" ? 6 : 2, 32));
        try { return await Run("splitp", t => pools[t], Plain, "tf-splitp"); }
        finally { foreach (var p in pools.Values) await p.DisposeAsync(); }
    }));
    modes.Add(("one pool of 24, tenant batched in, plans kept", async () =>
    {
        await using NpgsqlDataSource ds = Named(Cs(App), "tf-batchp", Budget, 32);
        return await Run("batchp", _ => ds, Batched, "tf-batchp");
    }));
    if (Callers < 24) modes.RemoveAll(m => m.Label.StartsWith("a pool per tenant, 24 each"));

    // Correctness of the shared pool before its speed: a pool of one, tenants alternating every request,
    // and a request that forgets to say who it is must read nothing rather than the last tenant's rows.
    foreach (int prepare in new[] { 0, 32 })
    await using (NpgsqlDataSource one = Named(Cs(App), "tf-one", 1, prepare))
    {
        int wrongTenant = 0, forgotten = 0;
        for (int i = 0; i < 2000; i++)
        {
            string t = Tenants[i % Tenants.Length];
            if (await Batched(one, t, NameFor(t, i)) != 1) wrongTenant++;
            if (await Plain(one, t, NameFor(t, i)) != 0) forgotten++;
        }
        Console.WriteLine($"shared pool of one (plans kept: {prepare > 0}), 2000 alternating requests: {wrongTenant} answered wrongly, "
            + $"{forgotten} requests that set no tenant read a row (must be 0 — the previous tenant must not survive)");
    }

    Console.WriteLine($"{Callers} callers, {seconds} s each, half on the default tenant; budget {Budget}; role limit 36");
    Console.WriteLine($"  {"mode",-48} {"peak conns",10} {"answered",9} {"refused",8} {"wrong",6} {"p50",7} {"p95",7} {"hot p95",8}");
    for (int round = 0; round < 2; round++)
        foreach (var m in modes)
        {
            var (peak, ok, refused, wrong, p50, p95, hot) = await m.Go();
            Console.WriteLine($"  {m.Label,-48} {peak,10} {ok,9} {refused,8} {wrong,6} {p50,7:F2} {p95,7:F2} {hot,8:F2}");
            NpgsqlConnection.ClearAllPools();
            await Task.Delay(500);
        }

    await Exec(super, $"alter role {App} connection limit -1; alter role {Bypass} connection limit -1");
    return;
}

Console.WriteLine("unknown stage " + stage);
