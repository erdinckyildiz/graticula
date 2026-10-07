using System;
using System.Collections.Generic;
using System.IO;
using Graticula.Platform.Jobs;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Exporting a service's layers as a file — ADR-106: what each format becomes and what it is called, the names layers
/// are given inside it, the estimate, and the file names nothing in a request can reach past.
/// </summary>
public sealed class FeatureExportTests
{
    [Theory]
    [InlineData(FeatureExportFormat.GeoPackage, 1, true, false, ".gpkg")]
    [InlineData(FeatureExportFormat.GeoPackage, 5, true, false, ".gpkg")]
    [InlineData(FeatureExportFormat.Excel, 3, true, false, ".xlsx")]
    [InlineData(FeatureExportFormat.Kml, 3, true, false, ".kml")]
    [InlineData(FeatureExportFormat.FileGeodatabase, 1, true, true, ".gdb.zip")]
    [InlineData(FeatureExportFormat.FileGeodatabase, 4, true, true, ".gdb.zip")]
    [InlineData(FeatureExportFormat.Shapefile, 1, false, true, "-shapefile.zip")]
    [InlineData(FeatureExportFormat.Shapefile, 4, false, true, "-shapefile.zip")]
    [InlineData(FeatureExportFormat.Csv, 1, false, false, ".csv")]
    [InlineData(FeatureExportFormat.Csv, 2, false, true, "-csv.zip")]
    [InlineData(FeatureExportFormat.GeoJson, 1, false, false, ".geojson")]
    [InlineData(FeatureExportFormat.GeoJson, 2, false, true, "-geojson.zip")]
    [InlineData(FeatureExportFormat.EsriJson, 1, false, false, ".json")]
    [InlineData(FeatureExportFormat.EsriJson, 2, false, true, "-featurecollection.zip")]
    public void Four_formats_hold_every_layer_in_one_file_and_the_rest_write_a_file_per_layer_in_a_zip_when_there_are_several(
        FeatureExportFormat format, int layers, bool oneDataset, bool zipped, string suffix)
    {
        FeatureExportPackaging packaging = FeatureExportPackaging.Of(format, layers);

        Assert.Equal(oneDataset, packaging.OneDataset);
        Assert.Equal(zipped, packaging.Zipped);
        Assert.Equal(suffix, packaging.Suffix);
        Assert.Equal(oneDataset, FeatureExportPackaging.Appends(format));
    }

