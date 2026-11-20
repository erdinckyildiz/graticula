using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Scenes;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-282 §5.5, by owner decision 2026-11-20: an FBX is converted from its own units to metres by its
/// <c>UnitScaleFactor</c> (centimetres per unit), so a model drawn in centimetres is not placed a hundred times too large.
/// </summary>
/// <remarks>
/// It runs the server's own converter, <see cref="ModelConverters"/>, on Assimp's command-line tool, so it needs
/// <c>assimp</c> and <c>sh</c> on the path: CI's <c>conformance</c> job installs Ubuntu's <c>assimp-utils</c> and runs it
/// there (<c>Needs=ModelConverter</c>). Without the tool it fails and says so rather than passing on nothing.
/// The two files are the same 10 × 20 × 4 box, as ASCII FBX: <c>box-ascii.fbx</c> as Assimp wrote it, which declares
/// <c>UnitScaleFactor</c> 1 (centimetres), and <c>box-ascii-metres.fbx</c>, the same file declaring 100 (metres).
/// </remarks>
[Trait("Needs", "ModelConverter")]
public sealed class AnFbxIsConvertedToMetresTests
{
    private static byte[] Corpus(string file)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && repository.GetFiles("*.sln").Length == 0)
        {
            repository = repository.Parent;
        }

        return File.ReadAllBytes(Path.Combine(repository!.FullName, "tests", "Graticula.Console.Tests", "corpus", file));
    }

    private static async Task<double[]> ConvertedSizeAsync(string file)
    {
        ModelConverters converters = new();
        Assert.True(converters.Formats.Contains("3D_fbx"), "Assimp's command-line tool `assimp` is not on the path, so FBX is not converted here.");
        ModelConversion converted = await converters.ConvertAsync(Corpus(file), "3D_fbx", CancellationToken.None);
        Assert.True(converted.Glb is not null, converted.Refusal);
        var points = GltfTriangles.Read(converted.Glb!).Points;
        return
        [
            points.Max(p => p.X) - points.Min(p => p.X),
            points.Max(p => p.Y) - points.Min(p => p.Y),
            points.Max(p => p.Z) - points.Min(p => p.Z),
        ];
    }

    [Fact]
    public async Task A_centimetre_fbx_is_a_hundredth_of_its_numbers_in_metres()
    {
        double[] size = await ConvertedSizeAsync("box-ascii.fbx");
        Assert.Equal(0.1, size[0], 4);
        Assert.Equal(0.2, size[1], 4);
        Assert.Equal(0.04, size[2], 4);
    }

    [Fact]
    public async Task A_metre_fbx_keeps_its_numbers()
    {
        double[] size = await ConvertedSizeAsync("box-ascii-metres.fbx");
        Assert.Equal(10, size[0], 4);
        Assert.Equal(20, size[1], 4);
        Assert.Equal(4, size[2], 4);
    }
}
