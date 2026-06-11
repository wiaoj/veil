namespace Veil.Analytics.Features.Queries;

/// <summary>
/// Shared query-window handling: hours are clamped to the table's 30-day
/// TTL, the optional zone filter binds server-side ({zone:String}) so user
/// input never lands in the SQL string.
/// </summary>
internal static class AnalyticsQueryWindow {
    public const int DefaultHours = 24;
    private const int MaxHours = 720; // 30 days — matches the TTL

    public static int ClampHours(int hours) {
        return Math.Clamp(hours, 1, MaxHours);
    }

    /// <summary>WHERE clause + bound parameters for window and zone filter.</summary>
    public static (string Where, Dictionary<string, string>? Parameters) Filter(int hours, string? zone) {
        string where = $"ts >= now() - INTERVAL {ClampHours(hours)} HOUR";

        if(string.IsNullOrWhiteSpace(zone))
            return (where, null);

        return ($"{where} AND zone = {{zone:String}}", new Dictionary<string, string> { ["zone"] = zone.Trim() });
    }

    /// <summary>Series bucket width: ~100 points across the window.</summary>
    public static int BucketMinutes(int hours) {
        return ClampHours(hours) switch {
            <= 3 => 5,
            <= 12 => 10,
            <= 48 => 30,
            <= 168 => 120,
            _ => 720
        };
    }
}
