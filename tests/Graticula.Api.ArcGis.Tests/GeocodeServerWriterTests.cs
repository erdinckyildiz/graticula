using System.Collections.Generic;
using Graticula.Geocoding;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>The score rule and the labels of ADR-258 §5, on the places the VPS measurement found.</summary>
public sealed class GeocodeServerWriterTests
{
    private static readonly GeocodePlace Istiklal = new(28.98, 41.03, GeocodePlaceKind.Street, "W1")
    {
        Name = "İstiklal Caddesi",
        Street = "İstiklal Caddesi",
        Neighborhood = "Kuloğlu",
        District = "Beyoğlu",
        City = "İstanbul",
        Country = "Türkiye",
        CountryCode = "TR",
    };

    private static readonly GeocodePlace BusStop = new(32.855, 39.915, GeocodePlaceKind.PointOfInterest, "N555")
    {
        Name = "Atatürk Bulvarı",
        Street = "Atatürk Bulvarı",
        District = "Çankaya",
        County = "Çankaya",
        City = "Ankara",
        Country = "Türkiye",
    };

    [Fact]
    public void Every_word_found_after_folding_and_by_prefix_scores_100()
    {
        // "cad" begins "Caddesi", "beyoglu" is "Beyoğlu" folded, "istiklal" is "İstiklal" folded.
        Assert.Equal(100, GeocodeServerWriter.Score("istiklal cad beyoglu", Istiklal));
    }

    [Fact]
    public void A_house_number_that_was_not_found_costs_its_share()
    {
        // Four words; the stop on the boulevard carries three of them and not 100.
        Assert.Equal(75, GeocodeServerWriter.Score("Atatürk Bulvarı 100 Ankara", BusStop));
    }

    [Fact]
    public void Nothing_in_common_scores_zero_and_an_empty_request_scores_zero()
    {
        Assert.Equal(0, GeocodeServerWriter.Score("Trabzon", BusStop));
        Assert.Equal(0, GeocodeServerWriter.Score("  , ", BusStop));
    }

    [Fact]
    public void A_label_says_a_repeated_part_once_and_a_street_by_its_street()
    {
        (string longLabel, string match, string shortLabel) = GeocodeServerWriter.Labels(BusStop);

        Assert.Equal("Atatürk Bulvarı, Çankaya, Ankara, Türkiye", longLabel);
        Assert.Equal("Atatürk Bulvarı, Çankaya, Ankara", match);
        Assert.Equal("Atatürk Bulvarı", shortLabel);

        Assert.Equal("İstiklal Caddesi, Kuloğlu, Beyoğlu, İstanbul, Türkiye", GeocodeServerWriter.Labels(Istiklal).Long);
    }

    [Theory]
    [InlineData(GeocodePlaceKind.House, "PointAddress")]
    [InlineData(GeocodePlaceKind.PointOfInterest, "POI")]
    [InlineData(GeocodePlaceKind.Street, "StreetName")]
    [InlineData(GeocodePlaceKind.Locality, "Locality")]
    [InlineData(GeocodePlaceKind.Postcode, "Postal")]
    public void Each_kind_has_the_references_match_type(GeocodePlaceKind kind, string expected) =>
        Assert.Equal(expected, GeocodeServerWriter.MatchType(kind));

    [Fact]
    public void Out_fields_choose_by_name_without_regard_to_case_and_star_chooses_all()
    {
        Dictionary<string, object?> all = GeocodeServerWriter.Attributes(
            Istiklal, 100, 28.98, 41.03, GeocodeServerWriter.ExtentOf(Istiklal), null);

        Assert.Equal(["Score", "Match_addr"], GeocodeServerWriter.Choose(all, null).Keys);
        Assert.Equal(["City", "Region"], GeocodeServerWriter.Choose(all, "region, city, nonsense").Keys);
        Assert.Equal(all.Count, GeocodeServerWriter.Choose(all, "*").Count);
    }

