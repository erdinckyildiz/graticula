using System;
using MaxRev.Gdal.Core;
using OSGeo.OGR;
using OSGeo.OSR;
using Driver = OSGeo.OGR.Driver;
using Dataset = OSGeo.GDAL.Dataset;
using GDALVectorTranslateOptions = OSGeo.GDAL.GDALVectorTranslateOptions;

// One question: does GDAL's managed binding read the owner's geodatabase, and what drivers does the
// build carry? Everything else about ADR-037 depends on the answer.
GdalBase.ConfigureAll();

Console.WriteLine($"GDAL {OSGeo.GDAL.Gdal.VersionInfo("RELEASE_NAME")}");

// Which drivers are here matters as much as whether it opens: OpenFileGDB is the reader and Parquet
// is the boundary Q-74 chose. A build without Parquet changes the design rather than the version.
foreach (string want in new[] { "OpenFileGDB", "Parquet", "FlatGeobuf", "GeoJSON", "ESRI Shapefile" })
{
    Driver? d = Ogr.GetDriverByName(want);
    Console.WriteLine($"  driver {want,-16} {(d is null ? "ABSENT" : "present")}");
}

string archive = args.Length > 0
    ? args[0]
    : @"/vsizip/C:/Temp2/gdb/PointofInvestigation.gdb.zip";

using DataSource? source = Ogr.Open(archive, 0);

if (source is null)
{
    Console.WriteLine($"could not open {archive}");
    return 1;
}

Console.WriteLine($"opened with {source.GetDriver().GetName()}, {source.GetLayerCount()} layers");

for (int i = 0; i < source.GetLayerCount(); i++)
{
    using Layer layer = source.GetLayerByIndex(i);
    using FeatureDefn definition = layer.GetLayerDefn();

    string? code = null;

    using (SpatialReference? reference = layer.GetSpatialRef())
    {
        if (reference is not null)
        {
            reference.AutoIdentifyEPSG();
            code = reference.GetAuthorityCode(null);
        }
    }

    Console.WriteLine(
        $"  {layer.GetName(),-46} {(wkbGeometryType)definition.GetGeomType(),-24} "
        + $"{layer.GetFeatureCount(1),6} features  {definition.GetFieldCount(),2} fields  "
        + $"EPSG:{code ?? "-"}");
}

// ------------------------------------------------------------------ and the conversion, timed
//
// The comparison that matters is against the Python worker's 0.21s read and 0.08s write on the same
// layer, because if .NET is not slower then the second runtime buys nothing at all.
string big = @"/vsizip/C:/Temp2/gdb/Environmental.gdb.zip";
string wanted = "OHN_Watercourse";
string output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gdal-dotnet-probe.parquet");

if (System.IO.File.Exists(output))
{
    System.IO.File.Delete(output);
}

using (DataSource? from = Ogr.Open(big, 0))
{
    if (from is null)
    {
        Console.WriteLine("could not open the second archive");
        return 1;
    }

    System.Diagnostics.Stopwatch clock;
    clock = System.Diagnostics.Stopwatch.StartNew();

    // `VectorTranslate` is `ogr2ogr` as a library call — the same code path, without a process.
    using (Dataset input = OSGeo.GDAL.Gdal.OpenEx(big, 0, null, null, null))
    using (Dataset written = OSGeo.GDAL.Gdal.wrapper_GDALVectorTranslateDestName(
        output,
        input,
        new GDALVectorTranslateOptions(["-f", "Parquet", "-lco", "GEOMETRY_ENCODING=WKB", wanted]),
        null,
        null))
    {
        clock.Stop();

        if (written is null)
        {
            Console.WriteLine("  translate returned nothing");
            return 1;
        }
    }

    // <b>Measured after disposal, because the first version measured 0 KB.</b> The dataset holds its
    // buffers until it is closed, so a size taken inside the `using` block is a size taken before the
    // file exists — a measurement that reported a working conversion as an empty one.
    Console.WriteLine(
        $"  converted {wanted} in {clock.Elapsed.TotalSeconds:F2}s, "
        + $"{new System.IO.FileInfo(output).Length / 1024} KB");
}

return 0;
