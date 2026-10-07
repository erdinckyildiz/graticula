using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using OSGeo.OGR;
using Dataset = OSGeo.GDAL.Dataset;
using Gdal = OSGeo.GDAL.Gdal;
using GdalConst = OSGeo.GDAL.GdalConst;

namespace Graticula.Import.Reader;

/// <summary>
/// A layer's field domains written into an exported GeoPackage or File Geodatabase as domains — ADR-106 conditions 1
/// and 2, which ArcGIS Pro and QGIS show as a pick list rather than as bare codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>After the rows, on the file GDAL wrote.</b> The rows are staged as GeoJSON, which has no domains, so the copy GDAL
/// makes has none either; they are added to the dataset afterwards and each field is pointed at its own
/// (<c>AlterFieldDefn</c> with <c>ALTER_DOMAIN_FLAG</c>).
/// </para>
/// <para>
/// <b>A coded-value domain is made through GDAL's C function.</b> The C# bindings wrap range and glob domains and not
/// <c>OGR_CodedFldDomain_Create</c>, whose argument is an array of structures; so the function is looked up in the GDAL
/// library MaxRev has already loaded — <c>gdal.dll</c>, or <c>libgdal.so.N</c> on Linux, a name <c>DllImport</c> would
/// not find — and its result handed to the bindings' own <see cref="FieldDomain"/>, which then owns it.
/// </para>
/// </remarks>
internal static class ExportDomains
{
    /// <summary>GDAL's <c>ALTER_DOMAIN_FLAG</c>, which the C# bindings do not expose.</summary>
    private const int AlterDomainFlag = 0x40;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CodedCreate(IntPtr name, IntPtr description, int type, int subType, IntPtr values);

    private static CodedCreate? _coded;

    /// <summary>Adds the domains to the output and points each listed field at its domain.</summary>
    /// <param name="output">The GeoPackage file or the <c>.gdb</c> folder.</param>
    /// <param name="layer">The layer in it.</param>
    /// <param name="domains">The host's list: name, description, kind, codes or bounds, and the fields that use it.</param>
    /// <returns>The domains written, by name.</returns>
    public static List<string> Apply(string output, string layer, JsonElement domains)
    {
        List<string> written = [];

        using Dataset dataset = Gdal.OpenEx(output, (uint)(GdalConst.OF_VECTOR | GdalConst.OF_UPDATE), null, null, null)
            ?? throw new InvalidOperationException($"GDAL could not open '{output}' to add its domains.");
        Layer target = dataset.GetLayerByName(layer)
            ?? throw new InvalidOperationException($"'{output}' has no layer '{layer}' to give domains to.");

        foreach (JsonElement domain in domains.EnumerateArray())
        {
            string name = domain.GetProperty("name").GetString()!;
            string description = domain.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? string.Empty : string.Empty;
            List<string> fields = [.. domain.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!)];
            FeatureDefn defn = target.GetLayerDefn();
            List<int> indexes = [.. fields.Select(f => defn.GetFieldIndex(f)).Where(i => i >= 0)];

            if (indexes.Count == 0)
            {
                continue;
            }

            FieldType type = defn.GetFieldDefn(indexes[0]).GetFieldType();

            if (dataset.GetFieldDomain(name) is null)
            {
                using FieldDomain made = domain.GetProperty("kind").GetString() == "range"
                    ? Ogr.CreateRangeFieldDomain(
                        name, description, type, FieldSubType.OFSTNone,
                        Number(domain, "min"), true, Number(domain, "max"), true)
                    : Coded(name, description, type, [.. domain.GetProperty("codes").EnumerateArray()
                        .Select(c => (c.GetProperty("code").GetString()!, c.GetProperty("name").GetString() ?? string.Empty))]);

                if (!dataset.AddFieldDomain(made))
                {
                    throw new InvalidOperationException($"GDAL would not add the domain '{name}' to '{output}'.");
                }
            }

            foreach (int index in indexes)
            {
                FieldDefn current = defn.GetFieldDefn(index);
                using FieldDefn pointed = new(current.GetName(), current.GetFieldType());
                pointed.SetDomainName(name);

                if (target.AlterFieldDefn(index, pointed, AlterDomainFlag) != 0)
                {
                    throw new InvalidOperationException($"GDAL would not give '{current.GetName()}' the domain '{name}'.");
                }
            }

            written.Add(name);
        }

        return written;
    }

    private static double Number(JsonElement domain, string member) =>
        double.Parse(domain.GetProperty(member).GetString()!, CultureInfo.InvariantCulture);

    /// <summary>A coded-value domain, through <c>OGR_CodedFldDomain_Create</c>.</summary>
    private static FieldDomain Coded(string name, string description, FieldType type, IReadOnlyList<(string Code, string Value)> codes)
    {
        CodedCreate create = _coded ??= Find();
        List<IntPtr> strings = [];
        IntPtr array = Marshal.AllocHGlobal(IntPtr.Size * 2 * (codes.Count + 1));

        try
        {
            IntPtr Utf8(string text)
            {
                IntPtr pointer = Marshal.StringToCoTaskMemUTF8(text);
                strings.Add(pointer);
                return pointer;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                Marshal.WriteIntPtr(array, IntPtr.Size * 2 * i, Utf8(codes[i].Code));
                Marshal.WriteIntPtr(array, (IntPtr.Size * 2 * i) + IntPtr.Size, Utf8(codes[i].Value));
            }

            // The array ends with a pair of nulls.
            Marshal.WriteIntPtr(array, IntPtr.Size * 2 * codes.Count, IntPtr.Zero);
            Marshal.WriteIntPtr(array, (IntPtr.Size * 2 * codes.Count) + IntPtr.Size, IntPtr.Zero);

            IntPtr handle = create(Utf8(name), Utf8(description), (int)type, (int)FieldSubType.OFSTNone, array);

            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"GDAL would not make the coded-value domain '{name}'.");
            }

            return new FieldDomain(handle, true, null);
        }
        finally
        {
            foreach (IntPtr pointer in strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }

            Marshal.FreeHGlobal(array);
        }
    }

    /// <summary>GDAL's C function, from the library already loaded beside this program.</summary>
    private static CodedCreate Find()
    {
        IEnumerable<string> candidates = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*gdal*", SearchOption.AllDirectories)
            .Where(path =>
            {
                string file = Path.GetFileName(path);
                return file.Equals("gdal.dll", StringComparison.OrdinalIgnoreCase)
                    || file.StartsWith("libgdal.so", StringComparison.Ordinal)
                    || file.StartsWith("libgdal.", StringComparison.Ordinal) && file.EndsWith(".dylib", StringComparison.Ordinal);
            })
            .Where(path => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() is { } arch
                && (!path.Contains("runtimes", StringComparison.OrdinalIgnoreCase) || path.Contains(arch, StringComparison.OrdinalIgnoreCase)));

        foreach (string path in candidates)
        {
            if (NativeLibrary.TryLoad(path, out IntPtr library)
                && NativeLibrary.TryGetExport(library, "OGR_CodedFldDomain_Create", out IntPtr function))
            {
                return Marshal.GetDelegateForFunctionPointer<CodedCreate>(function);
            }
        }

        throw new InvalidOperationException("GDAL's OGR_CodedFldDomain_Create was not found beside this reader.");
    }
}
