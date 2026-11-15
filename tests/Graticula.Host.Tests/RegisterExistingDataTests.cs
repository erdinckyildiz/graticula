using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-288 condition 3, D-406: <c>createReplica</c> with <c>replicaOptions.registerExistingData</c>, which ArcGIS Field Maps
/// 26.2.2 sends to register a downloaded map-area package (measured 2026-11-12, research note §5a).
/// </summary>
public sealed class RegisterExistingDataTests
{
    private static readonly Guid Reference = Guid.Parse("75f74eb9-c9aa-4e16-9857-d605b8d3d270");
    private static readonly Guid Packager = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();
    private static readonly DateTimeOffset Packaged = DateTimeOffset.FromUnixTimeMilliseconds(1794465265186);
    private static readonly DateTimeOffset Later = Packaged.AddHours(3);

    private static PublishedService Service()
    {
        PublishedLayer Layer(int index) => new(
            Guid.NewGuid(),
            new LayerDefinition("points", "public", "points", "geom", 3857, "objectid", "objectid", isHosted: true),
            "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Private, ServiceStatus.Started,
            layerIndex: index, serviceName: "Hydrants")
        {
            ServiceSyncEnabled = true,
        };

        return new PublishedService(
            Guid.NewGuid(), "Hydrants", "hosted", "FeatureServer", null, null, SharingScope.Private, ServiceStatus.Started,
            [Layer(0), Layer(1)])
        {
            SyncEnabled = true,
        };
    }

    private static ReplicaRecord Held(PublishedService service, long generation) => new(
        Reference, "Area_ab12", service.Id, Packager, "packager", "perLayer",
        [new ReplicaLayer(0, generation, "useFilter", "status = 1", true), new ReplicaLayer(1, generation, "all", string.Empty, false)],
        true, "bidirectional", """{"xmin":1,"ymin":2,"xmax":3,"ymax":4}""", 3857, Packaged, Packaged);

    private static Func<string, string?> Fields(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> values = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return name => values.TryGetValue(name, out string? value) ? value : null;
    }

    private static Guid ReadId(string options)
    {
        Assert.True(OfflineReplicaRequests.TryReadRegisterExisting(Fields(("replicaOptions", options)), out Guid? id, out string? error), error);
        return id.GetValueOrDefault();
    }

    // ------------------------------------------------------------------------------------------------ reading

    [Fact]
    public void The_request_Field_Maps_sends_is_read_as_a_registration_of_the_package_it_downloaded()
    {
        // Word for word what ArcGIS Field Maps 26.2.2 sent: braced, lower case, nothing else but async/f/transportType.
        Func<string, string?> field = Fields(
            ("async", "true"), ("f", "json"), ("transportType", "esriTransportTypeUrl"),
            ("replicaOptions", """{"registerExistingData":{"refReplicaId":"{75f74eb9-c9aa-4e16-9857-d605b8d3d270}"}}"""));

        Assert.True(OfflineReplicaRequests.TryReadRegisterExisting(field, out Guid? id, out string? error), error);
        Assert.Equal(Reference, id);
    }

    [Fact]
    public void A_request_without_replicaOptions_is_not_a_registration_and_is_left_to_the_ordinary_reading()
    {
        Assert.True(OfflineReplicaRequests.TryReadRegisterExisting(Fields(("layers", "0")), out Guid? id, out _));
        Assert.Null(id);

        Assert.True(OfflineReplicaRequests.TryReadRegisterExisting(Fields(("replicaOptions", """{"other":1}""")), out id, out _));
        Assert.Null(id);
    }

