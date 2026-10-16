using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Documents against PostgreSQL — ADR-188, migration 89: an item of kind <c>document</c> and its bytes beside it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DocumentStoreTests : PostgresFixture
{
    private static readonly DocumentKind Pdf = Documents.Named("pdf")!;
    private static readonly DocumentKind Excel = Documents.Named("xlsx")!;

    private async Task<(PostgresDocumentStore Store, Guid Owner, string OwnerName)> ReadyAsync(string name = "filer")
    {
        await MigrateAsync();

        Guid principal = Guid.NewGuid();
        string unique = name + "_" + principal.ToString("N")[..8];

        await using (NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name) values (@id, 'user', @name)"))
        {
            command.Parameters.AddWithValue("id", principal);
            command.Parameters.AddWithValue("name", unique);
            await command.ExecuteNonQueryAsync();
        }

        return (new PostgresDocumentStore(DataSource), principal, unique);
    }

    [Fact]
    public async Task A_document_reads_back_as_it_was_kept_and_its_bytes_only_when_asked()
    {
        (PostgresDocumentStore store, Guid owner, string ownerName) = await ReadyAsync();
        byte[] content = [.. "%PDF-1.7\n"u8, .. Enumerable.Range(0, 5000).Select(i => (byte)i)];

        Document made = await store.CreateAsync(
            "Zoning plan", "The 2026 plan", owner, SharingScope.Organization, "zoning.pdf", Pdf, content, CancellationToken.None);

        Assert.True(WebMaps.IsId(made.Id), $"'{made.Id}' is not a 32-hex item id.");
        Assert.Equal("Zoning plan", made.Title);
        Assert.Equal("The 2026 plan", made.Snippet);
        Assert.Equal(owner, made.Owner);
        Assert.Equal(ownerName, made.OwnerName);
        Assert.Equal(SharingScope.Organization, made.Sharing);
        Assert.Equal("zoning.pdf", made.FileName);
        Assert.Same(Pdf, made.Kind);
        Assert.Equal(content.Length, made.Size);

        Assert.Equal(content, await store.ContentAsync(made.Id, CancellationToken.None));
        Assert.Contains(await store.ListAsync(CancellationToken.None), listed => listed.Id == made.Id);

        // The item is the item every rule reads: one row, of kind document, with no service or map behind it.
        await using NpgsqlCommand item = DataSource.CreateCommand(
            "select kind, service_id is null and web_map_id is null from item where id = @id::uuid");
        item.Parameters.AddWithValue("id", made.Id);
        await using NpgsqlDataReader row = await item.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("document", row.GetString(0));
        Assert.True(row.GetBoolean(1));
    }

    [Fact]
    public async Task Replacing_the_file_keeps_the_item_and_changes_only_the_file()
    {
        (PostgresDocumentStore store, Guid owner, _) = await ReadyAsync();

        Document made = await store.CreateAsync(
            "Parcels", null, owner, SharingScope.Private, "parcels.pdf", Pdf, "%PDF-1.4 first"u8.ToArray(), CancellationToken.None);
        await store.SetTagsAsync(made.Id, ["cadastre"], CancellationToken.None);

        Assert.True(await store.ReplaceAsync(made.Id, "parcels.xlsx", Excel, "PK\u0003\u0004 second"u8.ToArray(), CancellationToken.None));

        Document after = (await store.FindAsync(made.Id, CancellationToken.None))!;
        Assert.Equal("Parcels", after.Title);
        Assert.Equal(["cadastre"], after.Tags);
        Assert.Equal("parcels.xlsx", after.FileName);
        Assert.Same(Excel, after.Kind);
        Assert.Equal("PK\u0003\u0004 second"u8.ToArray(), await store.ContentAsync(made.Id, CancellationToken.None));
        Assert.True(after.Modified >= made.Modified);
    }

    [Fact]
    public async Task A_document_is_shared_with_a_group_as_a_map_is_and_listed_in_it_as_a_document()
    {
        (PostgresDocumentStore store, Guid owner, string ownerName) = await ReadyAsync();
        PostgresGroupDirectory groups = new(DataSource);

        Document made = await store.CreateAsync(
            "Field guide", null, owner, SharingScope.Group, "guide.pdf", Pdf, "%PDF-1.7 guide"u8.ToArray(), CancellationToken.None);

        string group = "crew_" + Guid.NewGuid().ToString("N")[..8];
        await using (NpgsqlCommand command = DataSource.CreateCommand(
            "insert into sharing_group (id, name, owner_principal_id) values (gen_random_uuid(), @name, @owner)"))
        {
            command.Parameters.AddWithValue("name", group);
            command.Parameters.AddWithValue("owner", owner);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(GroupChange.Done, await groups.ShareDocumentAsync(owner, true, group, made.Id, true, CancellationToken.None));

        Document shared = (await store.FindAsync(made.Id, CancellationToken.None))!;
        Assert.Equal([group], shared.SharedWithNames);

        GroupItem listed = Assert.Single(await groups.ItemsAsync(group, CancellationToken.None));
        Assert.Equal("Field guide", listed.Name);
        Assert.Equal("Document", listed.Kind);

        // A web map's id is not a document's, and a document's id is not a map's.
        Assert.Equal(GroupChange.NoSuchTarget, await groups.ShareMapAsync(owner, true, group, made.Id, true, CancellationToken.None));
        Assert.Equal(GroupChange.Done, await groups.ShareDocumentAsync(owner, true, group, made.Id, false, CancellationToken.None));
        Assert.Empty((await store.FindAsync(made.Id, CancellationToken.None))!.SharedWithNames!);
        _ = ownerName;
    }

    [Fact]
    public async Task Deleting_a_document_takes_its_file_and_nothing_else()
    {
        (PostgresDocumentStore store, Guid owner, _) = await ReadyAsync();

        Document kept = await store.CreateAsync(
            "Kept", null, owner, SharingScope.Private, "kept.pdf", Pdf, "%PDF-1.7 kept"u8.ToArray(), CancellationToken.None);
        Document gone = await store.CreateAsync(
            "Gone", null, owner, SharingScope.Private, "gone.pdf", Pdf, "%PDF-1.7 gone"u8.ToArray(), CancellationToken.None);

        Assert.True(await store.DeleteAsync(gone.Id, CancellationToken.None));
        Assert.False(await store.DeleteAsync(gone.Id, CancellationToken.None));

        Assert.Null(await store.FindAsync(gone.Id, CancellationToken.None));
        Assert.Null(await store.ContentAsync(gone.Id, CancellationToken.None));
        Assert.NotNull(await store.ContentAsync(kept.Id, CancellationToken.None));

        await using NpgsqlCommand count = DataSource.CreateCommand("select count(*) from document where item_id = @id::uuid");
        count.Parameters.AddWithValue("id", gone.Id);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_store_refuses_a_size_that_is_not_the_files()
    {
        await MigrateAsync();

        // The constraint is what keeps the size a listing shows from ever disagreeing with the file.
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "with i as (insert into item (id, kind, title) values (gen_random_uuid(), 'document', 'Lying') returning id) "
            + "insert into document (item_id, file_name, kind, size, content) select id, 'x.pdf', 'pdf', 999, '\\x2550'::bytea from i");

        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("document_size_matches", refused.ConstraintName);
    }
}
