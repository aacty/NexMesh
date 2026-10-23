namespace NexMesh.Toolbox;

using MiniExcelLibs;

/// <summary>Excel 导入导出(MiniExcel 零模板):首行表头来自属性名(可用 [ExcelColumn] 定制)。</summary>
public static class NexMeshExcel
{
    /// <summary>导出到 xlsx 流(内存)。</summary>
    public static async Task ExportAsync<T>(Stream output, IEnumerable<T> rows, CancellationToken ct = default)
    {
        await output.SaveAsAsync(rows, cancellationToken: ct);
        output.Position = 0;
    }

    /// <summary>从 xlsx 流导入为强类型集合。</summary>
    public static async Task<List<T>> ImportAsync<T>(Stream input, CancellationToken ct = default) where T : class, new()
    {
        var rows = await input.QueryAsync<T>(cancellationToken: ct);
        return rows.ToList();
    }
}
