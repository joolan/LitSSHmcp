namespace LitSSHmcp.Core.Models;

/// <summary>
/// 资产拓扑可视化编辑器的手动布局：节点位置/尺寸 + 连线端点锚点。
/// 独立于语义配置（config.Relations），存于 %APPDATA%\LitSSH\topology-layout.json。
/// 资产增删时可增量合并；未记录布局的节点回退自动布局。
/// </summary>
public class TopologyLayout
{
    /// <summary>是否存在手动布局（false 表示全部走自动布局）。</summary>
    public bool IsManual { get; set; }

    /// <summary>节点布局：nodeId -> 位置与尺寸。</summary>
    public Dictionary<string, NodeLayout> Nodes { get; set; } = new();

    /// <summary>连线锚点：edgeKey(from|type|to) -> 两端锚点。</summary>
    public Dictionary<string, EdgeLayout> Edges { get; set; } = new();
}

public class NodeLayout
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class EdgeLayout
{
    public AnchorSpec? From { get; set; }
    public AnchorSpec? To { get; set; }
}

/// <summary>端点锚点：哪条边（side）+ 在该边上的比例位置（0..1）。</summary>
public class AnchorSpec
{
    /// <summary>left / right / top / bottom；auto 表示由路由自动选择。</summary>
    public string Side { get; set; } = "auto";

    /// <summary>在该边上的比例位置（0..1，默认 0.5 中点）。</summary>
    public double Ratio { get; set; } = 0.5;
}
