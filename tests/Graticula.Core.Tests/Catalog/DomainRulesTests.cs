using System;
using System.Collections.Generic;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Catalog;

/// <summary>
/// ADR-065: what a domain may say about a column, and whether a written value obeys it.
/// </summary>
/// <remarks>
/// <b>The rules both directions read.</b> The admin surface and the importer ask whether a domain
/// fits before storing it; the writer asks whether a value obeys what was stored. A domain that
/// passes the first and refuses every value at the second is the defect these exist to catch.
/// </remarks>
public sealed class DomainRulesTests
{
    private static readonly FieldDescription Status = new("status", FieldType.SmallInteger, true, null);
    private static readonly FieldDescription Material = new("material", FieldType.Text, true, 8);
    private static readonly FieldDescription Speed = new("speed", FieldType.Integer, true, null);
    private static readonly FieldDescription Ratio = new("ratio", FieldType.Single, true, null);
    private static readonly FieldDescription Inspected = new("inspected", FieldType.Date, true, null);

    private static FieldDomain Coded(params (object Code, string Name)[] codes)
    {
        List<CodedValue> values = [];

        foreach ((object code, string name) in codes)
        {
            values.Add(new CodedValue(
                code is string text ? DomainValue.Of(text) : DomainValue.Of(Convert.ToDecimal(code, System.Globalization.CultureInfo.InvariantCulture)),
                name));
        }

        return FieldDomain.Coded("Codes", values);
    }

