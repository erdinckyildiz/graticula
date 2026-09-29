using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The checked-in glyph ranges are the composite stack ADR-100 describes, and the glyphs every
/// stored style was already drawing did not change.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-100](../../docs/adr/ADR-100-labels-in-more-scripts.md), 2026-09-29.</b> The stack a
/// style names as <c>DejaVu Sans Regular</c> became DejaVu Sans, then Noto Sans, then a Noto
/// family per script, each codepoint taken from the first font that has it — except in a script
/// font's own blocks, where that font is asked first.
/// </para>
/// <para>
/// <b>Read from the repository, not from the build output</b>, because <c>provenance.json</c> —
/// which says which font drew which codepoint — is not copied beside the server, and the ranges
/// under test are the ones that are committed.
/// </para>
/// </remarks>
public sealed class GlyphCompositeTests
{
    /// <summary>
    /// Every codepoint DejaVu Sans answered for before ADR-100, outside the blocks a script font
    /// now claims — Latin, Greek, Cyrillic, the Lao DejaVu has, NKo, punctuation, arrows,
    /// mathematics, Braille and the Latin ligatures.
    /// </summary>
    /// <remarks>
    /// <b>Taken on 2026-09-29 from the ranges as they stood at commit <c>2c9f4ee</c>, before this
    /// change</b>, by decoding them and keeping the codepoints DejaVu's own <c>cmap</c> has. The
    /// ranges then also carried 3,800 boxes for codepoints DejaVu does not have, which ADR-100
    /// removed; those are not in this list, because a box was never a glyph.
    /// </remarks>
    private const string DejaVuBefore =
        "0021-007E,00A1-02E9,02EC-02EE,02F3-02F3,02F7-02F7,0300-034E,0351-0353,0357-0358,"
        + "035A-035A,035C-0362,0370-0377,037A-037E,0384-038A,038C-038C,038E-03A1,03A3-0525,"
        + "07C0-07E7,07EB-07F5,07F8-07FA,0E81-0E82,0E84-0E84,0E87-0E88,0E8A-0E8A,0E8D-0E8D,"
        + "0E94-0E97,0E99-0E9F,0EA1-0EA3,0EA5-0EA5,0EA7-0EA7,0EAA-0EAB,0EAD-0EB9,0EBB-0EBD,"
        + "0EC0-0EC4,0EC6-0EC6,0EC8-0ECD,0ED0-0ED9,0EDC-0EDD,1E00-1EFB,1F00-1F15,1F18-1F1D,"
        + "1F20-1F45,1F48-1F4D,1F50-1F57,1F59-1F59,1F5B-1F5B,1F5D-1F5D,1F5F-1F7D,1F80-1FB4,"
        + "1FB6-1FC4,1FC6-1FD3,1FD6-1FDB,1FDD-1FEF,1FF2-1FF4,1FF6-1FFE,2010-2027,2030-205E,"
        + "2070-2071,2074-208E,2090-209C,20A0-20B5,20B8-20BA,20BD-20BD,20D0-20D1,20D6-20D7,"
        + "20DB-20DC,20E1-20E1,2100-2109,210B-2149,214B-214B,214E-214E,2150-2185,2189-2189,"
        + "2190-2311,2318-2319,231C-2321,2324-2328,232B-232C,2373-2375,237A-237A,237D-237D,"
        + "2387-2387,2394-2394,239B-23AE,23CE-23CF,23E3-23E3,23E5-23E5,23E8-23E8,2422-2423,"
        + "2460-2469,2500-269C,269E-26B8,26C0-26C3,26E2-26E2,2701-2704,2706-2709,270C-2727,"
        + "2729-274B,274D-274D,274F-2752,2756-2756,2758-275E,2761-2794,2798-27AF,27B1-27BE,"
        + "27C5-27C6,27E0-27E0,27E6-27EB,27F0-27FF,2801-28FF,2906-2907,290A-290B,2940-2941,"
        + "2983-2984,29CE-29D5,29EB-29EB,29FA-29FB,2A00-2A02,2A0C-2A1C,2A2F-2A2F,2A6A-2A6B,"
        + "2A7D-2AA0,2AAE-2ABA,2AF9-2AFA,2B00-2B1A,2B1F-2B24,2B53-2B54,FB00-FB06";

    /// <summary>SHA-256 over those glyphs' protobuf records, in codepoint order, at <c>2c9f4ee</c>.</summary>
    private const string DejaVuBeforeDigest = "9dca246384b0a8ef6a3bf6e8b519d3405ca56eede1dc3a1994d2b8bb53278350";

