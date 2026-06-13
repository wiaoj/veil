namespace Veil.Shared;

/// <summary>
/// A single page of results plus the paging metadata every list endpoint
/// returns. The JSON shape (<c>items</c>/<c>page</c>/<c>pageSize</c>/
/// <c>totalCount</c>) is the contract the dashboard already consumes.
/// </summary>
public sealed record PagedList<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount) {
    /// <summary>Total number of pages for the current page size (≥ 1).</summary>
    public int TotalPages => this.PageSize <= 0 ? 0 : (int)Math.Ceiling((double)this.TotalCount / this.PageSize);

    public static PagedList<T> Create(IReadOnlyList<T> items, int page, int pageSize, int totalCount) {
        return new PagedList<T>(items, page, pageSize, totalCount);
    }

    /// <summary>Clamps a requested page/size to sane bounds.</summary>
    public static (int Page, int PageSize) Normalize(int page, int pageSize, int defaultSize = 20, int maxSize = 100) {
        return (Math.Max(page, 1), Math.Clamp(pageSize, 1, maxSize <= 0 ? defaultSize : maxSize));
    }
}
