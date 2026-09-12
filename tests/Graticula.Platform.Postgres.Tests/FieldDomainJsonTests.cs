using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Graticula.Catalog;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-065: what is stored reads back as what was said, and a stored entry this build cannot read
/// costs its values and not its layer.
/// </summary>
/// <remarks>
/// <b>No database</b>: the shape is a contract between one writer and one reader in the same
/// assembly, and a round trip through the strings they exchange is the whole of it.
/// </remarks>
public sealed class FieldDomainJsonTests
{
    private static LayerSubtypes Kinds() =>
        new(
            "kind",
            2,
            [
                new Subtype(
                    1,
                    "Main",
                    new Dictionary<string, DomainValue> { ["material"] = DomainValue.Of("CU"), ["diameter"] = DomainValue.Of(300.5m) },
                    new Dictionary<string, FieldDomain>
                    {
                        ["diameter"] = FieldDomain.Range("MainDiameter", DomainValue.Of(200), DomainValue.Of(900)),
                    }),
                new Subtype(2, "Lateral", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
            ]);

    [Fact]
    public void Domains_and_subtypes_survive_the_store()
    {
        FieldDomain materials = FieldDomain.Coded(
            "Material", [new CodedValue(DomainValue.Of("CU"), "Copper"), new CodedValue(DomainValue.Of(7), "Seven")]);

        string stored = FieldOverrideJson.Write(
        [
            new FieldOverride("material", "Material", false, Domain: materials),
            new FieldOverride("kind", null, false, Subtypes: Kinds()),
            new FieldOverride("plain", "Plain", false),
        ]);

        ImmutableArray<FieldOverride> read = FieldOverrideJson.Read(stored);

        Assert.Equal(3, read.Length);
        Assert.True(materials.SameAs(read[0].Domain), stored);
        Assert.Equal("Material", read[0].Alias);

        // The stored form does not repeat the column; the reader gives it back.
        Assert.True(Kinds().SameAs(read[1].Subtypes), stored);

        // An entry that says neither stores neither key, so an older build finds nothing to ignore.
        using JsonDocument document = JsonDocument.Parse(stored);
        JsonElement plain = document.RootElement[2];
        Assert.False(plain.TryGetProperty("domain", out _));
        Assert.False(plain.TryGetProperty("subtypes", out _));
    }

    [Fact]
    public void An_unreadable_domain_is_left_out_and_the_label_is_kept()
    {
        ImmutableArray<FieldOverride> read = FieldOverrideJson.Read(
            """[{"column":"material","alias":"Material","hidden":false,"domain":{"type":"inherited"}}]""");

        FieldOverride entry = Assert.Single(read);

        Assert.Equal("Material", entry.Alias);
        Assert.Null(entry.Domain);
    }

    [Fact]
    public void The_admin_reader_says_why_a_body_is_not_a_domain()
    {
        foreach ((string body, string expected) in ((string, string)[])
        [
            ("""{"type":"list","name":"X"}""", "not a domain type"),
            ("""{"type":"codedValue","name":"X"}""", "codedValues"),
            ("""{"type":"codedValue","name":"X","codedValues":[{"code":true,"name":"yes"}]}""", "number or text"),
            ("""{"type":"range","name":"X","range":[1]}""", "range: [least, greatest]"),
            ("""[]""", "is an object"),
        ])
        {
            using JsonDocument document = JsonDocument.Parse(body);

            Assert.Null(FieldDomainJson.ReadDomain(document.RootElement, out string? error));
            Assert.Contains(expected, error!, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Inherited_and_null_in_a_subtype_mean_the_column_keeps_its_own()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {"field":"kind","types":[{"code":3,"name":"Service",
              "domains":{"material":{"type":"inherited"},"diameter":null},
              "defaultValues":{"note":null}}]}
            """);

        LayerSubtypes subtypes = FieldDomainJson.ReadSubtypes("kind", document.RootElement, out string? error)!;

        Assert.Null(error);

        Subtype service = Assert.Single(subtypes.Types);

        Assert.Empty(service.Domains);
        Assert.Empty(service.Defaults);

        // No defaultCode given: the first subtype is the one a client offers first.
        Assert.Equal(3, subtypes.DefaultCode);
    }

    [Fact]
    public void The_written_domain_is_the_arcgis_domain_object()
    {
        string written = JsonSerializer.Serialize(FieldDomainJson.Write(
            FieldDomain.Range("SpeedLimit", DomainValue.Of(40.0m), DomainValue.Of(100))));

        Assert.Equal("""{"type":"range","name":"SpeedLimit","range":[40,100]}""", written);
        Assert.Equal(
            ["type", "name", "codedValues"],
            JsonDocument.Parse(JsonSerializer.Serialize(FieldDomainJson.Write(
                FieldDomain.Coded("C", [new CodedValue(DomainValue.Of(1), "one")])))).RootElement
                .EnumerateObject().Select(p => p.Name));
    }
}
