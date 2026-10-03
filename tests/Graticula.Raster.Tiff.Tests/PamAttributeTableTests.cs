using System;
using System.IO;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Raster.Tiff;
using Xunit;

namespace Graticula.Raster.Tiff.Tests;

/// <summary>ADR-154: the raster attribute table GDAL and ArcGIS write beside an image, read by usage or by name.</summary>
public sealed class PamAttributeTableTests
{
    [Fact]
    public void Gdal_s_table_is_read_by_its_usage_codes_with_colours_as_fractions()
    {
        string image = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tif");
        File.WriteAllText(image + ".aux.xml", """
            <PAMDataset>
              <PAMRasterBand band="1">
                <GDALRasterAttributeTable tableType="thematic">
                  <FieldDefn index="0"><Name>V</Name><Type>0</Type><Usage>5</Usage></FieldDefn>
                  <FieldDefn index="1"><Name>N</Name><Type>2</Type><Usage>2</Usage></FieldDefn>
                  <FieldDefn index="2"><Name>R</Name><Type>1</Type><Usage>6</Usage></FieldDefn>
                  <FieldDefn index="3"><Name>G</Name><Type>1</Type><Usage>7</Usage></FieldDefn>
                  <FieldDefn index="4"><Name>B</Name><Type>1</Type><Usage>8</Usage></FieldDefn>
                  <FieldDefn index="5"><Name>Px</Name><Type>0</Type><Usage>1</Usage></FieldDefn>
                  <Row index="0"><F>11</F><F>Open water</F><F>0</F><F>0</F><F>1</F><F>40</F></Row>
                  <Row index="1"><F>41</F><F>Deciduous forest</F><F>0</F><F>0.5</F><F>0</F><F>7</F></Row>
                </GDALRasterAttributeTable>
              </PAMRasterBand>
            </PAMDataset>
            """);

        try
        {
            RasterAttributeTable table = PamAttributeTable.TryRead(image)!;
            Assert.Equal(2, table.Classes.Count);
            Assert.Equal("Open water", table.Find(11)!.Name);
            Assert.Equal(new Rgba(0, 0, 255, 255), table.Find(11)!.Colour);
            Assert.Equal(new Rgba(0, 128, 0, 255), table.Find(41)!.Colour);
            Assert.Equal(40, table.Find(11)!.Count);
        }
        finally
        {
            File.Delete(image + ".aux.xml");
        }
    }

    [Fact]
    public void ArcGIS_s_names_are_read_where_usage_says_only_generic_and_no_sidecar_is_no_table()
    {
        string image = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tif");
        Assert.Null(PamAttributeTable.TryRead(image));

        File.WriteAllText(image + ".aux.xml", """
            <PAMDataset><PAMRasterBand band="1"><GDALRasterAttributeTable>
              <FieldDefn index="0"><Name>Value</Name><Type>0</Type><Usage>0</Usage></FieldDefn>
              <FieldDefn index="1"><Name>Class_Name</Name><Type>2</Type><Usage>0</Usage></FieldDefn>
              <FieldDefn index="2"><Name>Red</Name><Type>0</Type><Usage>0</Usage></FieldDefn>
              <FieldDefn index="3"><Name>Green</Name><Type>0</Type><Usage>0</Usage></FieldDefn>
              <FieldDefn index="4"><Name>Blue</Name><Type>0</Type><Usage>0</Usage></FieldDefn>
              <Row index="0"><F>3</F><F>Urban</F><F>255</F><F>0</F><F>0</F></Row>
            </GDALRasterAttributeTable></PAMRasterBand></PAMDataset>
            """);

        try
        {
            AttributeClass urban = PamAttributeTable.TryRead(image)!.Find(3)!;
            Assert.Equal("Urban", urban.Name);
            Assert.Equal(new Rgba(255, 0, 0, 255), urban.Colour);
        }
        finally
        {
            File.Delete(image + ".aux.xml");
        }
    }
}
