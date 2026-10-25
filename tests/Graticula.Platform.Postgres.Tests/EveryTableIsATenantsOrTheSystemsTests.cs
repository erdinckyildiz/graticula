using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-204 condition 2: no list of tenant tables is kept anywhere. Every table of the migrated platform schema either
/// carries <c>tenant_id</c> under a forced <c>tenant_floor</c> policy, or is named here as the system's or as a secret
/// looked up and never listed — so a table a later migration adds fails this until somebody says which it is.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EveryTableIsATenantsOrTheSystemsTests : PostgresFixture
{
    /// <summary>The tables that are not a tenant's, and why each is not (ADR-204 §1b, §5b, migration 100).</summary>
    private static readonly Dictionary<string, string> NotFloored = new(StringComparer.Ordinal)
    {
        ["tenant"] = "the tenants themselves",
        ["platform_schema"] = "the schema stamp",
        ["service_usage"] = "the system's counters",
        ["request_log"] = "carries tenant_id and is narrowed by its reader; written with COPY, which a forced policy refuses",
        ["job"] = "the system's queue; a job carries its tenant and runs as it",
        ["setup_token"] = "first start, before any tenant",
        ["system_service"] = "the machine's services",
        ["server_setting"] = "the machine's settings; an organisation's are kept under names that carry it",
        ["session"] = "looked up by a secret, never listed",
        ["api_key"] = "looked up by a secret, never listed",
        ["local_credential"] = "looked up by a secret, never listed",
        ["principal_second_factor"] = "looked up by a secret, never listed",
        ["login_attempt"] = "looked up by a secret, never listed",
        ["oauth_code"] = "looked up by a secret, never listed",
        ["oauth_refresh"] = "looked up by a secret, never listed",
        ["saml_assertion_used"] = "looked up by a secret, never listed",
        ["user_type"] = "the deployment's user types",
        ["partnership"] = "two tenants' at once; its own policy shows a row to either party and nobody else (ADR-219 §5.6)",
    };

    [Fact]
    public async Task Every_table_is_floored_or_named_as_the_systems()
    {
        await MigrateAsync();

        List<string> unaccounted = [];

        await using NpgsqlCommand command = DataSource.CreateCommand(
            """
            select c.relname,
                   exists (select 1 from pg_attribute a where a.attrelid = c.oid and a.attname = 'tenant_id' and not a.attisdropped),
                   c.relrowsecurity and c.relforcerowsecurity,
                   exists (select 1 from pg_policies p where p.schemaname = n.nspname and p.tablename = c.relname and p.policyname = 'tenant_floor')
              from pg_class c join pg_namespace n on n.oid = c.relnamespace
             where n.nspname = current_schema() and c.relkind in ('r', 'p')
             order by c.relname
            """);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            bool floored = reader.GetBoolean(1) && reader.GetBoolean(2) && reader.GetBoolean(3);

            if (!floored && !NotFloored.ContainsKey(table))
            {
                unaccounted.Add(table);
            }

            if (floored && NotFloored.ContainsKey(table))
            {
                unaccounted.Add($"{table} (floored, and also listed as the system's)");
            }
        }

        Assert.True(unaccounted.Count == 0,
            "These tables are neither a tenant's — tenant_id, row-level security forced, and the tenant_floor policy — nor "
            + $"named as the system's: {string.Join(", ", unaccounted)}. Floor it in its migration, or say here why it is not.");
    }
}
