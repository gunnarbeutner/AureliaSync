using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Wire;

namespace Jellyfin.Plugin.AureliaSync.Streaming;

/// <summary>
/// Writes one NDJSON segment.
/// </summary>
/// <remarks>
/// <para>
/// Payloads are copied straight from storage with <see cref="Utf8JsonWriter.WriteRawValue(System.ReadOnlySpan{byte}, bool)"/>
/// rather than deserialised and reserialised. Across tens of thousands of records per snapshot this
/// is the difference between streaming being a memory copy and being CPU-bound.
/// </para>
/// <para>
/// The byte budget counts <b>every</b> byte written, framing and newlines included, because that is
/// what the client counts against its own limit. Budgeting only payloads would overshoot.
/// </para>
/// </remarks>
public sealed class NdjsonSegmentWriter
{
    /// <summary>
    /// How much is accumulated before flushing to the network.
    /// </summary>
    /// <remarks>
    /// Keeps peak memory flat regardless of segment size, and keeps bytes moving so the client's
    /// between-packets timeout never fires on a slow segment.
    /// </remarks>
    public const int FlushThreshold = 64 * 1024;

    private static readonly byte[] Newline = "\n"u8.ToArray();

    /// <summary>
    /// Writes a complete segment: opening line, records, closing line.
    /// </summary>
    /// <param name="output">Where to write. Already gzip-wrapped if the client asked for it.</param>
    /// <param name="begin">The opening line.</param>
    /// <param name="rows">Candidate rows, in ascending ordinal order.</param>
    /// <param name="afterOrdinal">The position the client asked to continue from.</param>
    /// <param name="upperBound">The highest ordinal this session will ever deliver.</param>
    /// <param name="snapshotReady">Whether the snapshot is complete and may report catch-up.</param>
    /// <param name="maxRecords">Record limit.</param>
    /// <param name="maxTotalBytes">Budget for the whole segment, framing included.</param>
    /// <param name="timeBudget">Wall-clock budget.</param>
    /// <param name="onIssued">
    /// Invoked with the last ordinal written <b>before</b> the closing line is emitted, so an
    /// acknowledgement for a cursor the client received always validates even if the connection
    /// dies during the final flush.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the segment contained.</returns>
    public static async Task<SegmentOutcome> WriteAsync(
        Stream output,
        SegmentBegin begin,
        IReadOnlyList<SnapshotRow> rows,
        long afterOrdinal,
        long upperBound,
        bool snapshotReady,
        int maxRecords,
        long maxTotalBytes,
        TimeSpan timeBudget,
        Func<long, Task>? onIssued,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rows);

        var buffer = new ArrayBufferWriter<byte>(FlushThreshold * 2);
        var stopwatch = Stopwatch.StartNew();

        long total = 0;
        long payloadBytes = 0;
        var written = 0;
        var lastOrdinal = afterOrdinal;
        var stopReason = SegmentOutcome.StopUpperBound;

        total += await WriteLineAsync(output, buffer, begin, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];

            // A segment may only end where the next row starts a fresh group. Cutting inside one
            // would split a playlist across two segments, and the client clears a playlist's
            // membership and reinserts only what the segment it is applying contained — so the
            // playlist would silently lose every entry that landed in the other half.
            //
            // The consequence is that a single group larger than the budget is emitted whole and
            // overshoots. That is deliberate: an oversized segment is recoverable, a truncated
            // playlist is silent data loss.
            var startsNewGroup = row.GroupKey is null
                || !string.Equals(row.GroupKey, rows[index - 1].GroupKey, StringComparison.Ordinal);

            // Budgets are only consulted once something has been written: a segment of zero records
            // that could have carried one would stall the client forever at this ordinal.
            if (written > 0 && startsNewGroup)
            {
                if (written >= maxRecords)
                {
                    stopReason = SegmentOutcome.StopMaxRecords;
                    break;
                }

                if (total >= maxTotalBytes)
                {
                    stopReason = SegmentOutcome.StopMaxBytes;
                    break;
                }

                if (stopwatch.Elapsed >= timeBudget)
                {
                    stopReason = SegmentOutcome.StopTimeBudget;
                    break;
                }
            }

