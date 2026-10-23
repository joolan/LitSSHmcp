using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

/// <summary>拓扑手动布局的读取/保存与撤销重做历史（不依赖 WPF 图形元素）。</summary>
public sealed class TopologyLayoutHistory
{
    private readonly ITopologyLayoutStore _store;
    private readonly Stack<TopologyLayout> _undo = new();
    private readonly Stack<TopologyLayout> _redo = new();
    private TopologyLayout? _layout;

    public TopologyLayoutHistory(ITopologyLayoutStore store) => _store = store;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public TopologyLayout Get() => _layout ??= _store.Load();

    public void Save(TopologyLayout layout)
    {
        _layout = layout;
        _store.Save(layout);
    }

    /// <summary>提交前记录当前布局（用于撤销），并清空重做栈。</summary>
    public void PushUndo()
    {
        _undo.Push(Get());
        _redo.Clear();
    }

    /// <summary>撤销：当前布局进入重做栈，恢复上一版；无历史返回 false。</summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        _redo.Push(Get());
        Save(_undo.Pop());
        return true;
    }

    /// <summary>重做：当前布局回到撤销栈；无历史返回 false。</summary>
    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        _undo.Push(Get());
        Save(_redo.Pop());
        return true;
    }

    /// <summary>清除手动布局；保留撤销栈，调用方先 PushUndo。</summary>
    public void Clear()
    {
        _store.Clear();
        _layout = null;
    }
}
