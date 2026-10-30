using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// A GeoTIFF somebody else chose is opened first in a process of its own — ADR-009 condition 1, the isolation half.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before this server opens it, at each of the four doors a file comes in by</b>: registration in place, an upload
/// published as an image service, an image added to one later, and a raster a user's tool wrote (D-324). The import reader opens the file with the same
/// parser this server reads it with, applies the bound on its dimensions (<see cref="CoverageBounds"/>), and decodes a
/// block at each end; only a file that survives all of that is opened here. A parser defect a file triggers is then a
/// child process dying, refused <c>400</c>, rather than a fault in the process serving every other layer.
/// </para>
/// <para>
/// <b>What it does not do, said here so nobody reads more into it:</b> the serving reads stay in this process. A file
/// that passes was parsed whole at the header and decoded at two blocks, and a defect elsewhere in its body would still
/// meet the parser here, at a tile request. ImageServer is outside v1 (v1-scope §3b); what v1 exposes is the
/// registration route, and that is what this closes.
/// </para>
/// <para>
/// <b>Not installed is refused, not waved through.</b> The isolation is the condition; a deployment without the reader
/// would otherwise parse strangers' files in the serving process with nothing to say so. The official image ships it.
/// </para>
/// </remarks>
internal static partial class CoverageAdminEndpoints
{
    /// <summary>How long the reader has to open a file and decode two blocks of it.</summary>
    /// <remarks>
    /// <b>A minute, and generous on purpose.</b> Two blocks of any image this server accepts decode in milliseconds;
    /// what takes longer is a file built to, and a slow disk under a large registered-in-place file is the case the
    /// margin is for.
    /// </remarks>
    private static readonly TimeSpan IsolatedOpenDeadline = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Opens <paramref name="path"/> in the import reader's process first, and says why it is refused, or null.
    /// </summary>
    /// <param name="services">The server's services, for the reader and the settings — a request's, or a tool run's.</param>
    /// <param name="path">The file, where this server keeps it.</param>
    /// <param name="named">The file as its sender named it — never the path, which is not theirs to read.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>
    /// The refusal, or null when the file may be opened here. <b>Null also when the parser refused the file</b>: the
    /// open that follows refuses it the same way, deterministically, and each door already words that refusal for
    /// its own sender.
    /// </returns>
    private static async Task<string?> OpenedApartAsync(
        IServiceProvider services, string path, string named, CancellationToken cancellation)
    {
        GeodatabaseReader reader = services.GetRequiredService<GeodatabaseReader>();
        HostSettings settings = services.GetRequiredService<HostSettings>();

        if (!reader.Available)
        {
            return $"{named} is opened first by this server's import reader, in a process of its own, so that a file "
                + "which breaks the parser breaks that process rather than this one — and the reader is not installed "
                + "beside the server. Install the server with its import reader.";
        }

        JsonDocument answer;

        try
        {
            answer = await reader.AskAsync(
                new { op = "tiff", path, maximumSide = settings.RasterMaximumSide, maximumPixels = settings.RasterMaximumPixels },
                IsolatedOpenDeadline, cancellation).ConfigureAwait(false);
        }
        catch (InvalidOperationException failed)
        {
            return $"{named} was opened first in a process of its own, and that process did not come back with it, so "
                + $"this server did not open it: {failed.Message.Replace(path, named, StringComparison.Ordinal)}";
        }

        using (answer)
        {
            JsonElement root = answer.RootElement;

            if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True)
            {
                return CoverageBounds.Refusal(named, root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(),
                    settings.RasterMaximumSide, settings.RasterMaximumPixels);
            }

            if (root.TryGetProperty("refused", out JsonElement refused) && refused.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            string why = root.TryGetProperty("error", out JsonElement error) ? error.GetString() ?? "it failed" : "it failed";

            return $"{named} could not be read safely: opened in a process of its own, reading it failed there — "
                + $"{why.Replace(path, named, StringComparison.Ordinal).TrimEnd('.')}. A file whose blocks ask for more "
                + "memory than an image may take for one read is refused this way; so is one the parser cannot get through.";
        }
    }
}