            total += await WriteRecordAsync(output, buffer, row, begin.Generation ?? 0, cancellationToken)
                .ConfigureAwait(false);
            payloadBytes += row.Payload.Length;
            lastOrdinal = row.Ordinal;
            written++;
        }

        // Delivery is complete only when the snapshot itself is finished and everything in it has
        // been handed over. Reporting it early truncates the client's library.
        var caughtUp = snapshotReady && lastOrdinal >= upperBound && written == rows.Count;

        if (onIssued is not null)
        {
            await onIssued(lastOrdinal).ConfigureAwait(false);
        }

        var cursor = Cursor.ForSnapshot(begin.Generation ?? 0, lastOrdinal).Encode();
        var end = new SegmentEnd
        {
            Cursor = cursor,
            RecordCount = written,
            ByteCount = payloadBytes,
            CaughtUp = caughtUp,
            SessionUpperBound = upperBound,
            StopReason = caughtUp ? SegmentOutcome.StopUpperBound : stopReason,
            NextAfter = cursor
        };

        total += await WriteLineAsync(output, buffer, end, cancellationToken).ConfigureAwait(false);
        await FlushAsync(output, buffer, cancellationToken).ConfigureAwait(false);

        return new SegmentOutcome(written, payloadBytes, total, lastOrdinal, caughtUp, end.StopReason!);
    }

    /// <summary>
    /// Writes an in-band failure line.
    /// </summary>
    /// <remarks>
    /// Only valid once the body has started. Because the segment then ends without its closing
    /// line, the client discards everything it read and retries from its last acknowledgement.
    /// </remarks>
    /// <param name="output">Where to write.</param>
    /// <param name="error">The failure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the line is flushed.</returns>
    public static async Task WriteErrorAsync(Stream output, ErrorLine error, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        await WriteLineAsync(output, buffer, error, cancellationToken).ConfigureAwait(false);
        await FlushAsync(output, buffer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> WriteRecordAsync(
        Stream output,
        ArrayBufferWriter<byte> buffer,
        SnapshotRow row,
        long generation,
        CancellationToken cancellationToken)
    {
        var before = buffer.WrittenCount;

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("cursor", Cursor.ForSnapshot(generation, row.Ordinal).Encode());
            writer.WriteNumber("sequence", row.Ordinal);
            writer.WriteString("kind", row.Kind);

            if (row.EntityType is not null)
            {
                writer.WriteString("entityType", row.EntityType);
            }

            writer.WriteString("entityId", row.EntityId);

            if (row.Checksum is not null)
            {
                writer.WriteString("checksum", row.Checksum);
            }

            writer.WritePropertyName("payload");

            // The payload was serialised when the snapshot was built and has not been touched
            // since, so validating it again per record would be pure cost.
            writer.WriteRawValue(row.Payload, skipInputValidation: true);
            writer.WriteEndObject();
        }

        buffer.Write(Newline);
        var length = buffer.WrittenCount - before;

        if (buffer.WrittenCount >= FlushThreshold)
        {
            await FlushAsync(output, buffer, cancellationToken).ConfigureAwait(false);
        }

        return length;
    }

    private static async Task<long> WriteLineAsync<T>(
        Stream output,
        ArrayBufferWriter<byte> buffer,
        T value,
        CancellationToken cancellationToken)
    {
        var before = buffer.WrittenCount;

        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, value, WireSchema.JsonOptions);
        }

        buffer.Write(Newline);
        var length = buffer.WrittenCount - before;

        if (buffer.WrittenCount >= FlushThreshold)
        {
            await FlushAsync(output, buffer, cancellationToken).ConfigureAwait(false);
        }

        return length;
    }

    private static async Task FlushAsync(
        Stream output, ArrayBufferWriter<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.WrittenCount == 0)
        {
            return;
        }

        await output.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        buffer.Clear();
    }
}
