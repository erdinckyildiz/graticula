using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The worker's side of a lease: renewed beside the work, and the work ended when it is lost — D-243.
/// </summary>
/// <remarks>
/// <b>The distinction these pin is the one ADR-011 §3.9 depends on.</b> A renewal that throws is the
/// store being briefly away, and the lease outlasts three of those; a renewal that *answers* and says
/// the job is not this worker's any more means somebody took it back, and the worker must stop before
/// it writes over whoever holds it now. Getting those two the wrong way round is either a worker that
/// abandons good work during a blip or one that keeps writing after it was reclaimed.
/// </remarks>
public sealed class JobLeaseKeeperTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task A_renewal_that_says_not_yours_ends_the_work()
    {
        Store store = new(call => call < 3 ? true : false);

        await using JobLeaseKeeper lease = JobLeaseKeeper.Hold(
            store, Guid.NewGuid(), "graticula/test", NullLogger.Instance, CancellationToken.None, Quick);

        await WithinAsync(() => lease.Working.IsCancellationRequested);

        Assert.True(lease.Lost);
        Assert.Equal(3, store.Renewals);
    }

    [Fact]
    public async Task A_renewal_that_fails_is_not_a_lost_lease()
    {
        Store store = new(_ => throw new InvalidOperationException("the platform store is away"));

        await using JobLeaseKeeper lease = JobLeaseKeeper.Hold(
            store, Guid.NewGuid(), "graticula/test", NullLogger.Instance, CancellationToken.None, Quick);

        await WithinAsync(() => store.Renewals >= 5);

        Assert.False(lease.Working.IsCancellationRequested);
        Assert.False(lease.Lost);
    }

    [Fact]
    public async Task The_server_stopping_ends_the_work_without_calling_the_lease_lost()
    {
        Store store = new(_ => true);
        using CancellationTokenSource stopping = new();

        await using JobLeaseKeeper lease = JobLeaseKeeper.Hold(
            store, Guid.NewGuid(), "graticula/test", NullLogger.Instance, stopping.Token, Quick);

        await stopping.CancelAsync();

        Assert.True(lease.Working.IsCancellationRequested);
        Assert.False(lease.Lost);
    }

    [Fact]
    public async Task Once_the_work_is_over_nothing_renews()
    {
        Store store = new(_ => true);

        JobLeaseKeeper lease = JobLeaseKeeper.Hold(
            store, Guid.NewGuid(), "graticula/test", NullLogger.Instance, CancellationToken.None, Quick);

        await WithinAsync(() => store.Renewals >= 2);
        await lease.DisposeAsync();

        int after = store.Renewals;
        await Task.Delay(Quick * 5);

        Assert.Equal(after, store.Renewals);
    }

    private static async Task WithinAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(5);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was still false after five seconds.");
            await Task.Delay(5);
        }
    }

    /// <summary>A job store whose renewals answer as the test says, and which does nothing else.</summary>
    private sealed class Store : IJobStore
    {
        private readonly Func<int, bool> _answer;
        private int _renewals;

        public Store(Func<int, bool> answer) => _answer = answer;

        public int Renewals => Volatile.Read(ref _renewals);

        public Task<bool> RenewAsync(Guid id, string worker, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _renewals);

            return Task.FromResult(_answer(call));
        }

        public Task<JobRecord> CreateAsync(
            Guid owner, JobKind kind, string? subject, string? detail,
            CancellationToken cancellationToken, int protocol = 1) =>
            throw new NotSupportedException();

        public Task<JobRecord?> FindAsync(
            Guid id, Guid asking, bool administrator, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JobRecord>> ListAsync(
            Guid asking, bool all, bool unfinishedOnly, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> StartAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobRecord?> ClaimAsync(
            JobKind kind, string worker, int speaks, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ProgressAsync(Guid id, int percent, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FinishAsync(
            Guid id, JobStatus status, string? detail, string? failure,
            CancellationToken cancellationToken, string? worker = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JobReclaim>> ReclaimAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
