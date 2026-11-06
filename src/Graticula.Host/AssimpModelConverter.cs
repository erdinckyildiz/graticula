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
/// ADR-282: 3D models converted to glb by the Open Asset Import Library's command-line tool, <c>assimp</c> (BSD-3), in
/// a process of its own per file — a parser fault ends that process, not the server — under a time and a memory limit,
/// in a scratch directory removed afterwards. Its glb is read back by this server's own reader before it is accepted.
/// </summary>
/// <remarks>
/// <b>The formats offered are the ones measured to convert</b> (ADR-282 §4): OBJ (alone, or zipped with its materials),
/// COLLADA and FBX. Assimp reads more; each is offered when it is measured. When the tool is not installed the converter
/// offers nothing, and an upload in another format than glb says so.
/// </remarks>
internal sealed class AssimpModelConverter : IModelConverter
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3D_obj"] = ".obj", ["3D_dae"] = ".dae", ["3D_fbx"] = ".fbx",
    };

    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private readonly string? tool;

    public AssimpModelConverter() => tool = Find();

    public IReadOnlyCollection<string> Formats => tool is null ? [] : Extensions.Keys;

    private static string? Find() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "assimp")).FirstOrDefault(File.Exists);

    public async Task<ModelConversion> ConvertAsync(byte[] content, string format, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (tool is null)
        {
            return new(null, "This server has no 3D model converter installed (ADR-282); upload the model as glb.");
        }

        if (!Extensions.TryGetValue(format ?? string.Empty, out string? extension))
        {
            return new(null, $"{format} is not converted here; OBJ, COLLADA, FBX and glb are. Export the model as one of them.");
        }

        string scratch = Directory.CreateTempSubdirectory("graticula-model-").FullName;
        try
        {
            string? unpacked = Unpacked(content, scratch, extension);
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
            // A memory ceiling of 2 GB of address space for the converter alone.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("ulimit -v 2097152; exec \"$0\" export \"$1\" \"$2\" -fglb2");
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
                string why = all.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? "the converter could not read it";
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
