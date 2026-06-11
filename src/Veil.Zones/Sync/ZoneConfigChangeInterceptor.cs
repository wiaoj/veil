using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Veil.Zones.Sync;

/// <summary>
/// Signals <see cref="ZoneConfigChangeSignal"/> after every successful write
/// through <c>ZonesDbContext</c>. The context only holds zone configuration,
/// so any persisted change is by definition a config change — no per-entity
/// inspection or per-endpoint plumbing needed.
/// </summary>
public sealed class ZoneConfigChangeInterceptor(ZoneConfigChangeSignal signal) : SaveChangesInterceptor {
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) {
        if(result > 0) signal.NotifyChanged();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default) {
        if(result > 0) signal.NotifyChanged();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}
