using System;
using System.IO;
using System.Linq;
using System.Text;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>ADR-106 §5.6 and condition 4: a Shapefile's ten-byte field names, and the list of what each was.</summary>
public sealed class ShapefileFieldNamesTests
{
    [Fact]
    public void A_name_that_fits_is_kept_a_longer_one_is_cut_and_a_repeat_is_numbered_in_field_order()
    {
        Assert.Equal(
            ["kimlik", "şehir", "ilçe_adı", "mahalle_ad", "mahalle__1", "mahalle__2", "açççç"],
            ShapefileFieldNames.Of(["kimlik", "şehir", "ilçe_adı", "mahalle_adi_1", "mahalle_adi_2", "mahalle_adı_3", "aççççç"]));
    }

    [Fact]
    public void A_cut_never_splits_a_character()
    {
        // 'a' and five 'ç' are eleven bytes; ten would end inside the fifth 'ç', so the cut keeps four.
        Assert.Equal("açççç", ShapefileFieldNames.Cut("aççççç", 10));
        Assert.Equal(9, Encoding.UTF8.GetByteCount(ShapefileFieldNames.Cut("aççççç", 10)));
        Assert.Equal("ilçe_adı", ShapefileFieldNames.Cut("ilçe_adı", 10));
    }

    [Fact]
    public void Repeats_are_found_without_case_as_dbase_finds_them_and_the_tenth_takes_seven_bytes()
    {
        Assert.Equal(["Name", "name_1"], ShapefileFieldNames.Of(["Name", "name"]));

        string[] many = [.. Enumerable.Range(0, 11).Select(i => "column_name_" + i)];
        var names = ShapefileFieldNames.Of(many);
        Assert.Equal("column_nam", names[0]);
        Assert.Equal("column_n_9", names[9]);
        Assert.Equal("column__10", names[10]);
    }

    [Fact]
    public void A_written_dbf_is_renamed_and_listed_and_one_that_does_not_match_is_left_alone()
    {
        string folder = Path.Combine(Path.GetTempPath(), "shpnames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            // GDAL's names: the third split 'ı' in half.
            File.WriteAllBytes(Path.Combine(folder, "l.dbf"), Dbf(["mahalle_ad", "mahalle__1", "mahalle_a\xC4"]));

            Assert.True(ShapefileFieldNames.Apply(folder, "l", ["mahalle_adi_1", "mahalle_adi_2", "mahalle_adı_3"]));
            Assert.Equal(["mahalle_ad", "mahalle__1", "mahalle__2"], ShapefileFieldNames.Read(File.ReadAllBytes(Path.Combine(folder, "l.dbf"))));
            Assert.Equal(
                "served,shapefile\r\nmahalle_adi_1,mahalle_ad\r\nmahalle_adi_2,mahalle__1\r\nmahalle_adı_3,mahalle__2\r\n",
                File.ReadAllText(Path.Combine(folder, "l.fieldnames.csv"), Encoding.UTF8));

            // Another field count: nothing is rewritten.
            File.WriteAllBytes(Path.Combine(folder, "m.dbf"), Dbf(["a", "b"]));
            Assert.False(ShapefileFieldNames.Apply(folder, "m", ["a", "b", "c"]));
            Assert.False(File.Exists(Path.Combine(folder, "m.fieldnames.csv")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A header and descriptors, which is all the renaming reads; names given as Latin-1 to place raw bytes.</summary>
    private static byte[] Dbf(string[] names)
    {
        byte[] dbf = new byte[32 + (names.Length * 32) + 1];

        for (int i = 0; i < names.Length; i++)
        {
            byte[] name = names[i].Contains('\xC4', StringComparison.Ordinal)
                ? Encoding.Latin1.GetBytes(names[i])
                : Encoding.UTF8.GetBytes(names[i]);
            name.CopyTo(dbf, 32 + (i * 32));
        }

        dbf[^1] = 0x0D;
        return dbf;
    }
}
