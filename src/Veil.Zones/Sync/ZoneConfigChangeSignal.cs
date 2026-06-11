using System.Threading.Channels;

namespace Veil.Zones.Sync;

/// <summary>
/// In-process "zone config changed" signal. Capacity-1 channel with
/// drop-write semantics: a burst of mutations coalesces into a single
/// wake-up, which is exactly what the config sync loop wants — it pushes
/// the latest snapshot, not one push per change.
/// </summary>
public sealed class ZoneConfigChangeSignal {
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public void NotifyChanged() {
        this._channel.Writer.TryWrite(true);
    }

    public async ValueTask WaitForChangeAsync(CancellationToken cancellationToken) {
        await this._channel.Reader.ReadAsync(cancellationToken);
    }
}
