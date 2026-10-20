namespace Graticula.Platform.Catalog;

/// <summary>Where a registered layer keeps its attachments — ADR-207.</summary>
public static class AttachmentPlaces
{
    /// <summary>Beside the layer's table, in the database it is registered from.</summary>
    public const string Source = "source";

    /// <summary>In Graticula's datastore, in tables named for the layer.</summary>
    public const string Datastore = "datastore";

    /// <summary>Whether a word names a place.</summary>
    /// <param name="place">The word.</param>
    /// <returns>True for <see cref="Source"/> and <see cref="Datastore"/>.</returns>
    public static bool Known(string? place) => place is Source or Datastore;
}
