using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary><see cref="IRequestLog"/> over the platform store, written from behind a queue.</summary>
/// <remarks>
/// <para>
/// <b>The opposite decision from <see cref="PostgresAuditLog"/>, and the difference is the
/// point.</b> A failed audit write fails the request, because an administrative action that
/// cannot be recorded has not been authorised to happen. A failed *request log* write must
/// do nothing at all: the request has already been served, and there are thousands of these
/// for every one of those.
/// [ADR-045](../../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md)
/// §3 is where that asymmetry is argued.
/// </para>
/// <para>
/// <b>Bounded, and the bound is a number with a reason.</b> 4,096 entries: about ten
/// seconds of a busy server at the stated scale, which is long enough to ride out a flush
/// that is waiting on a slow store and short enough that the memory is a rounding error
/// beside one 4096² canvas. Full means drop, not wait — <see cref="Dropped"/> counts it and
/// the Logs screen shows the count, because a log that quietly stops recording is worse
/// than one that says it stopped.
/// </para>
/// <para>
/// <b>Batched with a binary import rather than an insert per row.</b> One round trip for up
/// to a batch of rows, which is what keeps the flusher from becoming the thing that fills
/// the queue.
/// </para>
/// </remarks>
public sealed class PostgresRequestLog : IRequestLog, IAsyncDisposable
{
    /// <summary>How many entries may wait to be written.</summary>
    /// <remarks>
    /// <b>Ten seconds of a busy server, and deliberately not more.</b> A deeper queue does
    /// not save more log lines under sustained load — it only delays the moment the drop
    /// starts and makes the eventual burst of writes larger.
    /// </remarks>
    public const int Capacity = 4096;

    /// <summary>Rows written in one round trip.</summary>
    public const int Batch = 256;

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    private readonly ConcurrentQueue<RequestEntry> _queue = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly NpgsqlDataSource _dataSource;
    private readonly Task _flusher;

    private long _queued;
    private long _dropped;

    /// <summary>Creates the log and starts its flusher.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    public PostgresRequestLog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
        _flusher = Task.Run(() => FlushForeverAsync(_stopping.Token));
    }

    /// <inheritdoc/>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>How many entries are waiting to be written.</summary>
    public long Waiting => Interlocked.Read(ref _queued);

    /// <inheritdoc/>
    public void Record(RequestEntry entry)
    {
        // <b>The check and the increment are not atomic together, and that is fine.</b>
        // Racing threads can push the queue a little over capacity; the consequence is a
        // few extra rows, and the alternative is a lock on the hot path to protect a
        // number whose exactness nobody reads.
        if (Interlocked.Read(ref _queued) >= Capacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(entry);
        Interlocked.Increment(ref _queued);

        // Release rather than Wait: this is the producer, and it must not be the thing
        // that discovers the store is slow.
        _work.Release();
    }

    /// <summary>Stops the flusher and writes what is still queued.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <b>One last flush on the way out.</b> A shutdown is exactly when the last few
    /// requests are the interesting ones.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _flusher.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation is how it is asked to stop.
        }

        try
        {
            await WriteBatchAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The store is going away too. Nothing to do and nowhere to say it.
        }

        _stopping.Dispose();
        _work.Dispose();
    }

    private async Task FlushForeverAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wake on work, and also on a timer, so a handful of entries that never
                // reach a full batch are still written within a couple of seconds.
                await _work.WaitAsync(Idle, cancellationToken).ConfigureAwait(false);

                await WriteBatchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (NpgsqlException)
            {
                // <b>Swallowed, and this is the one place in this repository where that is
                // the right thing.</b> The store being unreachable is already reported by
                // every other path that touches it; a request log that retried loudly
                // would turn one outage into a second, noisier one, and the requests it
                // describes have already been answered.
                await Task.Delay(Idle, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteBatchAsync(CancellationToken cancellationToken)
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
                """
                copy request_log
                  (method, path, query, status, duration_ms, principal_name,
                   source_address, face, service, bytes)
                from stdin (format binary)
                """,
                cancellationToken)
            .ConfigureAwait(false);

        int written = 0;

        while (written < Batch && _queue.TryDequeue(out RequestEntry entry))
        {
            Interlocked.Decrement(ref _queued);
            written++;

            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(entry.Method, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await writer.WriteAsync(entry.Path, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(writer, entry.Query, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(entry.Status, NpgsqlDbType.Integer, cancellationToken)
                .ConfigureAwait(false);
            await writer.WriteAsync(entry.DurationMs, NpgsqlDbType.Integer, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(writer, entry.PrincipalName, cancellationToken)
                .ConfigureAwait(false);
            await WriteAddressAsync(writer, entry.SourceAddress, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(writer, entry.Face, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(writer, entry.Service, cancellationToken).ConfigureAwait(false);

            if (entry.Bytes is { } bytes)
            {
                await writer.WriteAsync(bytes, NpgsqlDbType.Bigint, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAddressAsync(
        NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        // <b>An unparseable address is written as null rather than dropping the row.</b>
        // What the row is for is the request; the address is one of its fields, and losing
        // the whole entry because a proxy sent something odd would be the wrong trade.
        if (value is null || !IPAddress.TryParse(value, out IPAddress? address))
        {
            await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(address, NpgsqlDbType.Inet, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary><see cref="IClientEventLog"/> over the platform store.</summary>
/// <remarks>
/// <b>Written straight through, not queued, and that is because of where it comes
/// from.</b> These arrive one at a time from a browser at human rate, not at request rate,
/// so there is nothing to batch — and the endpoint that feeds it is rate limited, which is
/// the bound that matters here. Failing the write fails that request, which is correct: the
/// caller asked for one thing and it did not happen.
/// </remarks>
public sealed class PostgresClientEventLog : IClientEventLog
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the log.</summary>
    /// <param name="dataSource">The platform store pool.</param>
    public PostgresClientEventLog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task RecordAsync(ClientEntry entry, CancellationToken cancellationToken)
    {
        const string Sql = """
            insert into client_event
              (kind, page, message, detail, principal_name, source_address, agent)
            values (@kind, @page, @message, @detail::jsonb, @name, @address, @agent)
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);

        // <b>Truncated here as well as constrained in the schema.</b> The check constraints
        // exist so that no future writer can exceed them; clipping here means an
        // over-length message is recorded shortened rather than refused, because the first
        // 2,000 characters of a stack trace is the useful part and losing the event
        // entirely would be the worse outcome.
        command.Parameters.AddWithValue("kind", Clip(entry.Kind, 64));
        command.Parameters.AddWithValue("message", Clip(entry.Message, 2000));
        command.Parameters.AddWithValue("detail", entry.Detail);

        command.Parameters.Add(new NpgsqlParameter("page", NpgsqlDbType.Text)
        {
            Value = entry.Page is null ? DBNull.Value : Clip(entry.Page, 2000),
        });

        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text)
        {
            Value = entry.PrincipalName is null ? DBNull.Value : entry.PrincipalName,
        });

        command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text)
        {
            Value = entry.Agent is null ? DBNull.Value : Clip(entry.Agent, 512),
        });

        command.Parameters.Add(new NpgsqlParameter("address", NpgsqlDbType.Inet)
        {
            Value = entry.SourceAddress is not null
                && IPAddress.TryParse(entry.SourceAddress, out IPAddress? address)
                    ? address
                    : DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Clip(string value, int most) =>
        value.Length <= most ? value : value[..most];
}
