using System.Threading.Channels;

namespace Veil.Analytics.Ingestion;

/// <summary>
/// Hand-off between the interaction ingest endpoint and its flush service.
/// Bounded and drop-oldest, like <see cref="RequestLogQueue"/> — a ClickHouse
/// stall evicts old batches instead of back-pressuring edge nodes.
/// </summary>
public sealed class InteractionQueue {
    private const int CapacityBatches = 256;

    private readonly Channel<IReadOnlyList<InteractionRow>> channel =
        Channel.CreateBounded<IReadOnlyList<InteractionRow>>(new BoundedChannelOptions(CapacityBatches) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public void Enqueue(IReadOnlyList<InteractionRow> batch) {
        if(batch.Count > 0)
            this.channel.Writer.TryWrite(batch);
    }

    public IAsyncEnumerable<IReadOnlyList<InteractionRow>> ReadAllAsync(CancellationToken cancellationToken) {
        return this.channel.Reader.ReadAllAsync(cancellationToken);
    }
}
