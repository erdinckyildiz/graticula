using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Graticula.Api.ArcGis.Offline;
using Graticula.Offline.Sqlite;

namespace Graticula.Offline.Reader;

/// <summary>
/// Reads one uploaded delta and exits — ADR-288 condition 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire.</b> One JSON line on standard input:
/// <c>{"upload": path, "output": path, "layers": [...]}</c>, the layers in <see cref="OfflineDeltaWire.Shapes"/>'s form.
/// The changes go to <c>output</c>, which must not exist, in <see cref="OfflineDeltaWire"/>'s binary form; one JSON line
/// on standard output says how it went — <c>{"ok": true}</c> or <c>{"ok": false, "error": "…"}</c>. A stack trace goes
/// to standard error, which the host leaves attached to its own, as it does for the import reader.
/// </para>
/// <para>
/// <b>One question per process</b>, for <c>GeodatabaseReader</c>'s reason: nothing survives between two clients' files.
/// The bounds — a deadline, a heap ceiling, a working-set ceiling polled from outside, a lower priority — are the
/// host's, because this is the part that cannot be trusted to bound itself.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        string? line = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(line))
        {
            Answer(new { ok = false, error = "No request arrived on standard input." });
            return 2;
        }

        try
        {
            using JsonDocument request = JsonDocument.Parse(line);
            JsonElement root = request.RootElement;

            string upload = root.GetProperty("upload").GetString()
                ?? throw new InvalidDataException("The request names no upload.");
            string output = root.GetProperty("output").GetString()
                ?? throw new InvalidDataException("The request names no output.");
            IReadOnlyList<OfflineLayer> layers = OfflineDeltaWire.ReadShapes(root.GetProperty("layers"));

            OfflineDelta delta = new SqliteOfflineDeltaReader()
                .ReadDeltaAsync(upload, layers, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            using (FileStream file = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                OfflineDeltaWire.Write(delta, file);
            }

            Answer(new { ok = true });
            return 0;
        }
        catch (Exception failed)
        {
            Console.Error.WriteLine(failed);
            Answer(new { ok = false, error = $"{failed.GetType().Name}: {failed.Message}" });
            return 1;
        }
    }

    private static void Answer(object answer)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(answer));
        Console.Out.Flush();
    }
}
