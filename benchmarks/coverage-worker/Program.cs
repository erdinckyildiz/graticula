// D-295: the per-request cost of reading a GeoTIFF window in a child process rather than in the serving one.
//
//   dotnet run -c Release -- make <dir>                     writes float.tif (4096², 1 band, Real32) and rgb.tif (4096², 3 bands, byte)
//   dotnet run -c Release -- bench <tif> <requests> <callers> [window]
//
// Each request is what the ImageServer face does per tile, identify or export: open the file, read one window at
// full resolution, close it (ImageServerEndpoints opens a reader per request). Compared four ways:
//   in-process        TiffCoverageReader in this process, the server's shape today
//   worker, binary    the same calls in a child process; request a JSON line, answer a length-prefixed float64 frame
//   worker, json      the same, answered as one JSON line with the samples in base64 — the overlay and import pools' wire
//   pool              <callers> children, each caller holding one, against <callers> in-process threads
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Graticula.Coverages;
using Graticula.Raster.Tiff;

if (args.Length > 0 && args[0] == "worker")
{
    Worker(args.Length > 1 && args[1] == "json", args.Length > 1 && args[1] == "native");
    return 0;
}

if (args.Length >= 2 && args[0] == "make")
{
    Directory.CreateDirectory(args[1]);
    await Make(Path.Combine(args[1], "float.tif"), 1, SampleKind.Real32);
    await Make(Path.Combine(args[1], "rgb.tif"), 3, SampleKind.Unsigned8);
    return 0;
}

if (args.Length >= 4 && args[0] == "bench")
{
    string tif = args[1];
    int n = int.Parse(args[2], CultureInfo.InvariantCulture);
    int callers = int.Parse(args[3], CultureInfo.InvariantCulture);
    int window = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 256;
    await Bench(tif, n, callers, window);
    return 0;
}

Console.Error.WriteLine("usage: make <dir> | bench <tif> <requests> <callers> [window]");
return 2;

static async Task Make(string path, int bands, SampleKind kind)
{
    var random = new Random(42);
    await TiffGridWriter.WriteAsync(path, 4096, 4096, bands, kind, 500000, 4500000, 1, 1, 32635, false, null,
        (x, y, w, h, _) =>
        {
            double[] block = new double[w * h * bands];
            for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            for (int b = 0; b < bands; b++)
            {
                double v = 100 + 80 * Math.Sin((x + c) / 97.0 + b) * Math.Cos((y + r) / 131.0) + random.NextDouble() * 12;
                block[((r * w + c) * bands) + b] = kind == SampleKind.Unsigned8 ? Math.Clamp(Math.Round(v), 0, 255) : v;
            }
            return Task.FromResult(block);
        }, CancellationToken.None);
    Console.WriteLine($"{path}: {new FileInfo(path).Length / 1e6:F1} MB");
}

static CoverageWindow ReadOnce(string path, int x, int y, int w, int h) => ReadKind(path, x, y, w, h).Window;

static (CoverageWindow Window, SampleKind Kind) ReadKind(string path, int x, int y, int w, int h)
{
    using TiffCoverageReader reader = TiffCoverageReader.Open(path);
    return (reader.ReadAsync(0, x, y, w, h, CancellationToken.None).GetAwaiter().GetResult(), reader.Info.Bands[0].Kind);
}

static void Worker(bool json, bool native)
{
    using var input = new StreamReader(Console.OpenStandardInput());
    using Stream output = Console.OpenStandardOutput();
    string? line;
    while ((line = input.ReadLine()) is not null)
    {
        using JsonDocument request = JsonDocument.Parse(line);
        JsonElement r = request.RootElement;
        (CoverageWindow read, SampleKind kind) = ReadKind(r.GetProperty("p").GetString()!, r.GetProperty("x").GetInt32(),
            r.GetProperty("y").GetInt32(), r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32());
        byte[] bytes;
        if (native)
        {
            // The samples at their own width: a byte for 8-bit imagery, four for Real32 — what a production wire would send.
            int size = kind == SampleKind.Unsigned8 ? 1 : 4;
            bytes = new byte[read.Samples.Length * size + 1];
            bytes[0] = (byte)size;
            if (size == 1)
                for (int i = 0; i < read.Samples.Length; i++) bytes[i + 1] = (byte)read.Samples[i];
            else
                for (int i = 0; i < read.Samples.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(1 + i * 4), (float)read.Samples[i]);
        }
        else
        {
            bytes = new byte[read.Samples.Length * 8];
            Buffer.BlockCopy(read.Samples, 0, bytes, 0, bytes.Length);
        }
        if (json)
        {
            byte[] answer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                ok = true, w = read.Width, h = read.Height, bands = read.Bands, samples = Convert.ToBase64String(bytes),
            }) + "\n");
            output.Write(answer);
        }
        else
        {
            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(header, read.Width);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], read.Height);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], read.Bands);
            output.Write(header);
            output.Write(bytes);
        }
        output.Flush();
    }
}

