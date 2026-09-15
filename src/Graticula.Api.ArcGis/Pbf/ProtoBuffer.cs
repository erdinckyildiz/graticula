using System;
using System.Buffers;
using System.Text;

namespace Graticula.Api.ArcGis.Pbf;

/// <summary>
/// The part of the Protocol Buffers wire format a FeatureCollection needs, written by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>No library, because what is needed is small and the format is public.</b> Varints, zigzag,
/// length-delimited fields and packed repeats are the whole of it — the encoding section of
/// Google's published Protocol Buffers documentation. A generated-code dependency would bring a
/// runtime, a code generator in the build and a second model of a response this server already
/// describes, to write about a dozen message types.
/// </para>
/// <para>
/// <b>Nested messages are built inside out.</b> A length-delimited field needs its length before its
/// bytes, so a child is written to its own buffer and then copied into its parent. That buffers the
/// whole response, which the JSON face deliberately does not (ADR-062); the PBF face is bounded by the
/// same response ceiling instead, and says so where it is called.
/// </para>
/// </remarks>
internal sealed class ProtoBuffer
{
    private readonly ArrayBufferWriter<byte> _bytes = new(256);

    /// <summary>How many bytes are written.</summary>
    public int Length => _bytes.WrittenCount;

    /// <summary>The bytes written.</summary>
    public ReadOnlySpan<byte> Span => _bytes.WrittenSpan;

    /// <summary>A varint field.</summary>
    public void UInt(int field, ulong value)
    {
        Tag(field, 0);
        Varint(value);
    }

    /// <summary>A signed field in zigzag encoding, which <c>sint32</c> and <c>sint64</c> use.</summary>
    public void SInt(int field, long value)
    {
        Tag(field, 0);
        Varint(ZigZag(value));
    }

    /// <summary>An <c>int64</c> field, two's complement as a varint.</summary>
    public void Int64(int field, long value)
    {
        Tag(field, 0);
        Varint(unchecked((ulong)value));
    }

    /// <summary>A <c>bool</c> field.</summary>
    public void Bool(int field, bool value) => UInt(field, value ? 1UL : 0UL);

    /// <summary>A <c>double</c> field.</summary>
    public void Double(int field, double value)
    {
        Tag(field, 1);
        BitConverter.TryWriteBytes(_bytes.GetSpan(8), BitConverter.DoubleToInt64Bits(value));
        _bytes.Advance(8);
    }

    /// <summary>A <c>float</c> field.</summary>
    public void Float(int field, float value)
    {
        Tag(field, 5);
        BitConverter.TryWriteBytes(_bytes.GetSpan(4), BitConverter.SingleToInt32Bits(value));
        _bytes.Advance(4);
    }

    /// <summary>A <c>string</c> field.</summary>
    public void String(int field, string value)
    {
        Tag(field, 2);
        int count = Encoding.UTF8.GetByteCount(value);
        Varint((ulong)count);
        Encoding.UTF8.GetBytes(value, _bytes.GetSpan(count));
        _bytes.Advance(count);
    }

    /// <summary>A nested message.</summary>
    public void Message(int field, ProtoBuffer message)
    {
        Tag(field, 2);
        Varint((ulong)message.Length);
        _bytes.Write(message.Span);
    }

    /// <summary>A packed repeated <c>uint32</c>.</summary>
    public void PackedUInt(int field, ReadOnlySpan<uint> values)
    {
        if (values.IsEmpty)
        {
            return;
        }

        ProtoBuffer packed = new();

        foreach (uint value in values)
        {
            packed.Varint(value);
        }

        Tag(field, 2);
        Varint((ulong)packed.Length);
        _bytes.Write(packed.Span);
    }

    /// <summary>A packed repeated <c>sint64</c>.</summary>
    public void PackedSInt(int field, ReadOnlySpan<long> values)
    {
        if (values.IsEmpty)
        {
            return;
        }

        ProtoBuffer packed = new();

        foreach (long value in values)
        {
            packed.Varint(ZigZag(value));
        }

        Tag(field, 2);
        Varint((ulong)packed.Length);
        _bytes.Write(packed.Span);
    }

    /// <summary>A packed repeated <c>uint64</c>.</summary>
    public void PackedUInt64(int field, ReadOnlySpan<long> values)
    {
        if (values.IsEmpty)
        {
            return;
        }

        ProtoBuffer packed = new();

        foreach (long value in values)
        {
            packed.Varint(unchecked((ulong)value));
        }

        Tag(field, 2);
        Varint((ulong)packed.Length);
        _bytes.Write(packed.Span);
    }

    /// <summary>Zigzag, so a small negative number is a small varint.</summary>
    internal static ulong ZigZag(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));

    private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    private void Varint(ulong value)
    {
        Span<byte> span = _bytes.GetSpan(10);
        int at = 0;

        while (value >= 0x80)
        {
            span[at++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[at++] = (byte)value;
        _bytes.Advance(at);
    }
}