    [Fact]
    public void A_place_without_an_extent_gets_a_box_sized_by_what_it_is()
    {
        GeocodeBox street = GeocodeServerWriter.ExtentOf(Istiklal);
        GeocodeBox stop = GeocodeServerWriter.ExtentOf(BusStop);

        Assert.True(street.East - street.West > stop.East - stop.West);
        Assert.True(stop.West < BusStop.Longitude && BusStop.Longitude < stop.East);
    }

    [Fact]
    public void Distance_is_great_circle_metres()
    {
        // Kızılay to Kadıköy is about 350 km.
        double metres = GeocodeServerWriter.DistanceMeters(32.8541, 39.9208, 29.0244, 40.9903);
        Assert.InRange(metres, 340_000, 360_000);
    }

    [Fact]
    public void Categories_map_onto_kinds_a_parent_meaning_its_children_and_an_unmapped_one_is_refused_by_name()
    {
        Assert.True(GeocodeServerWriter.TryCategories(null, out IReadOnlySet<GeocodePlaceKind>? none, out _));
        Assert.Null(none);

        Assert.True(GeocodeServerWriter.TryCategories("Address,postal", out IReadOnlySet<GeocodePlaceKind>? kinds, out _));
        Assert.True(kinds!.SetEquals([GeocodePlaceKind.House, GeocodePlaceKind.Street, GeocodePlaceKind.Postcode]));

        Assert.True(GeocodeServerWriter.TryCategories("Street Name", out kinds, out _));
        Assert.True(kinds!.SetEquals([GeocodePlaceKind.Street]));

        Assert.False(GeocodeServerWriter.TryCategories("POI,Coffee Shop,MGRS", out _, out string? refusal));
        Assert.Contains("'Coffee Shop', 'MGRS'", refusal, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Source_country_takes_two_letter_codes_and_says_why_it_will_not_take_three()
    {
        Assert.True(GeocodeServerWriter.TryCountries("tr, de", out IReadOnlySet<string>? codes, out _));
        Assert.True(codes!.SetEquals(["TR", "DE"]));

        Assert.False(GeocodeServerWriter.TryCountries("TUR", out _, out string? refusal));
        Assert.Contains("alpha-2", refusal, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_record_carries_its_objectid_as_result_id_and_an_unmatched_one_is_empty_and_u()
    {
        Dictionary<string, object?> unmatched = GeocodeServerWriter.BatchAttributes(4L, GeocodeServerWriter.UnmatchedAttributes(), null);

        Assert.Equal(4L, unmatched["ResultID"]);
        Assert.Equal("U", unmatched["Status"]);
        Assert.Equal(0.0, unmatched["Score"]);
        Assert.Equal(string.Empty, unmatched["Match_addr"]);
        Assert.Equal(0.0, unmatched["Xmax"]);
        Assert.Equal(GeocodeServerWriter.CandidateFields.Count + 1, unmatched.Count);

        Dictionary<string, object?> chosen =
            GeocodeServerWriter.BatchAttributes("a", GeocodeServerWriter.UnmatchedAttributes(), "City,Score");
        Assert.Equal(["ResultID", "Score", "City"], [.. chosen.Keys]);
    }

    [Fact]
    public void The_service_document_advertises_the_batch_size_and_the_categories_it_filters_by()
    {
        Dictionary<string, object?> document = GeocodeServerWriter.ServiceDocument(11.2, "Photon", 200, 100);
        Dictionary<string, object?> properties = (Dictionary<string, object?>)document["locatorProperties"]!;

        Assert.Equal(200, properties["MaxBatchSize"]);
        Assert.Equal(100, properties["SuggestedBatchSize"]);

        // As Esri's World locator, which takes batches, answers: no Batch among the capabilities.
        Assert.Equal("Geocode,ReverseGeocode,Suggest", document["capabilities"]);
        Assert.Equal(4, ((object[])document["categories"]!).Length);
    }
}
