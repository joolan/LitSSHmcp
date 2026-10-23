using System.Collections.ObjectModel;
using System.IO;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

/// <summary>AI 助手待发送附件的收集与解析（文件/文件夹 → 图片或文档）。</summary>
internal sealed class AgentAttachmentManager
{
    private const int MaxFolderFiles = 50;

    private readonly Action<string> _setStatus;

    public AgentAttachmentManager(Action<string> setStatus) => _setStatus = setStatus;

    public ObservableCollection<AgentAttachment> Items { get; } = new();

    public bool HasAttachments => Items.Count > 0;

    /// <summary>附件集合发生变化（供宿主刷新 <see cref="HasAttachments"/> 绑定）。</summary>
    public event Action? Changed;

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                List<string> files;
                try
                {
                    files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).ToList();
                }
                catch (Exception ex)
                {
                    _setStatus("读取文件夹失败: " + ex.Message);
                    continue;
                }

                if (files.Count == 0)
                {
                    _setStatus($"文件夹为空，已忽略: {Path.GetFileName(path)}");
                    continue;
                }

                foreach (var file in files.Take(MaxFolderFiles))
                    AddOneFile(file);

                if (files.Count > MaxFolderFiles)
                    _setStatus($"文件夹内文件较多，仅添加前 {MaxFolderFiles} 个: {Path.GetFileName(path)}");
                continue;
            }

            AddOneFile(path);
        }

        NotifyChanged();
    }

    public void Remove(AgentAttachment attachment)
    {
        if (Items.Remove(attachment))
            NotifyChanged();
    }

    public void Clear()
    {
        if (Items.Count == 0)
            return;
        Items.Clear();
        NotifyChanged();
    }

    public void NotifyChanged() => Changed?.Invoke();

    private void AddOneFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _setStatus("已跳过(文件不存在): " + Path.GetFileName(path));
                return;
            }
            if (Items.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                return;

            var parsed = AttachmentService.Parse(path);
            if (parsed.Kind == AttachmentKind.Document && parsed.Text.StartsWith("(暂不支持解析", StringComparison.Ordinal))
            {
                _setStatus("已跳过(不支持的类型): " + parsed.Name);
                return;
            }
            Items.Add(new AgentAttachment
            {
                Name = parsed.Name,
                Path = path,
                Kind = parsed.Kind,
                MediaType = parsed.MediaType,
                ImageBytes = parsed.ImageBytes,
                Text = parsed.Text
            });
        }
        catch (Exception ex)
        {
            _setStatus("附件读取失败: " + ex.Message);
        }
    }
}
