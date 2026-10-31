using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;

namespace Graticula.Host;

/// <summary>
/// Builds an image's overviews in the import reader's process rather than this one — D-295.
/// </summary>
/// <remarks>
/// <para>
/// <b>The build decodes every block of the file</b>, and for a registered image that file is one a stranger chose:
/// ADR-009 condition 1's door decodes a block at each end and the builder decodes all the rest. A defect in a middle
/// block used to be met here, in the process serving every other layer; it is now met in a child the host kills on its
/// deadline or its memory ceiling, and the image is served without overviews as it is when a folder cannot be written.
/// </para>
/// <para>
/// <b>The same builder, run there</b> — <c>TiffPyramidBuilder</c> through the reader's <c>pyramid</c> operation — so the
/// overviews are the ones this server always wrote. Whatever stops the child arrives as an <see cref="IOException"/>,
/// the exception every caller of <see cref="ICoveragePyramidBuilder"/> already catches and logs.
/// </para>
/// </remarks>
internal sealed class ReaderPyramidBuilder : ICoveragePyramidBuilder
{
    /// <summary>How long one image's build may run: the largest registered file, decoded once, with room.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromHours(2);

    private readonly GeodatabaseReader _reader;

    /// <summary>Builds through the reader.</summary>
    public ReaderPyramidBuilder(GeodatabaseReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc/>
    public async Task<int> BuildAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonDocument answer;

        try
        {
            answer = await _reader.AskAsync(new { op = "pyramid", path }, Deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException stopped)
        {
            // Not installed, killed on its deadline or its memory, or gone without an answer.
            throw new IOException($"The import reader did not build the overviews: {stopped.Message}", stopped);
        }

        using (answer)
        {
            JsonElement root = answer.RootElement;

            if (root.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean())
            {
                return root.GetProperty("levels").GetInt32();
            }

            string why = root.TryGetProperty("error", out JsonElement error) ? error.GetString() ?? string.Empty : string.Empty;

            throw new IOException(why.Length == 0 ? "The import reader could not build the overviews." : why);
        }
    }
}