    [Fact]
    public void A_coded_domain_allows_its_codes_and_refuses_the_rest_by_name()
    {
        FieldDomain domain = Coded((1, "Open"), (2, "Closed"));

        Assert.Null(DomainRules.Refusal(Status, (short)1, domain));
        Assert.Null(DomainRules.Refusal(Status, (short)2, domain));

        string? refused = DomainRules.Refusal(Status, (short)3, domain);

        Assert.NotNull(refused);
        Assert.Contains("'status' is 3", refused, StringComparison.Ordinal);
        Assert.Contains("'Codes'", refused, StringComparison.Ordinal);
        Assert.Contains("1 (Open), 2 (Closed)", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_codes_are_compared_exactly()
    {
        FieldDomain domain = Coded(("CU", "Copper"), ("AL", "Aluminium"));

        Assert.Null(DomainRules.Refusal(Material, "CU", domain));
        Assert.NotNull(DomainRules.Refusal(Material, "cu", domain));
        Assert.NotNull(DomainRules.Refusal(Material, "CU ", domain));
    }

    [Fact]
    public void A_null_is_never_a_domain_s_to_refuse()
    {
        // Nullability is the column's, and the database enforces it.
        Assert.Null(DomainRules.Refusal(Status, null, Coded((1, "Open"))));
        Assert.Null(DomainRules.Refusal(Status, DBNull.Value, Coded((1, "Open"))));
    }

    [Fact]
    public void A_range_includes_both_bounds()
    {
        FieldDomain domain = FieldDomain.Range("SpeedLimit", DomainValue.Of(40), DomainValue.Of(100));

        Assert.Null(DomainRules.Refusal(Speed, 40, domain));
        Assert.Null(DomainRules.Refusal(Speed, 100, domain));

        string? refused = DomainRules.Refusal(Speed, 101, domain);

        Assert.NotNull(refused);
        Assert.Contains("outside the domain 'SpeedLimit'", refused, StringComparison.Ordinal);
        Assert.Contains("40 to 100", refused, StringComparison.Ordinal);
        Assert.NotNull(DomainRules.Refusal(Speed, 39, domain));
    }

    [Fact]
    public void A_single_precision_column_is_compared_in_its_own_precision()
    {
        // 1.1 stored in a `real` column reads back as 1.10000002; a bound or a code of 1.1 must
        // still admit it, or a value read from the table is refused when it is written back.
        Assert.Null(DomainRules.Refusal(
            Ratio, 1.1f, FieldDomain.Range("R", DomainValue.Of(0.5m), DomainValue.Of(1.1m))));

        Assert.Null(DomainRules.Refusal(Ratio, 1.1f, Coded((1.1m, "one point one"))));
    }

    [Fact]
    public void A_date_range_compares_epoch_milliseconds_and_says_dates()
    {
        long start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long end = new DateTimeOffset(2020, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        FieldDomain domain = FieldDomain.Range("Season", DomainValue.Of(start), DomainValue.Of(end));

        // The ArcGIS converter hands over an unspecified kind for what it read as UTC.
        Assert.Null(DomainRules.Refusal(Inspected, new DateTime(2020, 6, 1), domain));

        string? refused = DomainRules.Refusal(Inspected, new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc), domain);

        Assert.NotNull(refused);
        Assert.Contains("2020-01-01T00:00:00Z to 2020-12-31T00:00:00Z", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_domain_that_cannot_govern_its_column_is_refused_with_the_reason()
    {
        Assert.Contains("sent to ArcGIS clients as text",
            DomainRules.Refuse(Coded((1, "one")), FieldType.BigInteger, null)!, StringComparison.Ordinal);

        Assert.Contains("a range bounds a number or a date",
            DomainRules.Refuse(FieldDomain.Range("R", DomainValue.Of(1), DomainValue.Of(2)), FieldType.Text, null)!,
            StringComparison.Ordinal);

        Assert.Contains("takes a range",
            DomainRules.Refuse(Coded((1, "one")), FieldType.Date, null)!, StringComparison.Ordinal);

        Assert.Contains("is text, and this is a short integer column",
            DomainRules.Refuse(Coded(("A", "a")), FieldType.SmallInteger, null)!, StringComparison.Ordinal);

        Assert.Contains("not a whole number",
            DomainRules.Refuse(Coded((1.5m, "one and a half")), FieldType.Integer, null)!, StringComparison.Ordinal);

        Assert.Contains("does not fit a short integer column",
            DomainRules.Refuse(Coded((40_000, "too big")), FieldType.SmallInteger, null)!, StringComparison.Ordinal);

        Assert.Contains("the column holds at most 8",
            DomainRules.Refuse(Coded(("TOO LONG A CODE", "long")), FieldType.Text, 8)!, StringComparison.Ordinal);

        Assert.Contains("listed twice",
            DomainRules.Refuse(Coded((1, "one"), (1, "uno")), FieldType.Integer, null)!, StringComparison.Ordinal);

        Assert.Contains("given to two codes",
            DomainRules.Refuse(Coded((1, "one"), (2, "one")), FieldType.Integer, null)!, StringComparison.Ordinal);

        Assert.Contains("allow nothing",
            DomainRules.Refuse(FieldDomain.Range("R", DomainValue.Of(10), DomainValue.Of(1)), FieldType.Integer, null)!,
            StringComparison.Ordinal);

        Assert.Contains("at least one value",
            DomainRules.Refuse(FieldDomain.Coded("Empty", []), FieldType.Integer, null)!, StringComparison.Ordinal);

        Assert.Contains("no name",
            DomainRules.Refuse(FieldDomain.Coded(" ", [new CodedValue(DomainValue.Of(1), "one")]), FieldType.Integer, null)!,
            StringComparison.Ordinal);

        Assert.Null(DomainRules.Refuse(Coded((1, "one"), (2, "two")), FieldType.Integer, null));
        Assert.Null(DomainRules.Refuse(Coded((1.5m, "one and a half")), FieldType.Double, null));
    }

    [Fact]
    public void A_code_written_with_trailing_zeros_is_the_same_code()
    {
        Assert.Equal(DomainValue.Of(1.5m), DomainValue.Of(1.50m));
        Assert.Equal("1.5", DomainValue.Of(1.500m).ToString());

        // The normalisation must not overflow at the edge of what a decimal holds.
        Assert.Equal(decimal.MaxValue, DomainValue.Of(decimal.MaxValue).Number);
    }

    [Fact]
    public void A_subtype_column_takes_only_its_subtype_codes()
    {
        LayerSubtypes subtypes = Pipes();

        Assert.Null(DomainRules.SubtypeRefusal(subtypes, 1));
        Assert.Null(DomainRules.SubtypeRefusal(subtypes, (short)2));
        Assert.Null(DomainRules.SubtypeRefusal(subtypes, null));

        string? refused = DomainRules.SubtypeRefusal(subtypes, 7);

        Assert.NotNull(refused);
        Assert.Contains("not one of this layer's subtypes", refused, StringComparison.Ordinal);
        Assert.Contains("1 (Main), 2 (Lateral)", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subtype_s_domain_replaces_the_column_s_for_its_features_only()
    {
        LayerSubtypes subtypes = Pipes();
        FieldDomain own = Coded(("CU", "Copper"), ("PVC", "PVC"));

        Assert.Same(own, subtypes.DomainFor("material", null, own));
        Assert.Same(own, subtypes.DomainFor("material", 2, own));

        FieldDomain main = subtypes.DomainFor("material", 1, own)!;

        Assert.NotSame(own, main);

        string? refused = DomainRules.Refusal(Material, "PVC", main, subtypes.Find(1));

        Assert.NotNull(refused);
        Assert.Contains("for subtype 1 (Main)", refused, StringComparison.Ordinal);
        Assert.Null(DomainRules.Refusal(Material, "PVC", own));
    }

    [Fact]
    public void Subtypes_are_judged_against_the_table_before_they_are_stored()
    {
        LayerDescription table = new(
            [
                new FieldDescription("objectid", FieldType.Integer, false, null),
                new FieldDescription("kind", FieldType.SmallInteger, true, null),
                new FieldDescription("label", FieldType.Text, true, null),
                new FieldDescription("material", FieldType.Text, true, 8),
            ],
            new Envelope(0, 0, 1, 1));

        static string? Unlocked(string column) => column == "objectid" ? "it is this layer's object id." : null;
        static FieldDomain? None(string column) => null;

        Assert.Null(DomainRules.Refuse(Pipes(), table, Unlocked, None));

        Assert.Contains("is a text column",
            DomainRules.Refuse(Pipes() with { Field = "label" }, table, Unlocked, None)!, StringComparison.Ordinal);

        Assert.Contains("not a column",
            DomainRules.Refuse(Pipes() with { Field = "nope" }, table, Unlocked, None)!, StringComparison.Ordinal);

        Assert.Contains("default subtype 9",
            DomainRules.Refuse(Pipes() with { DefaultCode = 9 }, table, Unlocked, None)!, StringComparison.Ordinal);

        LayerSubtypes twice = Pipes() with { Types = [.. Pipes().Types, Pipes().Types[0]] };
        Assert.Contains("given twice", DomainRules.Refuse(twice, table, Unlocked, None)!, StringComparison.Ordinal);

        // A default the subtype's own domain does not allow is refused where it is set.
        LayerSubtypes badDefault = Pipes() with
        {
            Types =
            [
                Pipes().Types[0] with
                {
                    Defaults = new Dictionary<string, DomainValue> { ["material"] = DomainValue.Of("PVC") },
                },
                Pipes().Types[1],
            ],
        };

        Assert.Contains("which its domain 'MainMaterial' does not allow",
            DomainRules.Refuse(badDefault, table, Unlocked, None)!, StringComparison.Ordinal);

        // The subtype column cannot be governed by a subtype, and the identity cannot take a domain.
        LayerSubtypes onItself = Pipes() with
        {
            Types =
            [
                Pipes().Types[0] with
                {
                    Domains = new Dictionary<string, FieldDomain> { ["kind"] = Coded((1, "one")) },
                },
            ],
        };

        Assert.Contains("the subtype column itself",
            DomainRules.Refuse(onItself, table, Unlocked, None)!, StringComparison.Ordinal);

        LayerSubtypes onIdentity = Pipes() with
        {
            Types =
            [
                Pipes().Types[0] with
                {
                    Defaults = new Dictionary<string, DomainValue> { ["objectid"] = DomainValue.Of(1) },
                },
            ],
        };

        Assert.Contains("object id", DomainRules.Refuse(onIdentity, table, Unlocked, None)!, StringComparison.Ordinal);
    }

    /// <summary>Two subtypes of a pipe, the first narrowing what it may be made of.</summary>
    private static LayerSubtypes Pipes() =>
        new(
            "kind",
            1,
            [
                new Subtype(
                    1,
                    "Main",
                    new Dictionary<string, DomainValue> { ["material"] = DomainValue.Of("CU") },
                    new Dictionary<string, FieldDomain>
                    {
                        ["material"] = FieldDomain.Coded("MainMaterial", [new CodedValue(DomainValue.Of("CU"), "Copper")]),
                    }),
                new Subtype(2, "Lateral", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
            ]);
}