    [Theory]
    [InlineData("{not json", "is not JSON")]
    [InlineData("[1]", "is not a JSON object")]
    [InlineData("""{"registerExistingData":{}}""", "needs a refReplicaId")]
    [InlineData("""{"registerExistingData":{"refReplicaId":7}}""", "needs a refReplicaId")]
    [InlineData("""{"registerExistingData":{"refReplicaId":"not-a-guid"}}""", "refReplicaId 'not-a-guid' is not a replica id")]
    public void A_malformed_registration_is_refused_by_naming_the_parameter_and_the_value(string options, string said)
    {
        Assert.False(OfflineReplicaRequests.TryReadRegisterExisting(Fields(("replicaOptions", options)), out _, out string? error));

        Assert.Contains("replicaOptions", error, StringComparison.Ordinal);
        Assert.Contains(said, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_sent_beside_a_registration_are_ignored_because_the_reference_decides()
    {
        Guid id = ReadId("""{"registerExistingData":{"refReplicaId":"75f74eb9-c9aa-4e16-9857-d605b8d3d270"}}""");

        Assert.Equal(Reference, id);
    }

    // ------------------------------------------------------------------------------------------------ registering

    [Fact]
    public void The_new_replica_is_the_callers_with_the_references_layers_and_generations_and_a_different_id()
    {
        PublishedService service = Service();
        ReplicaRecord held = Held(service, Packaged.ToUnixTimeMilliseconds());

        (ReplicaRecord? created, int status, _) = OfflineReplicaEndpoints.Registration(
            held, Reference, service, Packager, "packager", null, Later);

        Assert.Equal(200, status);
        Assert.NotNull(created);
        Assert.NotEqual(Reference, created.Id);
        Assert.Equal(Packager, created.OwnerId);
        Assert.Equal(service.Id, created.ServiceId);
        Assert.Equal("perLayer", created.SyncModel);
        Assert.Equal(held.Layers, created.Layers);
        Assert.Equal(held.Geometry, created.Geometry);
        Assert.Equal(held.Wkid, created.Wkid);
        Assert.True(created.ReturnAttachments);
        Assert.Equal("bidirectional", created.AttachmentsSyncDirection);
        Assert.StartsWith("Area_ab12_", created.Name, StringComparison.Ordinal);
        Assert.True(OfflineReplicaRequests.IsReplicaName(created.Name), created.Name);
        Assert.Equal(Later, created.Created);
    }

    [Fact]
    public void A_member_who_did_not_package_the_area_registers_a_copy_of_their_own()
    {
        PublishedService service = Service();
        ReplicaRecord held = Held(service, Packaged.ToUnixTimeMilliseconds());

        (ReplicaRecord? mine, int status, _) = OfflineReplicaEndpoints.Registration(held, Reference, service, Member, "member", null, Later);
        (ReplicaRecord? yours, _, _) = OfflineReplicaEndpoints.Registration(held, Reference, service, Guid.NewGuid(), "other", null, Later);

        Assert.Equal(200, status);
        Assert.Equal(Member, mine!.OwnerId);
        Assert.NotEqual(mine.Id, yours!.Id);
        Assert.NotEqual(mine.Name, yours.Name);
    }

    [Fact]
    public void A_reference_the_packager_has_synchronised_since_still_starts_the_copy_at_the_packages_moment()
    {
        PublishedService service = Service();
        ReplicaRecord advanced = Held(service, Later.ToUnixTimeMilliseconds());

        (ReplicaRecord? created, _, _) = OfflineReplicaEndpoints.Registration(advanced, Reference, service, Member, "member", null, Later);

        Assert.All(created!.Layers, l => Assert.Equal(Packaged.ToUnixTimeMilliseconds(), l.ServerGeneration));
    }

    [Fact]
    public void A_name_the_client_sends_is_used_and_one_with_a_hyphen_is_refused_by_name()
    {
        PublishedService service = Service();
        ReplicaRecord held = Held(service, Packaged.ToUnixTimeMilliseconds());

        (ReplicaRecord? named, _, _) = OfflineReplicaEndpoints.Registration(held, Reference, service, Member, "member", "My_Phone", Later);
        (ReplicaRecord? refused, int status, string? why) = OfflineReplicaEndpoints.Registration(held, Reference, service, Member, "member", "my-phone", Later);

        Assert.Equal("My_Phone", named!.Name);
        Assert.Null(refused);
        Assert.Equal(400, status);
        Assert.Contains("replicaName 'my-phone'", why, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_reference_is_refused_with_its_id()
    {
        (ReplicaRecord? created, int status, string? why) = OfflineReplicaEndpoints.Registration(
            null, Reference, Service(), Member, "member", null, Later);

        Assert.Null(created);
        Assert.Equal(404, status);
        Assert.Contains("refReplicaId '75F74EB9-C9AA-4E16-9857-D605B8D3D270'", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_of_another_service_is_refused_as_if_it_did_not_exist()
    {
        PublishedService service = Service();
        ReplicaRecord foreign = Held(Service(), Packaged.ToUnixTimeMilliseconds());

        (ReplicaRecord? created, int status, string? why) = OfflineReplicaEndpoints.Registration(
            foreign, Reference, service, Member, "member", null, Later);

        Assert.Null(created);
        Assert.Equal(404, status);
        Assert.Contains("75F74EB9", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_with_a_layer_the_service_no_longer_has_is_refused_by_layer()
    {
        PublishedService service = Service();
        ReplicaRecord held = Held(service, Packaged.ToUnixTimeMilliseconds()) with
        {
            Layers = [new ReplicaLayer(9, Packaged.ToUnixTimeMilliseconds(), "all", string.Empty, false)],
        };

        (_, int status, string? why) = OfflineReplicaEndpoints.Registration(held, Reference, service, Member, "member", null, Later);

        Assert.Equal(409, status);
        Assert.Contains("Layer 9", why, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ the answer and the sync after it

    [Fact]
    public async Task The_answer_is_the_JSON_replica_of_the_new_id_with_the_generations_and_empty_layers()
    {
        PublishedService service = Service();
        (ReplicaRecord? created, _, _) = OfflineReplicaEndpoints.Registration(
            Held(service, Packaged.ToUnixTimeMilliseconds()), Reference, service, Member, "member", "Phone_1", Later);
        string file = Path.Combine(Path.GetTempPath(), "register-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            await OfflineJsonReplica.WriteRegistrationAsync(file, created!, CancellationToken.None);
            JsonElement root = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement;

            Assert.Equal("Phone_1", root.GetProperty("replicaName").GetString());
            Assert.Equal(OfflineReplicaRequests.Wire(created!.Id), root.GetProperty("replicaID").GetString());
            Assert.NotEqual(OfflineReplicaRequests.Wire(Reference), root.GetProperty("replicaID").GetString());
            Assert.Equal("perLayer", root.GetProperty("syncModel").GetString());
            Assert.Equal("esriReplicaResponseTypeData", root.GetProperty("responseType").GetString());

            JsonElement gens = root.GetProperty("layerServerGens");
            Assert.Equal(2, gens.GetArrayLength());
            Assert.All(gens.EnumerateArray(), g => Assert.Equal(Packaged.ToUnixTimeMilliseconds(), g.GetProperty("serverGen").GetInt64()));

            JsonElement layers = root.GetProperty("layers");
            Assert.Equal([0, 1], layers.EnumerateArray().Select(l => l.GetProperty("id").GetInt32()));
            Assert.All(layers.EnumerateArray(), l =>
            {
                Assert.Equal(0, l.GetProperty("features").GetArrayLength());
                Assert.Equal(0, l.GetProperty("attachments").GetArrayLength());
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_synchronizeReplica_with_the_new_id_is_let_through_for_its_owner_and_refused_for_nobody_elses()
    {
        PublishedService service = Service();
        MemoryStore store = new(Held(service, Packaged.ToUnixTimeMilliseconds()));
        (ReplicaRecord? created, _, _) = OfflineReplicaEndpoints.Registration(
            store.Find(Reference), Reference, service, Member, "member", null, Later);
        await store.CreateAsync(created!, CancellationToken.None);

        // The id the device writes into its geodatabase is the new one; what synchronizeReplica reads is the stored record.
        ReplicaRecord? found = await store.FindAsync(created!.Id, CancellationToken.None);

        Assert.Null(OfflineReplicaEndpoints.Gate(found, service.Id, Member, administrator: false, created.Id));
        Assert.Equal(403, OfflineReplicaEndpoints.Gate(found, service.Id, Packager, administrator: false, created.Id)?.Status);
        Assert.Null(OfflineReplicaEndpoints.Gate(found, service.Id, Packager, administrator: true, created.Id));

        // The next download starts at the package's generation for every layer.
        Assert.All(found!.Layers, l => Assert.Equal(Packaged.ToUnixTimeMilliseconds(), l.ServerGeneration));
        Assert.Equal([0, 1], found.Layers.Select(l => l.Id));

        // The packager's own replica is untouched, and the member still cannot sync it.
        Assert.Equal(403, OfflineReplicaEndpoints.Gate(store.Find(Reference), service.Id, Member, administrator: false, Reference)?.Status);
    }

    private sealed class MemoryStore(params ReplicaRecord[] seed) : IOfflineSyncStore
    {
        private readonly List<ReplicaRecord> held = [.. seed];

        public ReplicaRecord? Find(Guid id) => held.FirstOrDefault(r => r.Id == id);

        public Task<int> SetEnabledAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task CreateAsync(ReplicaRecord replica, CancellationToken cancellationToken)
        {
            held.Add(replica);
            return Task.CompletedTask;
        }

        public Task<ReplicaRecord?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(id));

        public Task<IReadOnlyList<ReplicaRecord>> ListAsync(Guid serviceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReplicaRecord>>([.. held.Where(r => r.ServiceId == serviceId)]);

        public Task<bool> AdvanceAsync(Guid id, IReadOnlyList<ReplicaLayer> layers, DateTimeOffset synced, CancellationToken cancellationToken) =>
            Task.FromResult(Find(id) is not null);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(held.RemoveAll(r => r.Id == id) > 0);
    }
}
