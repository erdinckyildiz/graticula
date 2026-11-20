using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Tenancy;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>Runs alone: these tests change how many organisations the process believes there are.</summary>
[CollectionDefinition("organisation count", DisableParallelization = true)]
public sealed class OrganisationCountRunsAlone;

/// <summary>
/// ADR-274 condition 4: the scene package files the sweep keeps are the ones a live row of any organisation, or a copy in
/// any organisation's recycle bin, names — across two real organisations, through a restore from the bin and a purge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two real organisations, under a role the floor binds.</b> The test's own connection is a superuser in CI and on the
/// local fixture, and row-level security never binds a superuser, so a test made on it would pass with the floor
/// missing. The organisations' statements are made here on a second pool signed in as a role with neither
/// <c>SUPERUSER</c> nor <c>BYPASSRLS</c> (as <c>PlatformStoreRoleTests</c> signs in), routed by tenant as the host routes its own (ADR-204). Each organisation then sees only its own documents — which the
/// test checks, so it knows the two are real — while the sweep's names, read on the store's root without a tenant, are
/// every organisation's.
/// </para>
/// <para>
/// <b>The folder half</b> — a day-old file the names leave out is removed, a named one never — is
/// <c>ScenePackageFilesTests</c>' in the host's tests; this is the half that asks the database.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection("organisation count")]
public sealed class ScenePackageFileNamesAcrossTenantsTests : PostgresFixture
{
    private static readonly Guid Main = TenantScope.DefaultTenant;
    private static readonly Guid Venture = new("20000000-0000-0000-0000-0000000002a4");

    private const string Password = "zz-spf-floor";

    private static string File(char c) => $"scene-package-files/{new string(c, 64)}.slpk";

