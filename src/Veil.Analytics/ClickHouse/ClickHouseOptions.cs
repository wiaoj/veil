namespace Veil.Analytics.ClickHouse;

/// <summary>
/// Typed view of the <c>ClickHouse</c> configuration section.
/// </summary>
public sealed record ClickHouseOptions {
    public const string SectionName = "ClickHouse";

    /// <summary>HTTP interface base URL.</summary>
    public string Url { get; init; } = "http://localhost:8123";
    public string Database { get; init; } = "veil";
    public string Username { get; init; } = "veil";
    public string Password { get; init; } = "veil";

    /// <summary>Days before request log rows expire (ClickHouse TTL).</summary>
    public int RetentionDays { get; init; } = 30;
}
