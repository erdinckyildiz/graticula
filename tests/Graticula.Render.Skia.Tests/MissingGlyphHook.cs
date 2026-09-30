using Xunit;

namespace Graticula.Render.Skia.Tests;

/// <summary>
/// The test classes that set <c>SkiaMapCanvas.Missing</c>, which is one static hook for the whole process.
/// </summary>
/// <remarks>
/// <b>One collection, so they do not run beside each other.</b> xUnit runs test classes in parallel, and each of these
/// sets the hook to its own list and clears it in <c>finally</c>. From ADR-100 on there were two such classes, and one
/// clearing or replacing the hook in the middle of the other's draw sent the report to the wrong list:
/// <c>A_glyph_no_face_has_is_reported_rather_than_drawn_as_a_box</c> saw nothing reported, once in CI (v1.0.238,
/// 2026-09-30), and passed on every rerun. The hook stays static because the host sets it once at start-up for its log.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class MissingGlyphHook
{
    /// <summary>The collection's name.</summary>
    public const string Name = "SkiaMapCanvas.Missing";
}
