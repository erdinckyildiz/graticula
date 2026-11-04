using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Graticula.Import.Reader;

/// <summary>
/// A PDF's first page drawn as a PNG, by PDFium — the picture a document item is given when its owner gave it none
/// (ADR-268, D-321).
/// </summary>
/// <remarks>
/// <para>
/// <b>Here and nowhere else, for ADR-009 §2.2's reason.</b> A PDF is a file somebody else chose, and PDFium is the parser
/// of it; a document that breaks the parser breaks this process, which the host kills and counts, and not the one
/// serving requests. The host never loads PDFium.
/// </para>
/// <para>
/// <b>Loaded by full path from <c>pdfium/&lt;rid&gt;/</c> beside the reader</b>, as GDAL is (<see cref="GdalNative"/>),
/// so a PDFium elsewhere on a host is never the one used, and resolved entry point by entry point rather than through
/// <c>DllImport</c>, so this assembly's import resolver stays GDAL's alone. The build in the lock has V8 and XFA off: a
/// document's JavaScript and XFA forms are not run, only its page drawn.
/// </para>
/// <para>
/// <b>Opened from memory, not from a path.</b> The file is read once, with the size the host allows, and PDFium is given
/// the bytes — so a path's encoding never differs between platforms, and nothing is read lazily after the bound was
/// checked.
/// </para>
/// </remarks>
internal static unsafe class PdfFirstPage
{
    private const int ErrorPassword = 4;

    private static readonly object Gate = new();
    private static bool _ready;

    private static delegate* unmanaged<void*, void> _init;
    private static delegate* unmanaged<void*, nuint, byte*, IntPtr> _loadMemory;
    private static delegate* unmanaged<nuint> _lastError;
    private static delegate* unmanaged<IntPtr, int> _pageCount;
    private static delegate* unmanaged<IntPtr, int, IntPtr> _loadPage;
    private static delegate* unmanaged<IntPtr, float> _pageWidth;
    private static delegate* unmanaged<IntPtr, float> _pageHeight;
    private static delegate* unmanaged<int, int, int, IntPtr> _bitmapCreate;
    private static delegate* unmanaged<IntPtr, int, int, int, int, nuint, int> _bitmapFill;
    private static delegate* unmanaged<IntPtr, IntPtr, int, int, int, int, int, int, void> _render;
    private static delegate* unmanaged<IntPtr, byte*> _bitmapBuffer;
    private static delegate* unmanaged<IntPtr, int> _bitmapStride;
    private static delegate* unmanaged<IntPtr, void> _bitmapDestroy;
    private static delegate* unmanaged<IntPtr, void> _closePage;
    private static delegate* unmanaged<IntPtr, void> _closeDocument;

    /// <summary>The library's folder for this platform, whether or not it is laid out.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "pdfium",
        (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsLinux() ? "linux-" : "osx-")
        + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

    /// <summary>
    /// Draws page 1 of <paramref name="path"/> no wider than <paramref name="width"/> and no taller than twice that, white
    /// behind it, and answers it as a PNG — or as a refusal the host turns into "no picture", never into a failed upload.
    /// </summary>
    public static object Draw(string path, int width, long maximumBytes)
    {
        width = Math.Clamp(width, 16, 2048);
        FileInfo file = new(path);

        if (!file.Exists || file.Length == 0 || file.Length > maximumBytes)
        {
            return new { ok = false, refused = true, error = "The file is empty, missing or larger than a document may be." };
        }

        Load();
        byte[] content = File.ReadAllBytes(path);

        fixed (byte* bytes = content)
        {
            IntPtr document = _loadMemory(bytes, (nuint)content.Length, null);

            if (document == IntPtr.Zero)
            {
                // FPDF_DWORD is C's unsigned long: 32 bits on Windows, 64 on Linux, so it is read as a native word.
                nuint error = _lastError();
                return new
                {
                    ok = false,
                    refused = true,
                    error = error == ErrorPassword ? "The PDF is protected by a password." : $"PDFium could not open it (error {error}).",
                };
            }

            try
            {
                int pages = _pageCount(document);
                IntPtr page = pages > 0 ? _loadPage(document, 0) : IntPtr.Zero;

                if (page == IntPtr.Zero)
                {
                    return new { ok = false, refused = true, error = "The PDF has no page to draw." };
                }

                try
                {
                    float across = _pageWidth(page);
                    float down = _pageHeight(page);

                    if (!(across > 0 && down > 0) || float.IsInfinity(across) || float.IsInfinity(down))
                    {
                        return new { ok = false, refused = true, error = "The first page has no size." };
                    }

                    double scale = Math.Min(width / (double)across, 2.0 * width / down);
                    int w = Math.Max(1, (int)Math.Round(across * scale));
                    int h = Math.Max(1, (int)Math.Round(down * scale));
                    IntPtr bitmap = _bitmapCreate(w, h, 0);

                    if (bitmap == IntPtr.Zero)
                    {
                        return new { ok = false, refused = true, error = "PDFium could not make a picture of that size." };
                    }

                    try
                    {
                        _bitmapFill(bitmap, 0, 0, w, h, (nuint)0xFFFFFFFFu);

                        // FPDF_ANNOT: the page as a reader sees it, with its annotations.
                        _render(bitmap, page, 0, 0, w, h, 0, 0x01);

                        byte[] png = Png(_bitmapBuffer(bitmap), _bitmapStride(bitmap), w, h);
                        return new { ok = true, png = Convert.ToBase64String(png), width = w, height = h, pages };
                    }
                    finally
                    {
                        _bitmapDestroy(bitmap);
                    }
                }
                finally
                {
                    _closePage(page);
                }
            }
            finally
            {
                _closeDocument(document);
            }
        }
    }

