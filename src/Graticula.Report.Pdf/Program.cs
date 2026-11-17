using System;
using System.IO;
using System.Text.Json;
using OfficeIMO.Word;
using OfficeIMO.Word.Pdf;

namespace Graticula.Report.Pdf;

/// <summary>
/// Lays out one filled report as a PDF and exits — ADR-302.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire.</b> One JSON line on standard input: <c>{"input": path, "output": path}</c>, the input a <c>.docx</c> the
/// server filled (ADR-301), the output a file that must not exist. One JSON line on standard output says how it went —
/// <c>{"ok": true, "bytes": n}</c> or <c>{"ok": false, "error": "…"}</c>. A stack trace goes to standard error, which the
/// host leaves attached to its own, as it does for the other siblings.
/// </para>
/// <para>
/// <b>Fonts are the machine's.</b> OfficeIMO embeds the installed family a document names and an installed substitute for
/// a missing Office one. The server image installs the metric-compatible families — Liberation for Arial, Times New Roman
/// and Courier New, Carlito for Calibri, Caladea for Cambria — and DejaVu for the symbols a template's tick boxes are
/// (☒, ☐), so a line breaks where Word breaks it. Without them the engine falls back to the PDF standard fonts, which
/// cannot write ☒, and the report is refused rather than written wrong (ADR-302 §5.3).
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

            string input = root.GetProperty("input").GetString()
                ?? throw new InvalidDataException("The request names no input.");
            string output = root.GetProperty("output").GetString()
                ?? throw new InvalidDataException("The request names no output.");

            byte[] pdf;

            using (WordDocument document = WordDocument.Load(input))
            {
                pdf = document.ToPdfBytes(new WordToPdfOptions());
            }

            using (FileStream file = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(pdf);
            }

            Answer(new { ok = true, bytes = pdf.Length });
            return 0;
        }
        catch (Exception failed)
        {
            Console.Error.WriteLine(failed);
            Answer(new { ok = false, error = failed.Message });
            return 1;
        }
    }

    private static void Answer(object answer)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(answer));
        Console.Out.Flush();
    }
}
