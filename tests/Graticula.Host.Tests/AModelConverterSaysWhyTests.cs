using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-282 condition 2: a real model a converter refuses is refused with a reason a person can use — the converter's own
/// sentence, not the source file of the library inside it.
/// </summary>
public sealed class AModelConverterSaysWhyTests
{
    /// <summary>
    /// usd2glb's output for a real USDZ it could not read (McUsd.usdz from the USD Working Group's assets, a Mineways
    /// export, 2026-11-20), shortened: its own sentence, then tinyusdz's error stack naming its source files.
    /// </summary>
    private const string Usd2GlbRefused =
        "The file is not a USD stage this converter reads: Failed to parse USDA\n"
        + "[error]/tinyusdz/src/usda-reader.cc:ReconstructPrim():1623 Failed to reconstruct Mesh Prim: Attribute `doubleSided` must be `uniform` variability.\n"
        + "\n"
        + "[error]/tinyusdz/src/usda-reader.cc:Read():1700 Parse failed:\n"
        + "err_stack[0] USDA source near line 288, col 9: [error]/tinyusdz/src/ascii-parser.cc:ParseBlock():5105 Constructing Prim type `Mesh` failed\n";

    [Fact]
    public void Usd2glbs_own_sentence_is_the_reason_not_tinyusdzs_source_file() =>
        Assert.Equal("The file is not a USD stage this converter reads: Failed to parse USDA", ModelConverters.Reason(Usd2GlbRefused));

    [Fact]
    public void Assimps_last_error_is_the_reason() =>
        Assert.Equal("ERROR: block offset is out of range",
            ModelConverters.Reason("Launching asset import ...\nInfo,  T0: Load model.fbx\nERROR: FBX: something\nERROR: block offset is out of range\n"));

    [Fact]
    public void Silence_is_said_as_such() =>
        Assert.Equal("the converter could not read it", ModelConverters.Reason("\n\n"));
}
