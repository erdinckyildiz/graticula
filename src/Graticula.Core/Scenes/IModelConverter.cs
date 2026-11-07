using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Scenes;

/// <summary>What converting a 3D model gave: a glb, or why not.</summary>
/// <param name="Glb">The model as glTF binary, or null.</param>
/// <param name="Refusal">Why it could not be converted, in a sentence for the person who uploaded it.</param>
public sealed record ModelConversion(byte[]? Glb, string? Refusal);

/// <summary>
/// The port a 3D model converter stands behind (ADR-282 §5.1): a file in one of the formats a 3D object layer's editor
/// uploads, as ArcGIS names them (<c>3D_obj</c>, <c>3D_fbx</c>, <c>3D_dae</c> …), turned into glb. Nothing of the library
/// that converts appears here (build-vs-adopt Tier 2).
/// </summary>
public interface IModelConverter
{
    /// <summary>The asset types this converter takes, empty when none is installed.</summary>
    IReadOnlyCollection<string> Formats { get; }

    /// <summary>Converts a model, or says why not.</summary>
    /// <param name="content">The file, or a zip of it with its materials and textures.</param>
    /// <param name="format">Its asset type, one of <see cref="Formats"/>.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The glb, or the refusal.</returns>
    Task<ModelConversion> ConvertAsync(byte[] content, string format, CancellationToken cancellation);
}
