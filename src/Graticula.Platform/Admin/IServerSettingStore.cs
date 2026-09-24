using System;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Admin;

/// <summary>
/// The settings an operator makes for the whole server from the console, by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration 54, for V-70 (ADR-084).</b> The page size is the first thing an operator sets for the whole
/// server rather than for one service, and the owner asked for it to be set from a screen rather than from a
/// configuration file. A named value per row is what lets the next one arrive without a migration.
/// </para>
/// <para>
/// <b>Text, and parsed by whoever owns the name.</b> The store does not know what a page size is, so it cannot
/// be the place that decides one is valid; <c>ServerPageSize</c> is.
/// </para>
/// </remarks>
public interface IServerSettingStore
{
    /// <summary>A setting as stored, or null when nobody has set it.</summary>
    /// <param name="name">The setting's name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The stored value, or null.</returns>
    Task<StoredSetting?> ReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Stores a setting, or removes it when <paramref name="value"/> is null.</summary>
    /// <param name="name">The setting's name.</param>
    /// <param name="value">The value, or null to go back to the server's own.</param>
    /// <param name="changedBy">Who changed it, for the record.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What it held before, or null.</returns>
    Task<StoredSetting?> WriteAsync(string name, string? value, Guid? changedBy, CancellationToken cancellationToken);
}

/// <summary>A stored setting.</summary>
/// <param name="Value">What it holds.</param>
/// <param name="ChangedAt">When it was last set.</param>
public sealed record StoredSetting(string Value, DateTimeOffset ChangedAt);