    [Fact]
    public async Task Every_organisations_live_and_recycled_package_is_named_a_restore_keeps_it_and_a_purge_lets_it_go()
    {
        await MigrateAsync();
        string role = "zz_spf_floor_" + Guid.NewGuid().ToString("N")[..10];
        await ExecuteAsync(
            $"create role {role} login password '{Password}' nosuperuser nobypassrls; "
            + $"grant usage on schema \"{SchemaName}\", public to {role}; "
            + $"grant select, insert, update, delete on all tables in schema \"{SchemaName}\" to {role}; "
            + $"grant usage, select on all sequences in schema \"{SchemaName}\" to {role}; "
            + $"grant execute on all functions in schema \"{SchemaName}\" to {role}");

        Guid mainOwner = await PrincipalAsync(Main, "spf-main");
        Guid ventureOwner = await PrincipalAsync(Venture, "spf-venture");
        DocumentKind slpk = Documents.Named("slpk")!;

        await using NpgsqlDataSource bound = Bound(role).RoutedByTenant();
        PostgresDocumentStore documents = new(bound);
        PostgresRecycleBin bin = new(bound);
        PostgresDocumentStore sweep = new(DataSource);
        TenantScope.SetCount(2);

        try
        {
            Document mainPackage, ventureShared, ventureOwn;

            using (TenantScope.Enter(Main))
            {
                mainPackage = await documents.CreateFileAsync(
                    "main mesh", null, mainOwner, SharingScope.Private, "a.slpk", slpk, 70_000_000, File('a'), new string('a', 64), CancellationToken.None);
            }

            using (TenantScope.Enter(Venture))
            {
                // The same bytes as the main organisation's: one file, named by its hash, for two rows of two organisations.
                ventureShared = await documents.CreateFileAsync(
                    "venture copy", null, ventureOwner, SharingScope.Private, "a.slpk", slpk, 70_000_000, File('a'), new string('a', 64), CancellationToken.None);
                ventureOwn = await documents.CreateFileAsync(
                    "venture mesh", null, ventureOwner, SharingScope.Private, "b.slpk", slpk, 80_000_000, File('b'), new string('b', 64), CancellationToken.None);

                // The floor binds: the venture sees its own two and not the main organisation's.
                Assert.Equal(
                    new[] { ventureOwn.Id, ventureShared.Id }.Order(StringComparer.Ordinal),
                    (await documents.ListAsync(CancellationToken.None)).Select(d => d.Id).Order(StringComparer.Ordinal));
                Assert.Null(await documents.FindAsync(mainPackage.Id, CancellationToken.None));
            }

            using (TenantScope.Enter(Main))
            {
                Assert.Equal(mainPackage.Id, Assert.Single(await documents.ListAsync(CancellationToken.None)).Id);
            }

            Assert.Equal(new HashSet<string> { File('a'), File('b') }, await NamesAsync(sweep));

            // The main organisation deletes its package for good: the venture's copy still names the shared file.
            using (TenantScope.Enter(Main))
            {
                Assert.True(await documents.DeleteAsync(mainPackage.Id, CancellationToken.None));
            }

            Assert.Equal(new HashSet<string> { File('a'), File('b') }, await NamesAsync(sweep));

            // The venture sends its own package to its recycle bin: the copy there still names the file.
            Guid entry;

            using (TenantScope.Enter(Venture))
            {
                entry = (await bin.RecycleAsync(
                    "item", Guid.Parse(ventureOwn.Id), Guid.Parse(ventureOwn.Id), "document", ventureOwn.Title, ventureOwner, ventureOwner,
                    DateTimeOffset.UtcNow.AddDays(14), [], CancellationToken.None))!.Value;
                Assert.Null(await documents.FindAsync(ventureOwn.Id, CancellationToken.None));
            }

            using (TenantScope.Enter(Main))
            {
                // Not the main organisation's to see, in the bin or out of it.
                Assert.Null(await bin.FindAsync(entry, CancellationToken.None));
            }

            Assert.Equal(new HashSet<string> { File('a'), File('b') }, await NamesAsync(sweep));

            // Restored, it names its file again from its own row, with its size and hash.
            using (TenantScope.Enter(Venture))
            {
                Assert.Null(await bin.RestoreRowsAsync(entry, CancellationToken.None));
                Document back = (await documents.FindAsync(ventureOwn.Id, CancellationToken.None))!;
                Assert.Equal(File('b'), back.ContentFile);
                Assert.Equal(80_000_000, back.Size);
            }

            Assert.Equal(new HashSet<string> { File('a'), File('b') }, await NamesAsync(sweep));

            // Recycled again and purged from the bin: nothing names its file, and the sweep may take it.
            using (TenantScope.Enter(Venture))
            {
                entry = (await bin.RecycleAsync(
                    "item", Guid.Parse(ventureOwn.Id), Guid.Parse(ventureOwn.Id), "document", ventureOwn.Title, ventureOwner, ventureOwner,
                    DateTimeOffset.UtcNow.AddDays(14), [], CancellationToken.None))!.Value;
                Assert.True(await bin.RemoveAsync(entry, CancellationToken.None));
            }

            Assert.Equal(new HashSet<string> { File('a') }, await NamesAsync(sweep));

            // And the last row naming the shared file goes: the folder's files are no longer anybody's.
            using (TenantScope.Enter(Venture))
            {
                Assert.True(await documents.DeleteAsync(ventureShared.Id, CancellationToken.None));
            }

            Assert.Empty(await NamesAsync(sweep));
        }
        finally
        {
            TenantScope.SetCount(1);
            await bound.DisposeAsync();
            await ExecuteAsync(
                $"revoke all on all tables in schema \"{SchemaName}\" from {role}; "
                + $"revoke all on all sequences in schema \"{SchemaName}\" from {role}; "
                + $"revoke all on all functions in schema \"{SchemaName}\" from {role}; "
                + $"revoke all on schema \"{SchemaName}\", public from {role}; "
                + $"drop role {role}");
        }
    }

    /// <summary>The names, as the sweep reads them: on the root, outside any organisation.</summary>
    private static async Task<HashSet<string>> NamesAsync(PostgresDocumentStore store) =>
        [.. await store.ContentFilesAsync(CancellationToken.None)];

    private async Task<Guid> PrincipalAsync(Guid tenant, string name)
    {
        Guid id = Guid.NewGuid();
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type, tenant_id) values (@id, 'user', @name, 'creator', @tenant)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name + "-" + id.ToString("N")[..8]);
        command.Parameters.AddWithValue("tenant", tenant);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A pool on this test's schema, signed in as <paramref name="role"/>.</summary>
    private NpgsqlDataSource Bound(string role)
    {
        // From the variable: Npgsql redacts the password out of a live data source's connection string.
        NpgsqlConnectionStringBuilder built = new(Environment.GetEnvironmentVariable("GRATICULA_TEST_PG"))
        {
            Username = role,
            Password = Password,
            SearchPath = $"{SchemaName},public",
        };
        return NpgsqlDataSource.Create(built.ConnectionString);
    }
}
