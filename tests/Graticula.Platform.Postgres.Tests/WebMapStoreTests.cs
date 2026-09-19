using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Saved web maps against PostgreSQL — ADR-079, migration 52.
/// </summary>
/// <remarks>
/// <b>The round trip is the point.</b> ADR-079 §5.2 promises that a map saved by ArcGIS Pro and saved
/// again here loses nothing it carried, which is a promise about the store before it is one about the
/// viewer: a field the table dropped could not be put back by anything above it.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class WebMapStoreTests : PostgresFixture
{
    /// <summary>A document with fields this server never reads, of every JSON kind.</summary>
    private const string ProDocument = """
        {
          "operationalLayers": [
            {
              "id": "parcels-1",
              "layerType": "ArcGISFeatureLayer",
              "url": "https://example.test/rest/services/hosted/parcels/FeatureServer/0",
              "title": "Parcels",
              "visibility": true,
              "opacity": 0.8,
              "layerDefinition": { "definitionExpression": "zone = 'R1'" },
              "popupInfo": { "title": "{name}", "fieldInfos": [ { "fieldName": "name", "visible": true } ] }
            }
          ],
          "baseMap": { "baseMapLayers": [ { "id": "osm", "layerType": "OpenStreetMap" } ], "title": "OSM" },
          "bookmarks": [ { "name": "Home", "extent": { "xmin": 1.5, "ymin": -2, "xmax": 3, "ymax": 4e2 } } ],
          "applicationProperties": { "viewing": { "search": { "enabled": false } } },
          "unicode": "Çankaya — İl",
          "nothing": null,
          "version": "2.31"
        }
        """;

    private async Task<(PostgresWebMapStore Store, Guid Owner, string OwnerName)> ReadyAsync(string name = "mapper")
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

        return (new PostgresWebMapStore(DataSource), principal, unique);
    }

    [Fact]
    public async Task A_saved_map_reads_back_with_every_field_it_was_given()
    {
        (PostgresWebMapStore store, Guid owner, string ownerName) = await ReadyAsync();

        WebMap made = await store.CreateAsync(
            "Zoning", "Residential parcels", owner, SharingScope.Organization, ProDocument, CancellationToken.None);

        Assert.True(WebMaps.IsId(made.Id), $"'{made.Id}' is not a 32-hex item id.");
        Assert.Equal("Zoning", made.Title);
        Assert.Equal("Residential parcels", made.Snippet);
        Assert.Equal(owner, made.Owner);
        Assert.Equal(ownerName, made.OwnerName);
        Assert.Equal(SharingScope.Organization, made.Sharing);

        WebMap? read = await store.FindAsync(made.Id, CancellationToken.None);

        Assert.NotNull(read);

        using JsonDocument expected = JsonDocument.Parse(ProDocument);
        using JsonDocument actual = JsonDocument.Parse(read!.Document!);

        Assert.True(
            JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
            "The stored document is not the one saved. jsonb may reorder keys; it must not lose or change "
            + $"one. Read back:\n{read.Document}");
    }

    [Fact]
    public async Task A_listing_carries_no_document_and_the_newest_change_first()
    {
        (PostgresWebMapStore store, Guid owner, _) = await ReadyAsync();

        WebMap first = await store.CreateAsync("First", null, owner, SharingScope.Private, "{}", CancellationToken.None);
        WebMap second = await store.CreateAsync("Second", null, owner, SharingScope.Public, "{}", CancellationToken.None);

        // Touch the first so it is the most recent change.
        await Task.Delay(20);
        await store.UpdateAsync(first.Id, "First, again", null, SharingScope.Private, """{"a":1}""", CancellationToken.None);

        WebMap[] listed = [.. await store.ListAsync(CancellationToken.None)];

        Assert.Equal([first.Id, second.Id], listed.Select(m => m.Id).ToArray());
        Assert.All(listed, map => Assert.Null(map.Document));
        Assert.Equal("First, again", listed[0].Title);
    }

    [Fact]
    public async Task An_update_replaces_title_scope_and_document_and_moves_the_modified_time()
    {
        (PostgresWebMapStore store, Guid owner, _) = await ReadyAsync();

        WebMap made = await store.CreateAsync("Draft", "one", owner, SharingScope.Private, "{}", CancellationToken.None);

        await Task.Delay(20);

        WebMap? changed = await store.UpdateAsync(
            made.Id, "Final", null, SharingScope.Public, ProDocument, CancellationToken.None);

        Assert.NotNull(changed);
        Assert.Equal("Final", changed!.Title);
        Assert.Null(changed.Snippet);
        Assert.Equal(SharingScope.Public, changed.Sharing);
        Assert.Equal(made.Created, changed.Created);
        Assert.True(changed.Modified > made.Modified, "An update did not move the modified time.");
        Assert.Contains("bookmarks", changed.Document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_map_that_is_not_there_is_null_on_read_and_update_and_false_on_delete()
    {
        (PostgresWebMapStore store, _, _) = await ReadyAsync();

        string absent = Guid.NewGuid().ToString("N");

        Assert.Null(await store.FindAsync(absent, CancellationToken.None));
        Assert.Null(await store.UpdateAsync(absent, "x", null, SharingScope.Private, "{}", CancellationToken.None));
        Assert.False(await store.DeleteAsync(absent, CancellationToken.None));

        // And an id of the wrong shape never reaches the database.
        Assert.Null(await store.FindAsync("not-an-id'; drop table web_map; --", CancellationToken.None));
    }

    [Fact]
    public async Task A_deleted_map_is_gone()
    {
        (PostgresWebMapStore store, Guid owner, _) = await ReadyAsync();

        WebMap made = await store.CreateAsync("Doomed", null, owner, SharingScope.Private, "{}", CancellationToken.None);

        Assert.True(await store.DeleteAsync(made.Id, CancellationToken.None));
        Assert.Null(await store.FindAsync(made.Id, CancellationToken.None));
        Assert.False(await store.DeleteAsync(made.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_table_refuses_a_document_that_is_not_an_object_and_a_group_scope()
    {
        (_, Guid owner, _) = await ReadyAsync();

        // The endpoint checks both first; the constraints are there for whatever writes past it.
        await using NpgsqlCommand array = DataSource.CreateCommand(
            "insert into web_map (id, title, owner_principal_id, document) values (@id, 't', @owner, '[1]')");
        array.Parameters.AddWithValue("id", Guid.NewGuid().ToString("N"));
        array.Parameters.AddWithValue("owner", owner);

        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => array.ExecuteNonQueryAsync());
        Assert.Equal("web_map_document_is_object", refused.ConstraintName);

        await using NpgsqlCommand group = DataSource.CreateCommand(
            "insert into web_map (id, title, owner_principal_id, sharing, document) values (@id, 't', @owner, 'group', '{}')");
        group.Parameters.AddWithValue("id", Guid.NewGuid().ToString("N"));
        group.Parameters.AddWithValue("owner", owner);

        refused = await Assert.ThrowsAsync<PostgresException>(() => group.ExecuteNonQueryAsync());
        Assert.Equal("web_map_sharing_known", refused.ConstraintName);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PostgresWebMapStore(DataSource)
            .CreateAsync("t", null, owner, SharingScope.Group, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task A_member_s_maps_are_holdings_a_transfer_moves_and_a_removal_takes()
    {
        (PostgresWebMapStore store, Guid owner, string ownerName) = await ReadyAsync("leaver");
        (_, Guid receiver, string receiverName) = await ReadyAsync("receiver");

        PostgresMemberDirectory members = new(DataSource);

        WebMap made = await store.CreateAsync("Theirs", null, owner, SharingScope.Public, "{}", CancellationToken.None);

        MemberHoldings? holdings = await members.HoldingsOfAsync(ownerName, CancellationToken.None);

        Assert.NotNull(holdings);
        Assert.Equal(1, holdings!.Value.WebMaps);
        Assert.True(holdings.Value.Any, "A member who owns a map owns something, and removing them must ask.");
        Assert.Contains("1 web map(s)", holdings.Value.Explanation, StringComparison.Ordinal);

        Assert.Equal(1, await members.TransferOwnershipAsync(ownerName, receiverName, CancellationToken.None));

        WebMap? moved = await store.FindAsync(made.Id, CancellationToken.None);
        Assert.Equal(receiver, moved!.Owner);
        Assert.Equal(SharingScope.Public, moved.Sharing);

        // And a removal with the maps still owned takes them with the account rather than failing.
        Assert.Equal(MemberRemoval.Removed, await members.RemoveAsync(receiverName, CancellationToken.None));
        Assert.Null(await store.FindAsync(made.Id, CancellationToken.None));
    }
}
