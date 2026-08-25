using Graticula.Api.Wms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>CRS:84</c> is longitude first, and resolving it to 4326 must not lose that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by the first CI run this repository ever completed, 2026-08-25.</b> A
/// <c>GetMap</c> over a layer's own advertised extent in <c>CRS:84</c> came back
/// <b>entirely transparent</b> — 589,824 pixels of nothing. The same request in
/// <c>EPSG:4326</c> with the ordinates swapped drew it, and the ArcGIS
/// <c>MapServer/export</c> face drew it from the same data and the same style, which is
/// what ruled out the data, the renderer and the symbology in one step.
/// </para>
/// <para>
/// <b>The cause is a resolution that is correct and lossy.</b> <c>TrySrid</c> maps
/// <c>CRS:84</c> to 4326 — the same reference, so the mapping is right — and the
/// transposition decision then read the number alone. 4326 under 1.3.0 <em>is</em>
/// latitude first, so the box was swapped, and a request for Ankara asked for a place
/// off the coast of Somalia. Every layer came back empty, which looks exactly like a
/// data problem and is diagnosed as one; `WmsRequest` says so in its own comment two
/// lines above the bug.
/// </para>
/// <para>
/// <b>What makes it worth a test of its own rather than a line in a conformance run:</b>
/// <c>CapabilitiesDocument</c> advertises <c>CRS:84</c> **precisely** so that clients who
/// would rather not think about axis order need not, and <c>TrySrid</c>'s own comment
/// says answering it *is answering the thing they are trying to avoid asking*. It was the
/// one code where getting this wrong was certain to be wrong.
/// </para>
/// </remarks>
public sealed class Crs84IsNotTransposedTests
{
    /// <summary>The name a client writes to mean longitude first.</summary>
    [Theory]
    [InlineData("CRS:84")]
    [InlineData("crs:84")]
    [InlineData(" CRS:84 ")]
    public void Crs84_is_longitude_first_however_it_is_spelled(string crs)
    {
        Assert.False(WmsNames.IsLatitudeFirst(WmsVersion.V130, crs, 4326));
    }

    /// <summary>
    /// EPSG:4326 under 1.3.0 is latitude first, which is the rule CRS:84 exists to escape.
    /// </summary>
    /// <remarks>
    /// <b>The control, and it is the half that keeps the fix honest.</b> Making CRS:84
    /// longitude-first by making everything longitude-first would pass the test above and
    /// break every conforming 1.3.0 client — the failure this file's subject is the mirror
    /// of, and the more common one.
    /// </remarks>
    [Fact]
    public void Epsg4326_is_still_latitude_first_under_1_3_0()
    {
        Assert.True(WmsNames.IsLatitudeFirst(WmsVersion.V130, "EPSG:4326", 4326));
    }

    /// <summary>1.1.1 never transposes, whatever the reference is.</summary>
    [Theory]
    [InlineData("EPSG:4326")]
    [InlineData("CRS:84")]
    public void Version_1_1_1_is_longitude_first_for_everything(string crs)
    {
        Assert.False(WmsNames.IsLatitudeFirst(WmsVersion.V111, crs, 4326));
    }

    /// <summary>A projected reference is easting first in both versions.</summary>
    [Fact]
    public void A_projected_reference_is_easting_first()
    {
        Assert.False(WmsNames.IsLatitudeFirst(WmsVersion.V130, "EPSG:3857", 3857));
    }

    /// <summary>
    /// A null or empty name falls back to the reference, rather than to longitude first.
    /// </summary>
    /// <remarks>
    /// <b>Because the cheapest wrong fix is <c>crs != "CRS:84"</c> read the other way.</b>
    /// A caller that has a srid and no name — the capabilities writer is one — must still
    /// get 1.3.0's rule, or the document advertises boxes in an order it does not serve.
    /// </remarks>
    [Fact]
    public void A_missing_name_still_follows_the_reference()
    {
        Assert.True(WmsNames.IsLatitudeFirst(WmsVersion.V130, null, 4326));
        Assert.True(WmsNames.IsLatitudeFirst(WmsVersion.V130, string.Empty, 4326));
    }
}
