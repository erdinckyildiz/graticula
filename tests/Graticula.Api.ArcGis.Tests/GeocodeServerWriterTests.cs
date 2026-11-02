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
}