    /// <summary>Loads the library once, by full path, and resolves every entry point it is asked for.</summary>
    private static void Load()
    {
        lock (Gate)
        {
            if (_ready)
            {
                return;
            }

            string file = Path.Combine(Root, OperatingSystem.IsWindows() ? "pdfium.dll" : "libpdfium.so");

            if (!File.Exists(file))
            {
                throw new InvalidOperationException(
                    $"PDFium is not at '{file}'. tools/pdfium-natives.py lays it out at build time (ADR-268); this platform "
                    + "has no entry in deploy/pdfium/pdfium-natives.lock.json, or the build did not run it.");
            }

            IntPtr library = NativeLibrary.Load(file);
            IntPtr Export(string name) => NativeLibrary.GetExport(library, name);

            _init = (delegate* unmanaged<void*, void>)Export("FPDF_InitLibraryWithConfig");
            _loadMemory = (delegate* unmanaged<void*, nuint, byte*, IntPtr>)Export("FPDF_LoadMemDocument64");
            _lastError = (delegate* unmanaged<nuint>)Export("FPDF_GetLastError");
            _pageCount = (delegate* unmanaged<IntPtr, int>)Export("FPDF_GetPageCount");
            _loadPage = (delegate* unmanaged<IntPtr, int, IntPtr>)Export("FPDF_LoadPage");
            _pageWidth = (delegate* unmanaged<IntPtr, float>)Export("FPDF_GetPageWidthF");
            _pageHeight = (delegate* unmanaged<IntPtr, float>)Export("FPDF_GetPageHeightF");
            _bitmapCreate = (delegate* unmanaged<int, int, int, IntPtr>)Export("FPDFBitmap_Create");
            _bitmapFill = (delegate* unmanaged<IntPtr, int, int, int, int, nuint, int>)Export("FPDFBitmap_FillRect");
            _render = (delegate* unmanaged<IntPtr, IntPtr, int, int, int, int, int, int, void>)Export("FPDF_RenderPageBitmap");
            _bitmapBuffer = (delegate* unmanaged<IntPtr, byte*>)Export("FPDFBitmap_GetBuffer");
            _bitmapStride = (delegate* unmanaged<IntPtr, int>)Export("FPDFBitmap_GetStride");
            _bitmapDestroy = (delegate* unmanaged<IntPtr, void>)Export("FPDFBitmap_Destroy");
            _closePage = (delegate* unmanaged<IntPtr, void>)Export("FPDF_ClosePage");
            _closeDocument = (delegate* unmanaged<IntPtr, void>)Export("FPDF_CloseDocument");

            // FPDF_LIBRARY_CONFIG version 2: no font paths, no V8 isolate, the default renderer.
            long* config = stackalloc long[6];
            new Span<long>(config, 6).Clear();
            *(int*)config = 2;
            _init(config);
            _ready = true;
        }
    }

    /// <summary>A BGRx bitmap as an 8-bit RGB PNG: one IDAT, filter 0 on every row.</summary>
    private static byte[] Png(byte* pixels, int stride, int width, int height)
    {
        byte[] raw = new byte[(1 + (width * 3)) * height];
        int at = 0;

        for (int y = 0; y < height; y++)
        {
            raw[at++] = 0;
            byte* row = pixels + ((long)y * stride);

            for (int x = 0; x < width; x++)
            {
                raw[at++] = row[(x * 4) + 2];
                raw[at++] = row[(x * 4) + 1];
                raw[at++] = row[x * 4];
            }
        }

        using MemoryStream png = new();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR"u8, header);

        using (MemoryStream compressed = new())
        {
            using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(png, "IDAT"u8, compressed.ToArray());
        }

        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(kind);
        png.Write(data);

        uint crc = Crc(Crc(0xFFFFFFFF, kind), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        uint[] table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}
