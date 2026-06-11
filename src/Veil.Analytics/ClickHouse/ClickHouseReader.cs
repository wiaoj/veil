using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Veil.Analytics.ClickHouse;

/// <summary>
/// Read-side counterpart of <see cref="ClickHouseWriter"/>: executes a
/// SELECT over the HTTP interface and materialises JSONEachRow lines.
/// User-supplied values must go through <c>parameters</c> (ClickHouse
/// server-side <c>{name:Type}</c> binding) — never into the SQL string.
/// </summary>
public sealed class ClickHouseReader(IHttpClientFactory httpClientFactory, IOptions<ClickHouseOptions> options) {
    private static readonly JsonSerializerOptions RowSerializerOptions = new();

    private readonly ClickHouseOptions options = options.Value;

    public async Task<List<T>> QueryAsync<T>(
        string sql,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken) {
        // 64-bit aggregates (count, sum) must come back as JSON numbers,
        // not the quoted strings ClickHouse emits by default.
        string url = $"{this.options.Url.TrimEnd('/')}/" +
            $"?database={Uri.EscapeDataString(this.options.Database)}" +
            $"&query={Uri.EscapeDataString(sql + " FORMAT JSONEachRow")}" +
            "&output_format_json_quote_64bit_integers=0";

        if(parameters is not null) {
            foreach((string name, string value) in parameters)
                url += $"&param_{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        }

        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Add("X-ClickHouse-User", this.options.Username);
        request.Headers.Add("X-ClickHouse-Key", this.options.Password);

        HttpClient client = httpClientFactory.CreateClient(ClickHouseWriter.HttpClientName);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if(!response.IsSuccessStatusCode) {
            throw new InvalidOperationException(
                $"ClickHouse returned {(int)response.StatusCode}: {(body.Length <= 500 ? body : body[..500])}");
        }

        List<T> rows = [];
        foreach(string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            T? row = JsonSerializer.Deserialize<T>(line, RowSerializerOptions);
            if(row is not null)
                rows.Add(row);
        }

        return rows;
    }
}
