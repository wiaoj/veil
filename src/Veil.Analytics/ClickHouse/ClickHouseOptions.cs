namespace Veil.Analytics.ClickHouse;

public sealed class ClickHouseOptions {
    public const string SectionName = "ClickHouse";

    /// <summary>HTTP interface base URL.</summary>
    public string Url { get; set; } = "http://localhost:8123";
    public string Database { get; set; } = "veil";
    public string Username { get; set; } = "veil";
    public string Password { get; set; } = "veil";

    /// <summary>Days before request log rows expire (ClickHouse TTL).</summary>
    public int RetentionDays { get; set; } = 30;
}
