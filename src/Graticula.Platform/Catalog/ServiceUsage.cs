using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Catalog;

/// <summary>How much one service has been used — ADR-135.</summary>
/// <param name="ServiceId">The service.</param>
/// <param name="Total">Requests it has answered since it was first counted.</param>
/// <param name="Last30">Requests in the last thirty days, today included.</param>
/// <param name="Last7">Requests in the last seven days, today included.</param>
/// <param name="LastDay">The last day it answered a request, or null when it never has.</param>
public sealed record ServiceUse(Guid ServiceId, long Total, long Last30, long Last7, DateOnly? LastDay);

/// <summary>One service's requests counted in memory, waiting to be written.</summary>
/// <param name="Folder">Its folder, or null at the root.</param>
/// <param name="Name">Its name.</param>
/// <param name="Requests">How many requests.</param>
public readonly record struct ServiceCount(string? Folder, string Name, long Requests);

/// <summary>
/// Where services' request counts are kept, a row a service a day — ADR-135. Written in batches by the host, never on
/// a request's path.
/// </summary>
public interface IServiceUsageStore
{
    /// <summary>Adds counts to a day's rows; a service no longer published is skipped.</summary>
    /// <param name="counts">The counts, one a service.</param>
    /// <param name="day">The day they belong to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task AddAsync(IReadOnlyCollection<ServiceCount> counts, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Every counted service's use, by service id.</summary>
    /// <param name="today">The day the last thirty and seven days end on.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The uses.</returns>
    Task<IReadOnlyDictionary<Guid, ServiceUse>> ReadAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>The first day anything was counted, or null before then — what "no requests" is measured from.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The day, or null.</returns>
    Task<DateOnly?> FirstDayAsync(CancellationToken cancellationToken);
}
