using System.Threading.Channels;

namespace Veil.Api.ConfigSync;

/// <summary>
/// In-process "edge fleet needs a push" signal, fed by the Tyto event
/// handlers. Capacity-1 channel with drop-write semantics: a burst of events
/// coalesces into a single wake-up, which is exactly what the push loop
/// wants — it pushes the latest snapshot, not one push per event.
/// </summary>
public sealed class ConfigPushSignal {
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public void Notify() {
        this._channel.Writer.TryWrite(true);
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken) {
        await this._channel.Reader.ReadAsync(cancellationToken);
    }
}
