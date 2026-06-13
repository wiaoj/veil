using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Veil.Shared.Observability;

/// <summary>
/// Minimal, dependency-free Prometheus counter registry. Thread-safe via
/// atomic interlocked adds; rendered to the text exposition format on a
/// <c>/metrics</c> endpoint. Counters only — enough for the rates the
/// control plane reports (config pushes, ClickHouse writes). Reach for
/// OpenTelemetry if histograms or richer instruments are ever needed.
/// </summary>
public sealed class MetricsCollector {
    private sealed class Family {
        public required string Help { get; init; }
        public ConcurrentDictionary<string, long> Series { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Family> _families = new();

    /// <summary>
    /// Adds <paramref name="value"/> to the counter series identified by
    /// <paramref name="name"/> and the (sorted) label set.
    /// </summary>
    public void IncrementCounter(
        string name,
        string help,
        long value = 1,
        params (string Key, string Value)[] labels) {
        Family family = this._families.GetOrAdd(name, _ => new Family { Help = help });
        string series = FormatSeries(name, labels);
        family.Series.AddOrUpdate(series, value, (_, current) => current + value);
    }

    public string Render() {
        StringBuilder sb = new(1024);
        foreach((string name, Family family) in this._families.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            sb.Append("# HELP ").Append(name).Append(' ').Append(family.Help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" counter\n");
            foreach((string series, long value) in family.Series.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(series).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static string FormatSeries(string name, (string Key, string Value)[] labels) {
        if(labels.Length == 0)
            return name;

        StringBuilder sb = new(name.Length + 16);
        sb.Append(name).Append('{');
        bool first = true;
        foreach((string key, string val) in labels.OrderBy(l => l.Key, StringComparer.Ordinal)) {
            if(!first) sb.Append(',');
            first = false;
            sb.Append(key).Append("=\"").Append(Escape(val)).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string Escape(string value) {
        return value.Contains('\\') || value.Contains('"') || value.Contains('\n')
            ? value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            : value;
    }
}
