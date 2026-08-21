using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;

namespace Graticula.Raster.Tiff;

/// <summary>
/// Opens GeoTIFF coverages. The only type the host binds to the port.
/// </summary>
/// <remarks>
/// <b>A factory rather than a constructor, for the reason
/// [ADR-041](../../../docs/adr/ADR-041-the-map-renderer.md) §5.1 gives for the
/// canvas.</b> Registration is what lets the host name one implementation in one
/// place; without it every caller would name the format, and the confinement the
/// project boundary buys would be a boundary around nothing.
/// </remarks>
public sealed class TiffCoverageReaderFactory : ICoverageReaderFactory
{
    /// <inheritdoc/>
    public Task<ICoverageReader> OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<ICoverageReader>(TiffCoverageReader.Open(path));
    }
}
