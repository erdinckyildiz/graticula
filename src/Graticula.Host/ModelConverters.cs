using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Scenes;

namespace Graticula.Host;

/// <summary>
/// ADR-282 and ADR-283: 3D models converted to glb by a command-line tool in a process of its own per file — a parser
/// fault ends that process, not the server — under a time and a memory limit, in a scratch directory removed afterwards.
/// Its glb is read back by this server's own reader before it is accepted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three tools, each for the formats measured to convert with it</b>:
/// <list type="bullet">
/// <item>the Open Asset Import Library's <c>assimp</c> (BSD-3) for OBJ, alone or zipped with its materials, COLLADA and
/// FBX (ADR-282 §4);</item>
/// <item>IfcOpenShell's <c>IfcConvert</c> (LGPL-3.0), with its Open CASCADE kernel and without its CGAL ones, for IFC
/// (ADR-283 §5.2);</item>
/// <item>our own <c>usd2glb</c>, built against tinyusdz (Apache-2.0), for USD in its text, binary and zipped forms
/// (ADR-283 §5.2).</item>
/// </list>
/// </para>
/// <para>
/// A format is offered only when its tool is installed, and an upload in another format than glb says which are.
/// </para>
/// </remarks>
internal sealed class ModelConverters : IModelConverter
{
    /// <summary>A format's file extension, the tool that converts it, and how the tool is told input and output.</summary>
    private sealed record Route(string Extension, string Tool, string Arguments);

    private static readonly Dictionary<string, Route> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3D_obj"] = new(".obj", "assimp", "export \"$1\" \"$2\" -fglb2"),
        ["3D_dae"] = new(".dae", "assimp", "export \"$1\" \"$2\" -fglb2"),
        ["3D_fbx"] = new(".fbx", "assimp", "export \"$1\" \"$2\" -fglb2"),
        ["3D_ifc"] = new(".ifc", "IfcConvert", "-y -q \"$1\" \"$2\""),
        ["3D_usdz"] = new(".usdz", "usd2glb", "\"$1\" \"$2\""),
        ["3D_usdc"] = new(".usdc", "usd2glb", "\"$1\" \"$2\""),
        ["3D_usda"] = new(".usda", "usd2glb", "\"$1\" \"$2\""),
    };

    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private readonly Dictionary<string, string> tools;

    public ModelConverters() =>
        tools = Extensions.Values.Select(r => r.Tool).Distinct(StringComparer.Ordinal)
            .Select(t => (Name: t, Path: Find(t))).Where(t => t.Path is not null)
            .ToDictionary(t => t.Name, t => t.Path!, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Formats => [.. Extensions.Where(e => tools.ContainsKey(e.Value.Tool)).Select(e => e.Key)];

    /// <summary>A tool on the path, followed through a link to where it is, so that the libraries beside it are found.</summary>
    private static string? Find(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, name)).Where(File.Exists)
            .Select(p => new FileInfo(p).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? p).FirstOrDefault();

    public async Task<ModelConversion> ConvertAsync(byte[] content, string format, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Extensions.TryGetValue(format ?? string.Empty, out Route? route) || !tools.TryGetValue(route.Tool, out string? tool))
        {
            return new(null, Formats.Count == 0
                ? "This server has no 3D model converter installed (ADR-282); upload the model as glb."
                : $"{format} is not converted here; {string.Join(", ", Formats.Select(f => f[3..].ToUpperInvariant()))} and GLB are. Export the model as one of them.");
        }

        string extension = route.Extension;

        string scratch = Directory.CreateTempSubdirectory("graticula-model-").FullName;
        try
        {
            // A usdz is a zip by its own definition, read whole by its converter: it is not unpacked here.
            string? unpacked = extension == ".usdz" ? null : Unpacked(content, scratch, extension);
            if (unpacked is { Length: 0 })
            {
                return new(null, $"The zip holds no {extension} file.");
            }

            string input = unpacked ?? Write(content, scratch, extension);

            string output = Path.Combine(scratch, "model.glb");
            ProcessStartInfo start = new("sh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = scratch, UseShellExecute = false,
            };
            // A memory ceiling of 2 GB of address space for the converter alone, and the libraries beside it found first.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("ulimit -v 2097152; LD_LIBRARY_PATH=\"$(dirname \"$0\")${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\" exec \"$0\" " + route.Arguments);
            start.ArgumentList.Add(tool);
            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The converter did not start.");
            Task<string> said = process.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellation);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(Limit);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new(null, $"Converting the model took longer than {Limit.TotalSeconds:0} seconds and was stopped.");
            }

            string all = (await said.ConfigureAwait(false)) + "\n" + (await errors.ConfigureAwait(false));
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                string why = all.Split('\n').Select(l => l.Trim())
                    .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal) || l.StartsWith("[error]", StringComparison.Ordinal) || l.StartsWith("The ", StringComparison.Ordinal))
                    ?? "the converter could not read it";
                return new(null, $"The model could not be converted: {why}.");
            }

            byte[] glb = await File.ReadAllBytesAsync(output, cancellation).ConfigureAwait(false);
            try
            {
                if (GltfTriangles.Read(glb).Points.Count == 0)
                {
                    return new(null, "The converted model has no triangles this server reads.");
                }
            }
            catch (FormatException e)
            {
                return new(null, $"The converted model is not one this server reads: {e.Message}");
            }

            return new(glb, null);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory left behind is the temporary directory's to clear.
            }
        }
    }

    private static string Write(byte[] content, string scratch, string extension)
    {
        string path = Path.Combine(scratch, "model" + extension);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A zip's files written out, within a limit on what it expands to, and the model file among them; null when not a zip.</summary>
    private static string? Unpacked(byte[] content, string scratch, string extension)
    {
        if (content.Length < 4 || content[0] != 'P' || content[1] != 'K')
        {
            return null;
        }

        try
        {
            using ZipArchive zip = new(new MemoryStream(content), ZipArchiveMode.Read);
            long total = 0;
            string? model = null;
            foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.Length > 0))
            {
                total += entry.Length;
                string name = Path.GetFileName(entry.FullName);
                if (total > MaximumExpandedBytes || name.Length == 0 || name.StartsWith('.'))
                {
                    continue;
                }

                // Every file flat in the scratch directory, by its own name: no path from the zip is followed.
                string path = Path.Combine(scratch, name);
                entry.ExtractToFile(path, overwrite: true);
                if (model is null && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    model = path;
                }
            }

            return model ?? string.Empty;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
