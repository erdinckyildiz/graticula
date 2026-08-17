using System;
using System.Text.RegularExpressions;

namespace Graticula.Catalog;

/// <summary>
/// Everything a provider needs to read one published layer.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the <c>layer</c> row in the platform store, minus the bookkeeping.
/// It is a domain type rather than a database row so that a provider does not
/// depend on the catalogue's storage.
/// </para>
/// <para>
/// <b>Identifiers are validated here, once, at construction.</b> Schema, table
/// and column names cannot be bound as SQL parameters — they are identifiers,
/// not values — so every provider has to interpolate them. Validating at the
/// boundary means the interpolation downstream is provably safe rather than
/// carefully written. ADR-008 §4.6 owns injection generally; this is the part
/// parameters cannot cover.
/// </para>
/// </remarks>
public sealed partial class LayerDefinition
{
    /// <summary>Creates a layer definition.</summary>
    /// <param name="name">The published name.</param>
    /// <param name="schemaName">The database schema holding the table.</param>
    /// <param name="tableName">The table.</param>
    /// <param name="geometryColumn">The geometry column.</param>
    /// <param name="srid">The column's spatial reference identifier.</param>
    /// <param name="identityColumn">
    /// The declared identity column (Q-57). Never inferred, never synthesised.
    /// </param>
    /// <param name="objectIdColumn">
    /// A unique integer column for ArcGIS <c>objectId</c>, or
    /// <see langword="null"/> when the table has none — in which case the layer
    /// is servable natively and <b>not</b> through the ArcGIS surface
    /// (ADR-013 §2a).
    /// </param>
    /// <param name="isHosted">Whether we own the schema.</param>
    public LayerDefinition(
        string name,
        string schemaName,
        string tableName,
        string geometryColumn,
        int srid,
        string identityColumn,
        string? objectIdColumn,
        bool isHosted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(srid, 1);

        Name = name;
        SchemaName = RequireIdentifier(schemaName, nameof(schemaName));
        TableName = RequireIdentifier(tableName, nameof(tableName));
        GeometryColumn = RequireIdentifier(geometryColumn, nameof(geometryColumn));
        IdentityColumn = RequireIdentifier(identityColumn, nameof(identityColumn));
        ObjectIdColumn = objectIdColumn is null
            ? null
            : RequireIdentifier(objectIdColumn, nameof(objectIdColumn));
        Srid = srid;
        IsHosted = isHosted;
    }

    /// <summary>The published name.</summary>
    public string Name { get; }

    /// <summary>The database schema.</summary>
    public string SchemaName { get; }

    /// <summary>The table.</summary>
    public string TableName { get; }

    /// <summary>The geometry column.</summary>
    public string GeometryColumn { get; }

    /// <summary>The column's spatial reference identifier.</summary>
    public int Srid { get; }

    /// <summary>The declared identity column (Q-57).</summary>
    public string IdentityColumn { get; }

    /// <summary>
    /// A unique integer column for ArcGIS, or <see langword="null"/>.
    /// </summary>
    public string? ObjectIdColumn { get; }

    /// <summary>
    /// <see langword="true"/> when we own the schema, so it can be changed;
    /// <see langword="false"/> for a registered source, where we cannot.
    /// </summary>
    public bool IsHosted { get; }

    /// <summary>
    /// <see langword="true"/> when this layer can be served through the ArcGIS
    /// surface, which requires a unique integer identity (ADR-013 §2a).
    /// </summary>
    /// <remarks>
    /// The capability report reads this rather than discovering it when a
    /// request fails — never degrade silently, applied to a data-shape
    /// limitation rather than a provider capability.
    /// </remarks>
    public bool IsArcGisServable => ObjectIdColumn is not null;

    /// <summary>
    /// Quotes an identifier for PostgreSQL, doubling any embedded quote.
    /// </summary>
    /// <remarks>
    /// Belt and braces. Everything reaching here has already passed
    /// <see cref="RequireIdentifier"/>, which rejects the characters that would
    /// make quoting necessary — but the two together mean a future caller who
    /// forgets the first still cannot inject through the second.
    /// </remarks>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    /// <summary>The quoted, schema-qualified table.</summary>
    public string QuotedTable => $"{Quote(SchemaName)}.{Quote(TableName)}";

    private static string RequireIdentifier(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);

        if (value.Length > 63)
        {
            // PostgreSQL truncates at 63 bytes, so a longer name would silently
            // become a different identifier than the one recorded.
            throw new ArgumentException(
                $"'{value}' is {value.Length} characters; PostgreSQL identifiers truncate at 63, "
                + "so this would refer to something other than what was registered.", parameter);
        }

        if (!IdentifierPattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a plain identifier. Schema, table and column names cannot be "
                + "bound as parameters, so they are interpolated — and only unquoted, "
                + "ASCII-alphanumeric names are accepted to make that provably safe rather than "
                + "carefully written.", parameter);
        }

        return value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
