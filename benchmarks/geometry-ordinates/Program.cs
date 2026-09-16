using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Geometries;

// 50,000 polygons of 41 vertices (two rings) = ~2 million coordinates, as ISO WKB from PostGIS would send them.
const int Features = 50_000;
List<byte[]> wkb = new(Features);
Random random = new(76);

bool points = args.Contains("points");
for (int f = 0; f < (points ? 1_000_000 : Features); f++)
{
    double cx = random.NextDouble() * 1_000_000, cy = random.NextDouble() * 1_000_000;
    if (points)
    {
        byte[] b = new byte[21];
        b[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(5), cx);
        BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(13), cy);
        wkb.Add(b);
    }
    else
    {
        wkb.Add(Polygon(cx, cy, shell: 30, hole: 11));
    }
}

static byte[] Polygon(double cx, double cy, int shell, int hole)
{
    int size = 1 + 4 + 4 + (4 + (shell + 1) * 16) + (4 + (hole + 1) * 16);
    byte[] b = new byte[size];
    int o = 0;
    b[o++] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), 3); o += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), 2); o += 4;
    foreach ((int n, double r) in new[] { (shell, 100.0), (hole, 30.0) })
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), (uint)(n + 1)); o += 4;
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * (i % n) / n * (r > 50 ? 1 : -1);
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(o), cx + r * Math.Cos(a)); o += 8;
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(o), cy + r * Math.Sin(a)); o += 8;
        }
    }
    return b;
}

ArrayBufferWriter<byte> buffer = new(64 * 1024 * 1024);

long Run()
{
    buffer.ResetWrittenCount();
    using Utf8JsonWriter json = new(buffer);
    json.WriteStartArray();
    long vertices = 0;
    foreach (byte[] one in wkb)
    {
        Geometry g = WkbReader.Read(one);
        ArcGisGeometryWriter.Write(json, g, 3857);
        vertices++;
    }
    json.WriteEndArray();
    json.Flush();
    return vertices;
}

for (int warm = 0; warm < 3; warm++) Run();

List<double> ms = new();
List<long> bytes = new();

for (int i = 0; i < 9; i++)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long before = GC.GetAllocatedBytesForCurrentThread();
    Stopwatch watch = Stopwatch.StartNew();
    Run();
    watch.Stop();
    bytes.Add(GC.GetAllocatedBytesForCurrentThread() - before);
    ms.Add(watch.Elapsed.TotalMilliseconds);
}

ms.Sort();
Console.WriteLine($"median {ms[ms.Count / 2]:F1} ms  min {ms[0]:F1} ms  allocated {bytes.Min() / 1024.0 / 1024.0:F2} MB  json {buffer.WrittenCount / 1024 / 1024} MB");
