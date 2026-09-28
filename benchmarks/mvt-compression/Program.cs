// ADR-068 §9: brotli and gzip at CompressionLevel.Fastest — the level the server uses — against
// real MVT tiles captured from the showcase. Reports per-bucket size ratios and per-tile CPU time.
using System.Diagnostics;
using System.IO.Compression;

string dir = args.Length > 0 ? args[0] : "corpus";
var tiles = Directory.GetFiles(dir, "*.pbf").Select(p => (Name: Path.GetFileName(p), Bytes: File.ReadAllBytes(p))).ToList();

static byte[] Br(byte[] b, CompressionLevel l) { using var ms = new MemoryStream(); using (var s = new BrotliStream(ms, l)) s.Write(b); return ms.ToArray(); }
static byte[] Gz(byte[] b, CompressionLevel l) { using var ms = new MemoryStream(); using (var s = new GZipStream(ms, l)) s.Write(b); return ms.ToArray(); }

// warm up
foreach (var t in tiles.Take(20)) { Br(t.Bytes, CompressionLevel.Fastest); Gz(t.Bytes, CompressionLevel.Fastest); }

var rows = new List<(string Name, int Raw, int Br, int Gz, double BrUs, double GzUs)>();
foreach (var t in tiles)
{
    const int reps = 20;
    var sw = Stopwatch.StartNew(); byte[] br = []; for (int i = 0; i < reps; i++) br = Br(t.Bytes, CompressionLevel.Fastest); double brUs = sw.Elapsed.TotalMicroseconds / reps;
    sw.Restart(); byte[] gz = []; for (int i = 0; i < reps; i++) gz = Gz(t.Bytes, CompressionLevel.Fastest); double gzUs = sw.Elapsed.TotalMicroseconds / reps;
    rows.Add((t.Name, t.Bytes.Length, br.Length, gz.Length, brUs, gzUs));
}

void Report(string label, IEnumerable<(string Name, int Raw, int Br, int Gz, double BrUs, double GzUs)> set)
{
    var s = set.ToList(); if (s.Count == 0) return;
    long raw = s.Sum(r => (long)r.Raw), br = s.Sum(r => (long)r.Br), gz = s.Sum(r => (long)r.Gz);
    double Med(IEnumerable<double> xs) { var a = xs.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
    Console.WriteLine($"{label,-22} n={s.Count,4}  raw={raw / 1024.0,9:F1} KiB  br={br / 1024.0,8:F1} KiB ({(double)raw / br:F2}x)  gz={gz / 1024.0,8:F1} KiB ({(double)raw / gz:F2}x)  " +
        $"median raw={Med(s.Select(r => (double)r.Raw)) / 1024:F1} KiB  br time med={Med(s.Select(r => r.BrUs)):F0} us max={s.Max(r => r.BrUs):F0} us  gz time med={Med(s.Select(r => r.GzUs)):F0} us  µs/KiB br={s.Sum(r => r.BrUs) / (raw / 1024.0):F1}");
}

Report("all", rows);
Report("< 1 KiB", rows.Where(r => r.Raw < 1024));
Report("1-16 KiB", rows.Where(r => r.Raw >= 1024 && r.Raw < 16384));
Report("16-64 KiB", rows.Where(r => r.Raw >= 16384 && r.Raw < 65536));
Report(">= 64 KiB", rows.Where(r => r.Raw >= 65536));
foreach (var g in rows.GroupBy(r => r.Name.Split("__")[0] + "/" + r.Name.Split("__")[1]).OrderBy(g => g.Key))
    Report(g.Key, g);
var big = rows.OrderByDescending(r => r.Raw).First();
Console.WriteLine($"largest: {big.Name} {big.Raw / 1024.0:F1} KiB -> br {big.Br / 1024.0:F1} KiB in {big.BrUs:F0} us, gz {big.Gz / 1024.0:F1} KiB in {big.GzUs:F0} us");
Console.WriteLine($"grew under br: {rows.Count(r => r.Br >= r.Raw)} of {rows.Count}; under gz: {rows.Count(r => r.Gz >= r.Raw)}");
Console.WriteLine($"cpu: {Environment.ProcessorCount} logical, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
