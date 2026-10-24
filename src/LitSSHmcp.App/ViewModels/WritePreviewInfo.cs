using System.Data;

namespace LitSSHmcp.App.ViewModels;

/// <summary>写入执行前的预览数据（影响行数 + 样本行），供确认对话框展示。</summary>
public sealed class WritePreviewInfo
{
    public string Sql { get; set; } = string.Empty;

    /// <summary>目标数据源名称。</summary>
    public string? DataSourceName { get; set; }

    /// <summary>目标库 / Redis DB。</summary>
    public string? DatabaseName { get; set; }

    /// <summary>操作类型摘要（SQL 写操作 / Redis 写命令 等）。</summary>
    public string? OperationLabel { get; set; }

    /// <summary>预计影响行数（无法预估时为 null）。</summary>
    public long? AffectedRows { get; set; }

    /// <summary>受影响样本行（最多 100 行，仅 UPDATE/DELETE 可预览）。</summary>
    public DataView? Sample { get; set; }

    /// <summary>预览说明（不支持/失败时的提示）。</summary>
    public string? Note { get; set; }

    public bool IsSensitive { get; set; }

    public string Describe()
    {
        if (Note is not null)
            return Note;
        var rows = AffectedRows is long n ? $"预计影响 {n} 行。" : "无法预估影响行数。";
        return rows + (Sample is not null ? "以下为受影响样本（最多 100 行）。" : string.Empty);
    }
}
