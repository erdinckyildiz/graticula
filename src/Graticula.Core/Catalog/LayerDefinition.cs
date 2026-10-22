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
    /// <param name="geometryColumn">
    /// The geometry column, or <see langword="null"/> for a table with none — ADR-213.
    /// </param>
    /// <param name="srid">
    /// The column's spatial reference identifier. A table has no column to declare one and carries 4326, which
    /// nothing reads (ADR-213 §5c).
    /// </param>
    /// <param name="identityColumn">
    /// The declared identity column (Q-57). Never inferred, never synthesised.
    /// </param>
    /// <param name="integerIdentityColumn">
    /// A column holding a unique integer per row, or <see langword="null"/> when the table
    /// has none — in which case a single feature cannot be named by a number and the faces
    /// that require one refuse it.
    /// </param>
    /// <param name="isHosted">
    /// Whether the layer's <em>source</em> is the datastore. <b>Not whether we made the
    /// table</b> — see <see cref="IsHosted"/>, which says why the difference matters.
    /// </param>
    public LayerDefinition(
        string name,
        string schemaName,
        string tableName,
        string? geometryColumn,
        int srid,
        string identityColumn,
        string? integerIdentityColumn,
        bool isHosted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(srid, 1);

        Name = name;
        SchemaName = RequireIdentifier(schemaName, nameof(schemaName));
        TableName = RequireIdentifier(tableName, nameof(tableName));
        GeometryColumn = geometryColumn is null ? null : RequireIdentifier(geometryColumn, nameof(geometryColumn));
        IdentityColumn = RequireIdentifier(identityColumn, nameof(identityColumn));
        IntegerIdentityColumn = integerIdentityColumn is null
            ? null
            : RequireIdentifier(integerIdentityColumn, nameof(integerIdentityColumn));
        Srid = srid;
        IsHosted = isHosted;
    }

    /// <summary>The published name.</summary>
    public string Name { get; }

    /// <summary>The database schema.</summary>
    public string SchemaName { get; }

    /// <summary>The table.</summary>
    public string TableName { get; }

    /// <summary>The geometry column, or <see langword="null"/> for a table — ADR-213.</summary>
    /// <remarks>
    /// <b>Null rather than a name nobody reads</b>, so that every place that would put a geometry into SQL, a tile or a
    /// map has to say what it does for a table instead of being handed a column that is not there.
    /// </remarks>
    public string? GeometryColumn { get; }

    /// <summary>
    /// <see langword="true"/> when this is a table with no geometry — ArcGIS's standalone table, ADR-213.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(false, nameof(GeometryColumn))]
    public bool IsTable => GeometryColumn is null;

    /// <summary>
    /// The reference a table carries, which nothing reads — ADR-213 §5c. A table has no column to declare one, and the
    /// catalogue's <c>srid</c> stays <c>not null</c> rather than opening a third column to null for no answer.
    /// </summary>
    public const int TableSrid = 4326;

    /// <summary>The geometry column, for a reader that serves nothing else — a tile, a map, a provider with no tables.</summary>
    /// <exception cref="InvalidOperationException">This is a table.</exception>
    /// <remarks>
    /// <b>A method, so that nothing reflecting over this type's properties — a serialiser, a fingerprint — calls it on a
    /// table.</b> <b>For the places that cannot meet a table rather than the places that must handle one.</b> Only PostGIS publishes
    /// a table (ADR-213 §5), every other provider's registration requires a geometry column, and the drawing faces leave
    /// tables out of what they draw; a table reaching one of them is a defect, and this says which.
    /// </remarks>
    /// <returns>The geometry column.</returns>
    public string RequireGeometryColumn() => GeometryColumn ?? throw new InvalidOperationException(
        $"'{Name}' is a table and has no geometry column; it cannot be drawn or read by a source that needs one (ADR-213).");

    /// <summary>The column's spatial reference identifier.</summary>
    public int Srid { get; }

    /// <summary>The declared identity column (Q-57).</summary>
    public string IdentityColumn { get; }

    /// <summary>
    /// A column holding a unique integer per row, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>Called <c>ObjectIdColumn</c> until 2026-08-26 —
    /// [D-124](../../../docs/architecture-debt.md).</b> *Object ID* is Esri's word, and it was
    /// in the domain every face reads. Two faces need what it describes and neither needs the
    /// name: ArcGIS because its protocol requires a unique 32-bit identity
    /// ([ADR-013](../../../docs/adr/ADR-013-arcgis-compatibility.md) §2a), and OGC API
    /// Features Part 4 because a created feature must be given a <c>Location</c> and a
    /// replaced or deleted one must be nameable.
    /// <b>The wire keeps Esri's word where it is Esri's</b> — <c>objectIdFieldName</c> on the
    /// ArcGIS surface, and <c>objectIdColumn</c> on the admin API, which is a published
    /// contract this console and any script already speak. The mapping between the two is the
    /// adapter boundary D-124 asks for, and it is now visible as one.
    /// </remarks>
    public string? IntegerIdentityColumn { get; }

    /// <summary>
    /// <see langword="true"/> when the layer's <em>source</em> is the datastore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This said <i>we own the schema, so it can be changed</i> until 2026-09-09, and
    /// that is not what it means.</b> The value comes from the data source's
    /// <c>is_datastore</c> flag, and a datastore source can serve <em>any</em> schema of
    /// that database — the conformance fixture publishes one that does. So a layer can be
    /// hosted and sit in a schema this server did not create.
    /// </para>
    /// <para>
    /// <b>The distinction is load-bearing, and it has already been got wrong twice.</b>
    /// [ADR-058](../../../docs/adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md)
    /// §5h found the field endpoints letting such a layer through to an importer guard
    /// that throws — a 500 for a state the endpoint should have refused in a sentence —
    /// and the attachment path had the same hole until it was found the same way. <b>A
    /// caller asking <i>may I run DDL here?</i> must ask
    /// <c>HostedDataEndpoints.AlterableSchema</c>, which tests this <em>and</em> the
    /// schema name.</b> This property alone is not that question's answer, and the
    /// sentence that used to be here is why two call sites believed it was.
    /// </para>
    /// </remarks>
    public bool IsHosted { get; }

    /// <summary>
    /// <see langword="true"/> when a single row of this layer can be named by an integer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called <c>IsArcGisServable</c> until 2026-08-26, and the rename is
    /// [D-124](../../../docs/architecture-debt.md)'s own prescription becoming due.</b> That
    /// row records four of one protocol's nouns living in the domain every protocol reads,
    /// and sets the trigger precisely: *when a second protocol needs its own notion of
    /// servability — today only ArcGIS does, so a rename would move the coupling rather than
    /// remove it.* **It stopped being only ArcGIS on 2026-08-25**, when OGC API Features
    /// gained its write half ([Q-44](../../../docs/open-questions.md)) and began refusing an
    /// unaddressable collection by reading this same property.
    /// </para>
    /// <para>
    /// <b>What the property states now is the data's shape, not a face's opinion of it.</b>
    /// The rule *ArcGIS FeatureServer requires a unique 32-bit integer identity* is genuinely
    /// ArcGIS's ([ADR-013](../../../docs/adr/ADR-013-arcgis-compatibility.md) §2a) and lives
    /// on that face; OGC API Features Part 4 needs an addressable feature for a different
    /// reason — a created feature must be given a <c>Location</c>, and a replaced or deleted
    /// one must be nameable — and reaches the same requirement by its own road. Two faces
    /// asking one question about the data is right; two faces asking it in one face's
    /// vocabulary was the leak.
    /// </para>
    /// <para>
    /// <b>The capability report reads this rather than discovering it when a request
    /// fails</b> — never degrade silently, applied to a data-shape limitation rather than a
    /// provider capability.
    /// </para>
    /// <para>
    /// <b>The column beside it moved on the same day</b> — see
    /// <see cref="IntegerIdentityColumn"/>, which was <c>ObjectIdColumn</c>. The wire keeps
    /// Esri's word where it is Esri's; the domain no longer does.
    /// </para>
    /// </remarks>
    public bool HasIntegerIdentity => IntegerIdentityColumn is not null;

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
