using System.Threading.Channels;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// Hand-off between the ingest endpoint and the ClickHouse flush service.
/// Bounded and drop-oldest: when ClickHouse stalls, old batches are evicted
/// instead of back-pressuring edge nodes (fire-and-forget by design).
/// </summary>
public sealed class RequestLogQueue {
    private const int CapacityBatches = 256;

    private readonly Channel<IReadOnlyList<RequestLogRow>> channel =
        Channel.CreateBounded<IReadOnlyList<RequestLogRow>>(new BoundedChannelOptions(CapacityBatches) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public void Enqueue(IReadOnlyList<RequestLogRow> batch) {
        if(batch.Count > 0)
            this.channel.Writer.TryWrite(batch);
    }

    public IAsyncEnumerable<IReadOnlyList<RequestLogRow>> ReadAllAsync(CancellationToken cancellationToken) {
        return this.channel.Reader.ReadAllAsync(cancellationToken);
    }
}
