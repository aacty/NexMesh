namespace NexMesh;

/// <summary>排序方向。</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>分页请求基类:继承后按需追加过滤字段。</summary>
public class PagedRequest
{
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    public string? Keyword { get; set; }

    public string? SortBy { get; set; }

    public SortDirection SortDirection { get; set; } = SortDirection.Ascending;
}

/// <summary>
/// 统一分页响应:Items/TotalCount/PageIndex/PageSize 必填,
/// TotalPages/HasNextPage/HasPreviousPage 由前四项计算得出(免手工维护一致性)。
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = [];

    public long TotalCount { get; set; }

    public int PageIndex { get; set; }

    public int PageSize { get; set; }

    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;

    public bool HasNextPage => PageIndex < TotalPages;

    public bool HasPreviousPage => PageIndex > 1;

    public PagedResult()
    {
    }

    public PagedResult(List<T> items, long totalCount, int pageIndex, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        PageIndex = pageIndex;
        PageSize = pageSize;
    }

    /// <summary>全量列表切页:输入完整数据集,取指定页片段并统计总数。</summary>
    public static PagedResult<T> From(IList<T> items, int pageIndex, int pageSize)
    {
        var page = pageSize > 0
            ? items.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList()
            : [.. items];
        return new PagedResult<T>(page, items.Count, pageIndex, pageSize);
    }
}