static async Task Bench(string tif, int n, int callers, int window)
{
    var random = new Random(7);
    (int X, int Y)[] windows = [.. Enumerable.Range(0, n + 20).Select(_ => (random.Next(0, 4096 - window), random.Next(0, 4096 - window)))];
    string path = Path.GetFullPath(tif);
    Console.WriteLine($"{Path.GetFileName(path)}, {window}×{window} windows, {n} requests after 20 discarded, {callers} callers for the pool rows");

    // In process, one at a time.
    Report("in-process", Sequential(windows, w => ReadOnce(path, w.X, w.Y, window, window).Samples.Length));

    // A child, one at a time, both wires — and what starting one costs.
    foreach (string wire in new[] { "binary", "native", "json" })
    {
        var started = Stopwatch.StartNew();
        using Child child = new(wire);
        child.Read(path, windows[0].X, windows[0].Y, window, window);
        double spawn = started.Elapsed.TotalMilliseconds;
        Report("worker, " + wire, Sequential(windows, w => child.Read(path, w.X, w.Y, window, window)));
        Console.WriteLine($"    start to first answer: {spawn:F0} ms");
    }

    // Throughput: callers at once, in process against a child each.
    Report($"in-process ×{callers}", await Concurrent(windows, callers, _ => w => ReadOnce(path, w.X, w.Y, window, window).Samples.Length));
    foreach (string wire in new[] { "binary", "native" })
    {
        Child[] pool = [.. Enumerable.Range(0, callers).Select(_ => new Child(wire))];
        try
        {
            foreach (Child c in pool) c.Read(path, 0, 0, window, window);
            Report($"pool ×{callers}, {wire}", await Concurrent(windows, callers, i => w => pool[i].Read(path, w.X, w.Y, window, window)));
        }
        finally
        {
            foreach (Child c in pool) c.Dispose();
        }
    }
}

static (double[] Ms, double Seconds) Sequential((int X, int Y)[] windows, Func<(int X, int Y), int> read)
{
    for (int i = 0; i < 20; i++) read(windows[i]);
    double[] ms = new double[windows.Length - 20];
    var total = Stopwatch.StartNew();
    for (int i = 20; i < windows.Length; i++)
    {
        long t = Stopwatch.GetTimestamp();
        read(windows[i]);
        ms[i - 20] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }
    return (ms, total.Elapsed.TotalSeconds);
}

static async Task<(double[] Ms, double Seconds)> Concurrent((int X, int Y)[] windows, int callers, Func<int, Func<(int X, int Y), int>> reader)
{
    for (int i = 0; i < callers; i++) reader(i)(windows[i]);
    double[] ms = new double[windows.Length - 20];
    int next = 19;
    var total = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, callers).Select(c => Task.Run(() =>
    {
        Func<(int X, int Y), int> read = reader(c);
        int i;
        while ((i = Interlocked.Increment(ref next)) < windows.Length)
        {
            long t = Stopwatch.GetTimestamp();
            read(windows[i]);
            ms[i - 20] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
    })));
    return (ms, total.Elapsed.TotalSeconds);
}

static void Report(string label, (double[] Ms, double Seconds) run)
{
    double[] s = [.. run.Ms.OrderBy(v => v)];
    double P(double q) => s[Math.Min(s.Length - 1, (int)(q * s.Length))];
    Console.WriteLine($"  {label,-22} p50 {P(0.5),7:F2} ms   p95 {P(0.95),7:F2} ms   mean {s.Average(),7:F2} ms   {s.Length / run.Seconds,7:F0} req/s");
}

sealed class Child : IDisposable
{
    private readonly Process _process;
    private readonly bool _json;
    private readonly bool _native;
    private readonly StreamWriter _in;
    private readonly Stream _out;
    private readonly StreamReader? _lines;

    public Child(string wire)
    {
        _json = wire == "json";
        _native = wire == "native";
        bool json = _json;
        var start = new ProcessStartInfo(Environment.ProcessPath!, "worker " + wire)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
        };

        // A worker is one request at a time: workstation GC, as the import reader and overlay worker run — server GC
        // in eight children is eight sets of sixteen heaps on one machine.
        start.Environment["DOTNET_gcServer"] = "0";
        _process = Process.Start(start)!;
        _in = _process.StandardInput;
        _in.AutoFlush = true;
        _out = _process.StandardOutput.BaseStream;
        _lines = json ? _process.StandardOutput : null;
    }

    public int Read(string path, int x, int y, int w, int h)
    {
        _in.WriteLine(JsonSerializer.Serialize(new { p = path, x, y, w, h }));
        if (_json)
        {
            using JsonDocument answer = JsonDocument.Parse(_lines!.ReadLine()!);
            byte[] bytes = Convert.FromBase64String(answer.RootElement.GetProperty("samples").GetString()!);
            double[] samples = new double[bytes.Length / 8];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples.Length;
        }
        Span<byte> header = stackalloc byte[12];
        _out.ReadExactly(header);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header) * BinaryPrimitives.ReadInt32LittleEndian(header[4..])
            * BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        double[] result = new double[count];
        if (_native)
        {
            int size = _out.ReadByte();
            byte[] packed = new byte[count * size];
            _out.ReadExactly(packed);
            if (size == 1)
                for (int i = 0; i < count; i++) result[i] = packed[i];
            else
                for (int i = 0; i < count; i++) result[i] = BinaryPrimitives.ReadSingleLittleEndian(packed.AsSpan(i * 4));
            return result.Length;
        }
        byte[] frame = new byte[count * 8];
        _out.ReadExactly(frame);
        Buffer.BlockCopy(frame, 0, result, 0, frame.Length);
        return result.Length;
    }

    public void Dispose()
    {
        try { _in.Close(); _process.WaitForExit(2000); } catch { }
        if (!_process.HasExited) _process.Kill();
        _process.Dispose();
    }
}
