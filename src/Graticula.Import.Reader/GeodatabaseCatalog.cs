using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using OSGeo.OGR;
using Dataset = OSGeo.GDAL.Dataset;
using Gdal = OSGeo.GDAL.Gdal;
using GdalConst = OSGeo.GDAL.GdalConst;

namespace Graticula.Import.Reader;

/// <summary>
/// A File Geodatabase's own catalogue: its domains and each table's subtypes — ADR-065.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the XML the geodatabase keeps, because the binding cannot hand over a coded value.</b>
/// GDAL reads a geodatabase's field domains, and its C# binding exposes a range's bounds — but not the
/// list of codes of a coded-value domain, which is the kind that matters (measured 2026-09-12 against
/// MaxRev.Gdal.Core 3.13.1: <c>GetMinAsDouble</c> is bound, <c>GetEnumeration</c> is not). Subtypes are
/// not in GDAL's API at all. Both are in the <c>GDB_Items</c> system table, which the OpenFileGDB driver
/// lists when asked with <c>LIST_ALL_TABLES=YES</c>: each item's <c>Definition</c> is an XML document in
/// the geodatabase XML schema Esri publishes — <c>GPCodedValueDomain2</c> and <c>GPRangeDomain2</c> for
/// domains, and for a feature class the <c>SubtypeFieldName</c>, <c>DefaultSubtypeCode</c> and array of
/// <c>Subtype</c> elements with their <c>SubtypeFieldInfo</c> that the published *XML schema of the
/// geodatabase* describes.
/// </para>
/// <para>
/// <b>Nothing here is required for an import to work.</b> An archive whose catalogue cannot be read is
/// imported exactly as before — labels, rows and geometry — and its domains and subtypes are reported as
/// not carried. A layer is never lost for the sake of its drop-downs.
/// </para>
/// </remarks>
internal sealed record GeodatabaseCatalog
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private readonly Dictionary<string, XElement> _domains;
    private readonly Dictionary<string, XElement> _tables;

    private GeodatabaseCatalog(Dictionary<string, XElement> domains, Dictionary<string, XElement> tables)
    {
        _domains = domains;
        _tables = tables;
    }

    /// <summary>An archive with nothing in its catalogue this reader can use.</summary>
    public static GeodatabaseCatalog Empty { get; } =
        new(new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase));

    /// <summary>What went wrong reading the catalogue, when something did.</summary>
    public string? Unread { get; private init; }

    /// <summary>
    /// Reads the catalogue of a geodatabase, or answers empty for anything that is not one.
    /// </summary>
    /// <param name="path">The dataset path GDAL opens, already through <c>/vsizip/</c>.</param>
    /// <param name="driver">The short name of the driver that opened the archive for its features.</param>
    /// <returns>The catalogue.</returns>
    public static GeodatabaseCatalog Read(string path, string driver)
    {
        // A shapefile has no catalogue, and asking its driver for system tables is asking nothing.
        if (!string.Equals(driver, "OpenFileGDB", StringComparison.Ordinal))
        {
            return Empty;
        }

        try
        {
            using Dataset? source = Gdal.OpenEx(
                path, (uint)GdalConst.OF_VECTOR, ["OpenFileGDB"], ["LIST_ALL_TABLES=YES"], null);

            using Layer? items = source?.GetLayerByName("GDB_Items");

            if (items is null)
            {
                return Empty with { Unread = "the archive has no GDB_Items table to read domains and subtypes from." };
            }

            Dictionary<string, XElement> domains = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, XElement> tables = new(StringComparer.OrdinalIgnoreCase);

            items.ResetReading();

            for (Feature? item = items.GetNextFeature(); item is not null; item = items.GetNextFeature())
            {
                using (item)
                {
                    int at = item.GetFieldIndex("Definition");

                    if (at < 0 || !item.IsFieldSetAndNotNull(at))
                    {
                        continue;
                    }

                    XElement root;

                    try
                    {
                        root = XDocument.Parse(item.GetFieldAsString(at)).Root!;
                    }
                    catch (XmlException)
                    {
                        // One unreadable item costs that item, not the catalogue.
                        continue;
                    }

                    switch (root.Name.LocalName)
                    {
                        case "GPCodedValueDomain2" or "GPRangeDomain2":
                            if (Text(root, "DomainName") is { Length: > 0 } domain)
                            {
                                domains[domain] = root;
                            }

                            break;

                        case "DEFeatureClassInfo" or "DETableInfo":
                            if (Text(root, "Name") is { Length: > 0 } table)
                            {
                                tables[table] = root;
                            }

                            break;

                        default:
                            break;
                    }
                }
            }

            return new GeodatabaseCatalog(domains, tables);
        }
        catch (Exception e) when (e is ApplicationException or InvalidOperationException or ArgumentException)
        {
            return Empty with { Unread = $"the geodatabase's catalogue could not be read: {e.Message}" };
        }
    }

    /// <summary>
    /// A domain as the ArcGIS REST API's domain object, or null when the catalogue has no such domain.
    /// </summary>
    /// <param name="name">The domain's name, as a field names it.</param>
    /// <returns>The object, ready to be written as JSON.</returns>
    public Dictionary<string, object?>? Domain(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_domains.TryGetValue(name, out XElement? root))
        {
            return null;
        }

        string domainName = Text(root, "DomainName") ?? name;

        if (root.Name.LocalName == "GPRangeDomain2")
        {
            return Value(Child(root, "MinValue")) is { } min
                && Value(Child(root, "MaxValue")) is { } max
                    ? new Dictionary<string, object?>
                    {
                        ["type"] = "range",
                        ["name"] = domainName,
                        ["range"] = new[] { min, max },
                    }
                    : null;
        }

        List<Dictionary<string, object?>> codes = [];

        foreach (XElement coded in Child(root, "CodedValues")?.Elements().Where(e => e.Name.LocalName == "CodedValue") ?? [])
        {
            if (Value(Child(coded, "Code")) is { } code)
            {
                codes.Add(new Dictionary<string, object?> { ["name"] = Text(coded, "Name") ?? string.Empty, ["code"] = code });
            }
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "codedValue",
            ["name"] = domainName,
            ["codedValues"] = codes,
        };
    }

    /// <summary>
    /// A table's subtypes, in the shape the server's admin surface takes, or null when it has none.
    /// </summary>
    /// <param name="table">The table's name, as GDAL names its layer.</param>
    /// <param name="ownDomains">Each field's own domain name, so a subtype repeating it is not a replacement.</param>
    /// <returns>
    /// <c>{field, defaultCode, types: [{code, name, defaultValues, domains}]}</c> with source field names;
    /// the importer maps them to the columns it made.
    /// </returns>
    public Dictionary<string, object?>? Subtypes(string table, IReadOnlyDictionary<string, string?> ownDomains)
    {
        if (!_tables.TryGetValue(table, out XElement? root)
            || Text(root, "SubtypeFieldName") is not { Length: > 0 } field
            || Child(root, "Subtypes") is not { } declared)
        {
            return null;
        }

        List<Dictionary<string, object?>> types = [];

        foreach (XElement subtype in declared.Elements().Where(e => e.Name.LocalName == "Subtype"))
        {
            if (!long.TryParse(Text(subtype, "SubtypeCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
            {
                continue;
            }

            Dictionary<string, object?> defaults = [];
            Dictionary<string, object?> domains = [];

            foreach (XElement info in Child(subtype, "FieldInfos")?.Elements().Where(e => e.Name.LocalName == "SubtypeFieldInfo") ?? [])
            {
                if (Text(info, "FieldName") is not { Length: > 0 } name)
                {
                    continue;
                }

                if (Value(Child(info, "DefaultValue")) is { } value)
                {
                    defaults[name] = value;
                }

                // <b>A subtype naming its field's own domain replaces nothing</b>, and one naming none
                // is not expressible on the wire this carries to — so only a different, readable domain
                // is a replacement.
                string? given = Text(info, "DomainName");
                ownDomains.TryGetValue(name, out string? own);

                if (!string.IsNullOrWhiteSpace(given)
                    && !string.Equals(given, own, StringComparison.OrdinalIgnoreCase)
                    && Domain(given) is { } replaced)
                {
                    domains[name] = replaced;
                }
            }

            types.Add(new Dictionary<string, object?>
            {
                ["code"] = code,
                ["name"] = Text(subtype, "SubtypeName") ?? code.ToString(CultureInfo.InvariantCulture),
                ["defaultValues"] = defaults,
                ["domains"] = domains,
            });
        }

        if (types.Count == 0)
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            ["field"] = field,
            ["defaultCode"] = long.TryParse(Text(root, "DefaultSubtypeCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long first)
                ? first
                : types[0]["code"],
            ["types"] = types,
        };
    }

    /// <summary>The first child with this local name, whatever namespace the document put it in.</summary>
    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Text(XElement parent, string name) =>
        Child(parent, name)?.Value.Trim();

    /// <summary>
    /// A typed XML value — <c>xsi:type</c> says which — as a JSON number, a string, or a date in epoch
    /// milliseconds.
    /// </summary>
    private static object? Value(XElement? element)
    {
        if (element is null || element.Attribute(Xsi + "nil")?.Value == "true")
        {
            return null;
        }

        string type = element.Attribute(Xsi + "type")?.Value ?? string.Empty;
        string text = element.Value;

        // The prefix is whatever the document bound the XML Schema namespace to; the local part decides.
        string local = type.Contains(':', StringComparison.Ordinal) ? type[(type.IndexOf(':', StringComparison.Ordinal) + 1)..] : type;

        switch (local)
        {
            case "short" or "int" or "long" or "double" or "float" or "decimal" or "unsignedByte" or "byte":
                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
                    ? number
                    : null;

            case "dateTime":
                return DateTimeOffset.TryParse(
                        text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset at)
                    ? at.ToUnixTimeMilliseconds()
                    : null;

            default:
                return text;
        }
    }
}