    private static readonly Lazy<string> Folder = new(() =>
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !File.Exists(Path.Combine(at.FullName, "graticula.sln")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);

        return Path.Combine(at.FullName, "src", "Graticula.Host", "glyphs");
    });

    private static readonly Lazy<Dictionary<int, byte[]>> Glyphs = new(() =>
    {
        Dictionary<int, byte[]> all = [];

        foreach (string path in Directory.EnumerateFiles(Path.Combine(Folder.Value, GlyphStore.Fallback), "*.pbf"))
        {
            foreach ((int code, byte[] record) in Records(File.ReadAllBytes(path)))
            {
                all[code] = record;
            }
        }

        return all;
    });

    private static JsonElement Provenance()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder.Value, "provenance.json")));

        return document.RootElement.Clone();
    }

    /// <summary>
    /// <b>The owner's rule, pinned: every Latin, Greek and Cyrillic glyph a stored style has been
    /// drawing is byte for byte what it was.</b> DejaVu is first in the stack for exactly this
    /// reason, and a Noto font that answered first for one of these codepoints — or a generator
    /// change that moved one pixel of one distance field — would change the digest.
    /// </summary>
    [Fact]
    public void The_glyphs_DejaVu_drew_before_are_byte_identical()
    {
        List<int> codes = [.. Codepoints(DejaVuBefore)];

        Assert.Equal(3563, codes.Count);

        List<int> missing = [.. codes.Where(c => !Glyphs.Value.ContainsKey(c))];

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} glyphs DejaVu drew before ADR-100 are no longer served, starting at U+"
            + (missing.Count > 0 ? missing[0].ToString("X4", CultureInfo.InvariantCulture) : string.Empty));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (int code in codes)
        {
            hash.AppendData(Glyphs.Value[code]);
        }

        Assert.Equal(DejaVuBeforeDigest, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Each script's first letter is served, from the font the stack says.</summary>
    [Theory]
    [InlineData(0x0041, "DejaVuSans.ttf")]              // Latin A
    [InlineData(0x0130, "DejaVuSans.ttf")]              // İ
    [InlineData(0x0391, "DejaVuSans.ttf")]              // Greek Alpha
    [InlineData(0x0416, "DejaVuSans.ttf")]              // Cyrillic Zhe
    [InlineData(0x0627, "NotoSansArabic-Regular.ttf")]  // alef, which DejaVu also has
    [InlineData(0xFEDF, "NotoSansArabic-Regular.ttf")]  // lam, initial: what MapLibre's RTL plugin asks for
    [InlineData(0xFEFB, "NotoSansArabic-Regular.ttf")]  // lam-alef ligature, isolated
    [InlineData(0x05D0, "NotoSansHebrew-Regular.ttf")]  // alef, which DejaVu also has
    [InlineData(0x0531, "NotoSansArmenian-Regular.ttf")]
    [InlineData(0x10D0, "NotoSansGeorgian-Regular.ttf")]
    [InlineData(0x0915, "NotoSansDevanagari-Regular.ttf")]
    [InlineData(0x0995, "NotoSansBengali-Regular.ttf")]
    [InlineData(0x0B95, "NotoSansTamil-Regular.ttf")]
    [InlineData(0x0E01, "NotoSansThai-Regular.ttf")]
    public void A_codepoint_is_drawn_from_the_font_the_stack_says(int code, string font)
    {
        Assert.True(
            Glyphs.Value.ContainsKey(code),
            $"U+{code:X4} is in no checked-in range, so a label using it draws nothing.");

        Assert.Equal(font, SourceOf(code));
    }

    /// <summary>
    /// <b>A space has a glyph.</b> None did until ADR-100: the generator threw every inkless
    /// codepoint away, and a client with no glyph for a codepoint has no advance for it.
    /// </summary>
    [Theory]
    [InlineData(0x0020)]
    [InlineData(0x00A0)]
    public void A_space_is_served_with_its_advance(int code)
    {
        Assert.True(Glyphs.Value.TryGetValue(code, out byte[]? record), $"U+{code:X4} has no glyph.");

        Assert.True(Field(record!, 7) > 0, $"U+{code:X4} is served with no advance, so words run together.");
    }

    /// <summary>
    /// <b>A box is not a glyph.</b> Until ADR-100 a codepoint the font lacked was drawn as the
    /// font's box and served as if it were a letter — 3,800 of them, the C0 controls and whole
    /// Indic blocks among them. A client that is handed a box draws a box; one that is handed
    /// nothing can fall back.
    /// </summary>
    [Fact]
    public void Every_glyph_served_is_one_some_font_in_the_stack_has()
    {
        HashSet<int> sourced = [.. Sources().Values.SelectMany(static set => set)];

        List<int> unsourced = [.. Glyphs.Value.Keys.Where(c => !sourced.Contains(c)).Order()];

        Assert.True(
            unsourced.Count == 0,
            $"{unsourced.Count} glyphs are served that no font's cmap has, starting at U+"
            + (unsourced.Count > 0 ? unsourced[0].ToString("X4", CultureInfo.InvariantCulture) : string.Empty));

        Assert.DoesNotContain(Glyphs.Value.Keys, static c => c < 0x20 || (c >= 0x7F && c <= 0x9F));
    }

    /// <summary>
    /// <b>Every range is on the grid a client asks on.</b> The ligatures were written as
    /// <c>64256-64335.pbf</c> for a year; clients only ever ask for <c>start-(start+255)</c>, so
    /// the file shipped and was never served.
    /// </summary>
    [Fact]
    public void Every_range_is_one_a_client_can_name()
    {
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Folder.Value, GlyphStore.Fallback), "*.pbf"))
        {
            string range = Path.GetFileNameWithoutExtension(path);

            Assert.True(GlyphStore.TryRange(range, out _, out _), $"{range}.pbf is off the 256-codepoint grid.");
        }
    }

    /// <summary>
    /// <b>A range no font in the stack covers is still not written</b>, so it is still refused
    /// rather than substituted — ADR-027 §5's rule, which ADR-100 keeps. Gurmukhi and Telugu are
    /// scripts this product does not carry; Han is carried only by a <c>GLYPHS_CJK=1</c> build.
    /// </summary>
    [Theory]
    [InlineData(0x0A00)] // Gurmukhi, Gujarati
    [InlineData(0x0C00)] // Telugu, Kannada
    public void A_range_no_font_covers_is_not_written(int start)
    {
        string path = Path.Combine(
            Folder.Value, GlyphStore.Fallback, string.Create(CultureInfo.InvariantCulture, $"{start}-{start + 255}.pbf"));

        Assert.False(File.Exists(path), $"{Path.GetFileName(path)} exists, and no font in the stack draws that script.");
    }

    [Fact]
    public void Han_is_written_only_by_a_build_that_asked_for_it()
    {
        bool cjk = Provenance().GetProperty("cjk").GetBoolean();

        Assert.Equal(cjk, File.Exists(Path.Combine(Folder.Value, GlyphStore.Fallback, "19968-20223.pbf")));
    }

    // ---------- reading provenance.json and the wire format ----------

    private static string? SourceOf(int code) =>
        Sources().FirstOrDefault(pair => pair.Value.Contains(code)).Key;

    private static Dictionary<string, HashSet<int>> Sources()
    {
        Dictionary<string, HashSet<int>> sources = [];

        foreach (JsonProperty font in Provenance().GetProperty("sources").EnumerateObject())
        {
            sources[font.Name] = [.. font.Value.EnumerateArray().SelectMany(static r => Codepoints(r.GetString()!))];
        }

        return sources;
    }

    private static IEnumerable<int> Codepoints(string runs)
    {
        foreach (string run in runs.Split(','))
        {
            string[] ends = run.Split('-');
            int first = int.Parse(ends[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int last = int.Parse(ends[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            for (int code = first; code <= last; code++)
            {
                yield return code;
            }
        }
    }

    /// <summary>A varint, advancing the position past it.</summary>
    private static ulong Varint(byte[] bytes, ref int at)
    {
        ulong value = 0;
        int shift = 0;

        while (at < bytes.Length)
        {
            byte piece = bytes[at++];
            value |= (ulong)(piece & 0x7F) << shift;

            if ((piece & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return value;
    }

    /// <summary>Each glyph message in a range, with its codepoint. Written from the format.</summary>
    private static List<(int Code, byte[] Record)> Records(byte[] pbf)
    {
        List<(int, byte[])> records = [];
        int i = 0;

        // glyphs { repeated fontstack stacks = 1 }
        Varint(pbf, ref i);
        int length = (int)Varint(pbf, ref i);
        int end = i + length;

        while (i < end)
        {
            ulong key = Varint(pbf, ref i);

            if ((key & 7) != 2)
            {
                Varint(pbf, ref i);
                continue;
            }

            int size = (int)Varint(pbf, ref i);

            if ((key >> 3) == 3)
            {
                byte[] record = pbf[i..(i + size)];
                int at = 0;
                Varint(record, ref at);
                int code = (int)Varint(record, ref at);
                records.Add((code, record));
            }

            i += size;
        }

        return records;
    }

    /// <summary>One varint field of a glyph record, or 0 when it is absent.</summary>
    private static ulong Field(byte[] record, int number)
    {
        int at = 0;

        while (at < record.Length)
        {
            ulong key = Varint(record, ref at);

            if ((key & 7) == 2)
            {
                int size = (int)Varint(record, ref at);
                at += size;
                continue;
            }

            ulong value = Varint(record, ref at);

            if ((int)(key >> 3) == number)
            {
                return value;
            }
        }

        return 0;
    }
}