    [Fact]
    public void A_zip_is_served_as_a_zip_and_a_bare_file_as_what_it_is()
    {
        Assert.Equal("application/zip", FeatureExportPackaging.Of(FeatureExportFormat.Csv, 2).MediaType);
        Assert.Equal("text/csv; charset=utf-8", FeatureExportPackaging.Of(FeatureExportFormat.Csv, 1).MediaType);
        Assert.Equal("application/geo+json", FeatureExportPackaging.Of(FeatureExportFormat.GeoJson, 1).MediaType);
        Assert.Equal("application/json", FeatureExportPackaging.Of(FeatureExportFormat.EsriJson, 1).MediaType);
        Assert.Equal("application/geopackage+sqlite3", FeatureExportPackaging.Of(FeatureExportFormat.GeoPackage, 3).MediaType);
        Assert.Equal("application/vnd.google-earth.kml+xml", FeatureExportPackaging.Of(FeatureExportFormat.Kml, 3).MediaType);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FeatureExportPackaging.Of(FeatureExportFormat.Excel, 3).MediaType);
        Assert.Equal("application/zip", FeatureExportPackaging.Of(FeatureExportFormat.Shapefile, 1).MediaType);
    }

    [Fact]
    public void Every_format_the_job_stores_has_a_packaging_and_all_but_esri_json_are_written_by_the_reader()
    {
        foreach (FeatureExportFormat format in Enum.GetValues<FeatureExportFormat>())
        {
            Assert.NotEmpty(FeatureExportPackaging.Of(format, 1).Suffix);

            if (format == FeatureExportFormat.EsriJson)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => FeatureExportPackaging.ReaderToken(format));
            }
            else
            {
                Assert.NotEmpty(FeatureExportPackaging.ReaderToken(format));
            }
        }
    }

    [Theory]
    [InlineData("gpkg", FeatureExportFormat.GeoPackage)]
    [InlineData("geoPackage", FeatureExportFormat.GeoPackage)]
    [InlineData("shapefile", FeatureExportFormat.Shapefile)]
    [InlineData("Shapefile", FeatureExportFormat.Shapefile)]
    [InlineData("xlsx", FeatureExportFormat.Excel)]
    [InlineData("Excel", FeatureExportFormat.Excel)]
    [InlineData("fgdb", FeatureExportFormat.FileGeodatabase)]
    [InlineData("File Geodatabase", FeatureExportFormat.FileGeodatabase)]
    [InlineData("file-geodatabase", FeatureExportFormat.FileGeodatabase)]
    [InlineData("kml", FeatureExportFormat.Kml)]
    [InlineData("KML", FeatureExportFormat.Kml)]
    [InlineData("csv", FeatureExportFormat.Csv)]
    [InlineData("CSV", FeatureExportFormat.Csv)]
    [InlineData("geojson", FeatureExportFormat.GeoJson)]
    [InlineData("GeoJson", FeatureExportFormat.GeoJson)]
    [InlineData("esrijson", FeatureExportFormat.EsriJson)]
    [InlineData("Feature Collection", FeatureExportFormat.EsriJson)]
    [InlineData("featureCollection", FeatureExportFormat.EsriJson)]
    public void This_servers_tokens_and_ArcGIS_Onlines_spellings_name_the_same_eight_formats(string text, FeatureExportFormat format) =>
        Assert.Equal(format, FeatureExportPackaging.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("dxf")]
    [InlineData("pdf")]
    [InlineData("../shapefile/x")]
    public void Anything_else_is_no_format(string? text) => Assert.Null(FeatureExportPackaging.Parse(text));

    [Fact]
    public void A_download_is_named_for_the_service_safe_for_a_file_system()
    {
        Assert.Equal("EarlyAlert.gpkg", FeatureExportPackaging.DownloadName("EarlyAlert", FeatureExportFormat.GeoPackage, 3));
        Assert.Equal("Early_Alert__1_.gdb.zip", FeatureExportPackaging.DownloadName("Early Alert (1)", FeatureExportFormat.FileGeodatabase, 1));
        Assert.Equal("roads-csv.zip", FeatureExportPackaging.DownloadName("roads", FeatureExportFormat.Csv, 2));
        Assert.Equal("roads.csv", FeatureExportPackaging.DownloadName("roads", FeatureExportFormat.Csv, 1));

        // A Turkish name keeps its letters; the download header carries them as RFC 5987.
        Assert.Equal("Şehir_Yolları.xlsx", FeatureExportPackaging.DownloadName("Şehir Yolları", FeatureExportFormat.Excel, 2));

        // Nothing of a service's name is a path.
        string hostile = FeatureExportPackaging.DownloadName("../../etc/passwd", FeatureExportFormat.GeoPackage, 1);
        Assert.DoesNotContain('/', hostile);
        Assert.DoesNotContain('\\', hostile);
        Assert.DoesNotContain("..", hostile, StringComparison.Ordinal);

        Assert.Equal("___.gpkg", FeatureExportPackaging.DownloadName("///", FeatureExportFormat.GeoPackage, 1));
        Assert.Equal("export.gpkg", FeatureExportPackaging.DownloadName(string.Empty, FeatureExportFormat.GeoPackage, 1));
    }

    [Fact]
    public void Layer_names_are_unique_ignoring_case_and_the_first_keeps_its_name()
    {
        Assert.Equal(
            ["Roads", "roads_2", "Roads_3", "Sites"],
            FeatureExportPackaging.LayerNames(["Roads", "roads", "Roads", "Sites"], FeatureExportFormat.GeoPackage));

        // The rule is the same for the same input every time.
        Assert.Equal(
            FeatureExportPackaging.LayerNames(["a b", "a_b", "A_B"], FeatureExportFormat.Shapefile),
            FeatureExportPackaging.LayerNames(["a b", "a_b", "A_B"], FeatureExportFormat.Shapefile));
        Assert.Equal(["a_b", "a_b_2", "A_B_3"], FeatureExportPackaging.LayerNames(["a b", "a_b", "A_B"], FeatureExportFormat.Shapefile));
    }

    [Fact]
    public void A_file_geodatabase_gets_letters_digits_and_underscores_and_never_starts_with_a_digit()
    {
        Assert.Equal(
            ["L2024_sites", "Sehir_Yollari", "a_b_c", "layer"],
            FeatureExportPackaging.LayerNames(
                ["2024 sites", "Sehir_Yollari", "a-b.c", "layer"], FeatureExportFormat.FileGeodatabase));

        // Turkish letters are letters.
        Assert.Equal(["Şehir_Yolları"], FeatureExportPackaging.LayerNames(["Şehir Yolları"], FeatureExportFormat.FileGeodatabase));

        // A name of nothing legal is still a name.
        Assert.Equal(["L", "L_2"], FeatureExportPackaging.LayerNames(["", ""], FeatureExportFormat.FileGeodatabase));
        Assert.Equal(["layer", "layer_2"], FeatureExportPackaging.LayerNames(["", ""], FeatureExportFormat.GeoPackage));
    }

    [Fact]
    public void A_worksheet_name_is_thirty_one_characters_and_a_collision_keeps_its_suffix()
    {
        string longName = new('x', 40);

        IReadOnlyList<string> names = FeatureExportPackaging.LayerNames([longName, longName, longName], FeatureExportFormat.Excel);

        Assert.All(names, n => Assert.True(n.Length <= 31, n));
        Assert.Equal(new string('x', 31), names[0]);
        Assert.Equal(new string('x', 29) + "_2", names[1]);
        Assert.Equal(new string('x', 29) + "_3", names[2]);

        // The characters a sheet may not have.
        Assert.Equal(["a_b_c_d_e_f_g_h"], FeatureExportPackaging.LayerNames(["a[b]c:d*e?f/g\\h"], FeatureExportFormat.Excel));
    }

    [Fact]
    public void The_estimate_scales_the_sample_to_the_rows_doubles_it_and_adds_a_layers_overhead()
    {
        // 500 rows weighed 250,000 bytes: 500 a row. 10,000 rows: 5,000,000, twice for staging plus output.
        Assert.Equal((500L * 10_000 * 2) + FeatureExportPackaging.PerLayerOverhead, FeatureExportPackaging.EstimateBytes(250_000, 500, 10_000));

        // Rounds a row up rather than down: an estimate errs high.
        Assert.Equal((2L * 3 * 2) + FeatureExportPackaging.PerLayerOverhead, FeatureExportPackaging.EstimateBytes(5, 3, 3));

        // A layer with nothing sampled weighs nothing but its overhead.
        Assert.Equal(FeatureExportPackaging.PerLayerOverhead, FeatureExportPackaging.EstimateBytes(0, 0, 0));
        Assert.Equal(FeatureExportPackaging.PerLayerOverhead, FeatureExportPackaging.EstimateBytes(0, 0, 500));
    }

    /// <summary>
    /// A wider format is estimated wider — ADR-106 condition 5: a 300,000-row KML estimated as GeoJSON wrote 181 MB against
    /// 143 MB counted, and was stopped for it.
    /// </summary>
    [Fact]
    public void Kml_and_esri_json_are_estimated_wider_than_geojson_and_kml_has_a_largest_size()
    {
        Assert.Equal(3.0, FeatureExportPackaging.OutputWidth(FeatureExportFormat.Kml));
        Assert.Equal(1.5, FeatureExportPackaging.OutputWidth(FeatureExportFormat.EsriJson));

        foreach (FeatureExportFormat narrower in (FeatureExportFormat[])
            [FeatureExportFormat.GeoPackage, FeatureExportFormat.Shapefile, FeatureExportFormat.Excel, FeatureExportFormat.FileGeodatabase,
             FeatureExportFormat.Csv, FeatureExportFormat.GeoJson])
        {
            Assert.Equal(1.0, FeatureExportPackaging.OutputWidth(narrower));
        }

        // Staging plus three times the staging: 500 a row, 10,000 rows.
        Assert.Equal((500L * 10_000 * 4) + FeatureExportPackaging.PerLayerOverhead, FeatureExportPackaging.EstimateBytes(250_000, 500, 10_000, 3.0));
        Assert.Equal(500L * 10_000 * 3, FeatureExportPackaging.OutputBytes(250_000, 500, 10_000, 3.0));

        // The measurements behind the limit: 200,000 points wrote a 127 MB KML at a 1.06 GB peak; 300,000 were refused.
        Assert.Equal(150L * 1024 * 1024, FeatureExportPackaging.KmlLargestBytes);
    }

    [Fact]
    public void A_feature_export_is_rerun_from_the_start_and_is_harmless_to_repeat() =>
        Assert.Equal(JobRerun.Harmless, JobKinds.RerunOf(JobKind.FeatureExport));

    [Fact]
    public void The_defaults_are_two_million_rows_and_an_hour()
    {
        Assert.Equal(2_000_000, FeatureExporter.DefaultMaximumRows);
        Assert.Equal(60, FeatureExporter.DefaultTimeoutMinutes);
    }

    [Fact]
    public void A_refusal_for_rows_names_the_cap_or_the_sheet_it_is_about()
    {
        Assert.Contains("2,000,000", FeatureExporter.RefusedForRows("roads", FeatureExportFormat.GeoPackage, 2_000_000, 500, 500), StringComparison.Ordinal);
        Assert.Contains("FeatureExportMaxRows", FeatureExporter.RefusedForRows("roads", FeatureExportFormat.GeoPackage, 2_000_000, 500, 500), StringComparison.Ordinal);

        string sheet = FeatureExporter.RefusedForRows("roads", FeatureExportFormat.Excel, 2_000_000, 1_500_000, 1_048_575);
        Assert.Contains("1,048,575", sheet, StringComparison.Ordinal);
        Assert.Contains("'roads'", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void A_files_path_is_the_token_inside_its_own_directory_and_nothing_else()
    {
        string directory = Path.Combine(Path.GetTempPath(), "graticula-fexport-" + Guid.NewGuid().ToString("N"), "data");
        string token = TileExporter.NewToken();

        Assert.Equal(Path.Combine(Path.GetFullPath(directory), token + ".export"), FeatureExporter.FileOf(directory, token));
        Assert.Equal(Path.Combine(Path.GetFullPath(directory), token + ".part"), FeatureExporter.PartOf(directory, token));
        Assert.Equal(Path.Combine(Path.GetFullPath(directory), token + ".staging"), FeatureExporter.StagingOf(directory, token));

        foreach (string hostile in new[]
                 {
                     "../../etc/passwd", "..", "", "ABCDEF0123456789ABCDEF0123456789", token + "/x",
                     token[..31], token + "0", "0123456789abcdef0123456789abcde\\", "0123456789abcdef0123456789abc../",
                 })
        {
            Assert.Null(FeatureExporter.FileOf(directory, hostile));
            Assert.Null(FeatureExporter.PartOf(directory, hostile));
            Assert.Null(FeatureExporter.StagingOf(directory, hostile));
        }
    }

    // 32 zero bytes, base64: valid AES-256 and obviously not a real key.
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static HostSettings Read(Dictionary<string, string?> more)
    {
        Dictionary<string, string?> values = new()
        {
            ["Graticula:PlatformStore"] = "Host=localhost;Database=gis",
            ["Graticula:SecretKey"] = Key,
        };

        foreach ((string name, string? value) in more)
        {
            values[name] = value;
        }

        return HostSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public void Feature_exports_live_in_a_folder_the_tile_exporters_stray_sweep_does_not_read()
    {
        HostSettings settings = Read([]);

        Assert.Equal(Path.Combine(settings.TileExportDirectory, "data"), settings.FeatureExportDirectory);
        Assert.Equal(
            Path.GetFullPath(settings.TileExportDirectory),
            Path.GetDirectoryName(Path.GetFullPath(settings.FeatureExportDirectory)));
    }

    [Fact]
    public void The_cap_and_the_deadline_have_their_defaults_and_can_be_set_but_never_below_one()
    {
        HostSettings settings = Read([]);

        Assert.Equal(2_000_000, settings.FeatureExportMaximumRows);
        Assert.Equal(TimeSpan.FromMinutes(60), settings.FeatureExportTimeout);

        settings = Read(new() { ["Graticula:FeatureExportMaxRows"] = "5000", ["Graticula:FeatureExportTimeoutMinutes"] = "5" });

        Assert.Equal(5000, settings.FeatureExportMaximumRows);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.FeatureExportTimeout);

        settings = Read(new() { ["Graticula:FeatureExportMaxRows"] = "0", ["Graticula:FeatureExportTimeoutMinutes"] = "-3" });

        Assert.Equal(1, settings.FeatureExportMaximumRows);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.FeatureExportTimeout);
    }

    [Fact]
    public void One_budget_is_named_ExportBudgetMB_and_the_tile_exports_name_for_it_is_still_read()
    {
        Assert.Equal(10L * 1024 * 1024 * 1024, Read([]).TileExportBudgetBytes);
        Assert.Equal(512L * 1024 * 1024, Read(new() { ["Graticula:TileExportBudgetMB"] = "512" }).TileExportBudgetBytes);
        Assert.Equal(256L * 1024 * 1024, Read(new() { ["Graticula:ExportBudgetMB"] = "256" }).TileExportBudgetBytes);

        // With both set, the new name wins.
        Assert.Equal(
            256L * 1024 * 1024,
            Read(new() { ["Graticula:ExportBudgetMB"] = "256", ["Graticula:TileExportBudgetMB"] = "512" }).TileExportBudgetBytes);
    }
}
