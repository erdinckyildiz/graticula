using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

namespace GisServer.Overlay.Worker;

/// <summary>
/// One overlay at a time, in a process the server can kill.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is Q-97's answer, and the reason it is an executable.</b>
/// <see href="../../benchmarks/geometry-overlay/RESULTS.md">Measurement</see>
/// invalidated A-042: no property of the input bounds the cost of an overlay.
/// The only reliable bound is on execution, and .NET offers none in-process —
/// OverlayNG never checks for cancellation, and <c>Thread.Abort</c> does not
/// exist. A process can be killed and can be given a heap ceiling, so the work
/// happens in one.
/// </para>
/// <para>
/// <b>Long-lived and pooled, not one process per request.</b> A launch is tens
/// of milliseconds and the operations that matter are milliseconds, so
/// per-request spawning would make the common case slower than the work. The
/// worker handles requests in sequence until the server kills it or closes its
/// input.
/// </para>
/// <para>
/// <b>The protocol is deliberately dull:</b> a four-byte little-endian length,
/// then that many bytes of UTF-8 JSON, in both directions. Geometry travels as
/// WKB in base64, which <see cref="GisServer.Geometries.WkbReader"/> and
/// <see cref="GisServer.Geometries.WkbWriter"/> already produce and consume, so
/// no third format exists to disagree with the other two.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>The largest request the worker will read, in bytes.</summary>
    /// <remarks>
    /// A worker that trusts its length prefix is a worker a corrupt pipe can
    /// make allocate two gigabytes. The server writes these frames, so this is
    /// a bug bound rather than an attack bound — which is exactly the kind that
    /// goes unnoticed without a number.
    /// </remarks>
    private const int MaximumFrame = 256 * 1024 * 1024;

    private static int Main()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();

        // Out of the loop: a stackalloc inside one accumulates on the frame
        // until the method returns, which for a loop that never returns is an
        // overflow with a very long fuse.
        byte[] length = new byte[4];

        while (true)
        {
            byte[]? frame = ReadFrame(input);

            if (frame is null)
            {
                // The server closed the pipe. An orderly end, not a failure.
                return 0;
            }

            byte[] response = Handle(frame);

            BinaryPrimitives.WriteInt32LittleEndian(length, response.Length);

            output.Write(length);
            output.Write(response);
            output.Flush();
        }
    }

    private static byte[]? ReadFrame(Stream input)
    {
        Span<byte> header = stackalloc byte[4];

        if (!ReadExactly(input, header))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length is < 0 or > MaximumFrame)
        {
            throw new InvalidDataException(
                $"The server sent a frame of {length} bytes, and the limit is {MaximumFrame}.");
        }

        byte[] frame = new byte[length];

        return ReadExactly(input, frame) ? frame : null;
    }

    /// <summary>Fills the span, or reports that the pipe ended.</summary>
    /// <remarks>
    /// A pipe read returns what is available, not what was asked for. Treating
    /// one read as the whole frame works on small messages and fails on the
    /// large ones — which here means the adversarial input, which is the case
    /// that must not behave differently.
    /// </remarks>
    private static bool ReadExactly(Stream input, Span<byte> destination)
    {
        int filled = 0;

        while (filled < destination.Length)
        {
            int read = input.Read(destination[filled..]);

            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }

    private static byte[] Handle(byte[] frame)
    {
        try
        {
            OverlayRequest request =
                JsonSerializer.Deserialize<OverlayRequest>(frame) ?? throw new InvalidDataException(
                    "The request was the JSON literal null.");

            return JsonSerializer.SerializeToUtf8Bytes(Compute(request));
        }
        catch (OutOfMemoryException)
        {
            // Reported rather than swallowed, but the process is no longer
            // trustworthy: the GC hard limit has been hit and the heap is in
            // whatever state that left it. The server retires this worker.
            return JsonSerializer.SerializeToUtf8Bytes(
                new OverlayResponse { Refusal = "OutOfMemory", Message = "The overlay ran out of memory." });
        }
        catch (Exception e)
        {
            return JsonSerializer.SerializeToUtf8Bytes(
                new OverlayResponse { Refusal = "Invalid", Message = e.Message });
        }
    }

    private static OverlayResponse Compute(OverlayRequest request)
    {
        Stopwatch clock = Stopwatch.StartNew();

        GeometryFactory factory = new(new PrecisionModel(), request.Srid);
        WKBReader reader = new(NtsGeometryServices.Instance) { HandleSRID = false };

        List<NetTopologySuite.Geometries.Geometry> left = Read(request.Left, reader, factory);
        List<NetTopologySuite.Geometries.Geometry> right = Read(request.Right, reader, factory);

        if (left.Count == 0)
        {
            return new OverlayResponse
            {
                Refusal = "Invalid",
                Message = "No geometry was given to overlay.",
            };
        }

        NetTopologySuite.Geometries.Geometry first = Combine(left, factory);

        NetTopologySuite.Geometries.Geometry? second =
            right.Count == 0 ? null : Combine(right, factory);

        // <b>The pre-flight, before any overlay arithmetic.</b> Counting segment
        // pairs whose boxes overlap is an R-tree build and query — no arithmetic
        // on the segments themselves. Finding 16 measured 83 ms foreseeing a
        // 17-second operation, and also measured it under-predicting the
        // adversarial case fourteenfold. It is a cheap filter, and the deadline
        // the server enforces is the actual bound.
        long candidates = second is null ? SelfPairs(first) : CandidatePairs(first, second);

        if (request.MaximumCandidatePairs > 0 && candidates > request.MaximumCandidatePairs)
        {
            return new OverlayResponse
            {
                Refusal = "TooLarge",
                Message =
                    $"The pre-flight counted {candidates:N0} candidate segment pairs and the limit "
                    + $"is {request.MaximumCandidatePairs:N0}. Overlay cost grows with crossings "
                    + "rather than with vertex count, so a small input can be an expensive one.",
                CandidatePairs = candidates,
                Milliseconds = clock.ElapsedMilliseconds,
            };
        }

        NetTopologySuite.Geometries.Geometry result = request.Operation switch
        {
            "Intersect" => OverlayNGRobust.Overlay(first, second, SpatialFunction.Intersection),
            "Difference" => OverlayNGRobust.Overlay(first, second, SpatialFunction.Difference),
            _ => second is null
                ? OverlayNGRobust.Union(first)
                : OverlayNGRobust.Overlay(first, second, SpatialFunction.Union),
        };

        WKBWriter writer = new();

        List<string> geometries = [];

        // <b>Flattened, because an overlay legitimately returns mixed
        // dimensions and our model has no collection.</b> Two combs intersect in
        // squares where the teeth cross and in bare line segments where their
        // edges touch, which OverlayNG returns as a GeometryCollection —
        // and GisServer.Geometries deliberately does not model one
        // (WkbReader: "v1 serves homogeneous layers"). Emitting the parts
        // separately loses nothing: the ArcGIS response is a list of geometries
        // already. Discarding the lower-dimension pieces would be tidier and
        // would be throwing away part of the answer.
        foreach (NetTopologySuite.Geometries.Geometry part in Flatten(result))
        {
            geometries.Add(Convert.ToBase64String(writer.Write(part)));
        }

        return new OverlayResponse
        {
            Geometries = geometries,
            CandidatePairs = candidates,
            Milliseconds = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// A result as parts our geometry model can carry.
    /// </summary>
    /// <remarks>
    /// <b>Only <c>GeometryCollection</c> is broken up.</b> A MultiPolygon is a
    /// single geometry in our model and stays one; splitting it would turn one
    /// answer into fifty and lose the fact that they are one feature's worth of
    /// shape.
    /// </remarks>
    private static IEnumerable<NetTopologySuite.Geometries.Geometry> Flatten(
        NetTopologySuite.Geometries.Geometry geometry)
    {
        if (geometry.IsEmpty)
        {
            yield break;
        }

        // GetType() rather than a pattern: MultiPolygon and the rest derive from
        // GeometryCollection, and matching on the base type would split them too.
        if (geometry.GetType() == typeof(GeometryCollection))
        {
            foreach (NetTopologySuite.Geometries.Geometry part in
                ((GeometryCollection)geometry).Geometries)
            {
                foreach (NetTopologySuite.Geometries.Geometry inner in Flatten(part))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        yield return geometry;
    }

    private static List<NetTopologySuite.Geometries.Geometry> Read(
        string[]? encoded, WKBReader reader, GeometryFactory factory)
    {
        List<NetTopologySuite.Geometries.Geometry> geometries = [];

        foreach (string wkb in encoded ?? [])
        {
            NetTopologySuite.Geometries.Geometry geometry = reader.Read(Convert.FromBase64String(wkb));
            geometry.SRID = factory.SRID;
            geometries.Add(geometry);
        }

        return geometries;
    }

    private static NetTopologySuite.Geometries.Geometry Combine(
        List<NetTopologySuite.Geometries.Geometry> parts, GeometryFactory factory) =>
        parts.Count == 1 ? parts[0] : factory.BuildGeometry(parts);

    /// <summary>
    /// Segment pairs whose bounding boxes overlap, across two geometries.
    /// </summary>
    /// <remarks>
    /// <b>An R-tree build and query, which is O((n+m) log n)</b> and touches no
    /// segment arithmetic. That is what makes it affordable on every request;
    /// the overlay it predicts is the expensive part.
    /// </remarks>
    private static long CandidatePairs(
        NetTopologySuite.Geometries.Geometry a, NetTopologySuite.Geometries.Geometry b)
    {
        STRtree<Envelope> tree = new();

        foreach (Envelope segment in Segments(a))
        {
            tree.Insert(segment, segment);
        }

        if (tree.Count == 0)
        {
            return 0;
        }

        tree.Build();

        long pairs = 0;

        foreach (Envelope segment in Segments(b))
        {
            pairs += tree.Query(segment).Count;
        }

        return pairs;
    }

    /// <summary>
    /// Segment pairs a self-union would have to resolve.
    /// </summary>
    /// <remarks>
    /// A union of one geometry still noded against itself, so the same estimate
    /// applies with the geometry on both sides — minus each segment's match with
    /// itself, which is not a crossing.
    /// </remarks>
    private static long SelfPairs(NetTopologySuite.Geometries.Geometry a)
    {
        long pairs = CandidatePairs(a, a);
        long segments = 0;

        foreach (Envelope _ in Segments(a))
        {
            segments++;
        }

        return Math.Max(0, pairs - segments);
    }

    private static IEnumerable<Envelope> Segments(NetTopologySuite.Geometries.Geometry geometry)
    {
        foreach (Coordinate[] ring in Rings(geometry))
        {
            for (int i = 1; i < ring.Length; i++)
            {
                yield return new Envelope(ring[i - 1], ring[i]);
            }
        }
    }

    private static IEnumerable<Coordinate[]> Rings(NetTopologySuite.Geometries.Geometry geometry)
    {
        switch (geometry)
        {
            case GeometryCollection collection:
                foreach (NetTopologySuite.Geometries.Geometry part in collection.Geometries)
                {
                    foreach (Coordinate[] ring in Rings(part))
                    {
                        yield return ring;
                    }
                }

                break;

            case NetTopologySuite.Geometries.Polygon polygon:
                yield return polygon.ExteriorRing.Coordinates;

                foreach (LineString hole in polygon.InteriorRings)
                {
                    yield return hole.Coordinates;
                }

                break;

            case LineString line:
                yield return line.Coordinates;
                break;

            default:
                // A point has no segments, which is the honest answer rather
                // than an error: overlaying with a point is cheap.
                break;
        }
    }
}

/// <summary>One request, as the server writes it.</summary>
internal sealed class OverlayRequest
{
    /// <summary>Intersect, Union or Difference.</summary>
    public string Operation { get; set; } = "Intersect";

    /// <summary>The first operand, WKB in base64.</summary>
    public string[]? Left { get; set; }

    /// <summary>The second operand, WKB in base64.</summary>
    public string[]? Right { get; set; }

    /// <summary>The reference both are in.</summary>
    public int Srid { get; set; }

    /// <summary>The pre-flight threshold, or zero for none.</summary>
    public long MaximumCandidatePairs { get; set; }
}

/// <summary>One response, as the server reads it.</summary>
internal sealed class OverlayResponse
{
    /// <summary>What came back, WKB in base64.</summary>
    public List<string> Geometries { get; set; } = [];

    /// <summary>Empty when it worked.</summary>
    public string Refusal { get; set; } = string.Empty;

    /// <summary>A sentence for the caller.</summary>
    public string? Message { get; set; }

    /// <summary>What the pre-flight counted.</summary>
    public long CandidatePairs { get; set; }

    /// <summary>How long it took inside the worker.</summary>
    public long Milliseconds { get; set; }
}
