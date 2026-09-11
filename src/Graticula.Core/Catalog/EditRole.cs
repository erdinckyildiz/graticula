using System;
using System.Collections.Generic;

namespace Graticula.Catalog;

/// <summary>
/// What a column records about the edits made to its row — ADR-064.
/// </summary>
/// <remarks>
/// <para>
/// <b>A role on a column, carried by the layer's field overrides</b>, because Q-58c put the
/// values of the rest of the FeatureServer data model with ADR-063's machinery rather than
/// beside it: an alias, a hidden flag and <em>this column records who created the row</em> are
/// all claims about one column, and two places making them is how two places come to disagree.
/// </para>
/// <para>
/// <b>The four ArcGIS names.</b> A layer document's <c>editFieldsInfo</c> names a
/// <c>creatorField</c>, <c>creationDateField</c>, <c>editorField</c> and <c>editDateField</c>;
/// these are those four, and nothing else.
/// </para>
/// </remarks>
public enum EditRole
{
    /// <summary>The column records nothing about edits.</summary>
    None,

    /// <summary>Who created the row. A text column. Its presence is what makes a layer tracked.</summary>
    Creator,

    /// <summary>When the row was created. A date column.</summary>
    Created,

    /// <summary>Who last changed the row. A text column.</summary>
    Editor,

    /// <summary>When the row was last changed. A date column.</summary>
    Edited,
}

/// <summary>
/// Which of a layer's columns hold each edit role — ADR-064.
/// </summary>
/// <param name="Creator">The column naming who created a row, or null.</param>
/// <param name="Created">The column holding when, or null.</param>
/// <param name="Editor">The column naming who last changed a row, or null.</param>
/// <param name="Edited">The column holding when, or null.</param>
/// <remarks>
/// <para>
/// <b>Tracked means a creator column</b>, because the creator is what ownership reads: a layer
/// that records only when rows changed can fill those columns and still cannot say whose a row
/// is, so <c>features:edit</c> keeps D-20's narrow rule on it.
/// </para>
/// <para>
/// <b>Derived from the overrides every time rather than stored beside them</b>, so there is no
/// second copy to fall out of step with the Fields page that writes them.
/// </para>
/// </remarks>
public sealed record EditorTracking(string? Creator, string? Created, string? Editor, string? Edited)
{
    /// <summary>A layer that records nothing about edits.</summary>
    public static EditorTracking None { get; } = new(null, null, null, null);

    /// <summary>Whether the layer records who created each row, which is what ownership needs.</summary>
    public bool IsOn => Creator is not null;

    /// <summary>Whether any column is maintained by this server rather than by the client.</summary>
    public bool Any => Creator is not null || Created is not null || Editor is not null || Edited is not null;

    /// <summary>The role a column holds, or <see cref="EditRole.None"/>.</summary>
    /// <param name="column">A column name.</param>
    /// <returns>Its role.</returns>
    public EditRole RoleOf(string column) =>
        string.Equals(column, Creator, StringComparison.Ordinal) ? EditRole.Creator
        : string.Equals(column, Created, StringComparison.Ordinal) ? EditRole.Created
        : string.Equals(column, Editor, StringComparison.Ordinal) ? EditRole.Editor
        : string.Equals(column, Edited, StringComparison.Ordinal) ? EditRole.Edited
        : EditRole.None;

    /// <summary>Reads the roles out of a layer's field overrides.</summary>
    /// <param name="overrides">The overrides.</param>
    /// <returns>The tracking they describe; <see cref="None"/> when they describe none.</returns>
    /// <remarks>
    /// <b>The first column to claim a role keeps it.</b> The admin surface refuses two columns
    /// with one role, so a second claim can only come from a hand edit in the database, and
    /// taking the first is the answer that does not depend on which one a reader met last.
    /// </remarks>
    public static EditorTracking From(IEnumerable<FieldOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        string? creator = null, created = null, editor = null, edited = null;

        foreach (FieldOverride said in overrides)
        {
            switch (said.Tracks)
            {
                case EditRole.Creator:
                    creator ??= said.Column;
                    break;
                case EditRole.Created:
                    created ??= said.Column;
                    break;
                case EditRole.Editor:
                    editor ??= said.Column;
                    break;
                case EditRole.Edited:
                    edited ??= said.Column;
                    break;
                default:
                    break;
            }
        }

        return creator is null && created is null && editor is null && edited is null
            ? None
            : new EditorTracking(creator, created, editor, edited);
    }
}
