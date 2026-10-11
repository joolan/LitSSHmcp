using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Agent;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>技能选择窗口：左侧多选技能，右侧预览正文；确定后由调用方应用到当前会话。</summary>
public partial class SkillPickerWindow : FluentWindow
{
    private readonly string _root;

    public SkillPickerWindow(string root, IReadOnlyList<SkillInfo> skills, IReadOnlyList<string> activeIds)
    {
        InitializeComponent();
        _root = root;

        var items = skills.Select(s => new SkillPickItem(s)).ToList();
        SkillList.ItemsSource = items;

        if (activeIds.Count > 0)
        {
            var active = new HashSet<string>(activeIds, StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Where(i => active.Contains(i.Info.Id)))
                SkillList.SelectedItems.Add(item);
        }

        ShowPreview(SkillList.SelectedItems.Cast<SkillPickItem>().LastOrDefault() ?? items.FirstOrDefault());
        UpdateStatus();
    }

    /// <summary>确定后返回的激活技能 Id。</summary>
    public IReadOnlyList<string> SelectedSkillIds { get; private set; } = Array.Empty<string>();

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ShowPreview(SkillList.SelectedItems.Cast<SkillPickItem>().LastOrDefault());
        UpdateStatus();
    }

    private void ShowPreview(SkillPickItem? item)
    {
        if (item is null)
        {
            Preview.Markdown = "_（选择左侧技能查看内容）_";
            return;
        }

        try
        {
            var path = Path.Combine(_root, item.Info.MainPath.Replace('/', Path.DirectorySeparatorChar));
            Preview.Markdown = File.Exists(path) ? File.ReadAllText(path) : "（文件不存在）";
        }
        catch (Exception ex)
        {
            Preview.Markdown = "（读取失败: " + ex.Message + "）";
        }
    }

    private void UpdateStatus()
    {
        var count = SkillList.SelectedItems.Count;
        StatusText.Text = count == 0 ? "未选择技能（索引模式）" : $"已选择 {count} 个技能";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SelectedSkillIds = SkillList.SelectedItems.Cast<SkillPickItem>().Select(i => i.Info.Id).ToList();
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        SkillList.SelectedItems.Clear();
        SelectedSkillIds = Array.Empty<string>();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---- 导入 / 编辑 ----

    private void OnOpenSkillsDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_root);
            Process.Start(new ProcessStartInfo(_root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开技能目录失败: " + ex.Message, "选择技能");
        }
    }

    private void OnEditSkill(object sender, MouseButtonEventArgs e)
    {
        if (SkillList.SelectedItem is not SkillPickItem item)
            return;
        try
        {
            var path = Path.Combine(_root, item.Info.MainPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开技能文件失败: " + ex.Message, "选择技能");
        }
    }

    private void OnImportFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "导入技能（.md）",
            Filter = "技能 Markdown|*.md|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        int ok = 0, skip = 0;
        foreach (var file in dialog.FileNames)
        {
            var name = Sanitize(Path.GetFileNameWithoutExtension(file));
            var dir = Path.Combine(_root, name);
            try
            {
                if (Directory.Exists(dir))
                {
                    skip++;
                    continue;
                }
                Directory.CreateDirectory(dir);
                File.Copy(file, Path.Combine(dir, "SKILL.md"), overwrite: false);
                ok++;
            }
            catch
            {
                skip++;
            }
        }
        AfterImport(ok, skip);
    }

    private void OnImportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "导入技能目录（须含 SKILL.md）" };
        if (dialog.ShowDialog() != true)
            return;

        var source = dialog.FolderName;
        if (!File.Exists(Path.Combine(source, "SKILL.md")))
        {
            System.Windows.MessageBox.Show("所选目录不含 SKILL.md，无法作为技能导入。", "选择技能");
            return;
        }

        var dest = Path.Combine(_root, Sanitize(Path.GetFileName(source.TrimEnd('\\', '/'))));
        int ok = 0, skip = 0;
        try
        {
            if (Directory.Exists(dest))
            {
                skip = 1;
            }
            else
            {
                CopyDir(source, dest);
                ok = 1;
            }
        }
        catch
        {
            skip = 1;
        }
        AfterImport(ok, skip);
    }

    private void AfterImport(int ok, int skip)
    {
        ReloadList();
        StatusText.Text = $"导入完成：成功 {ok}，跳过（已存在/失败）{skip}";
    }

    private void ReloadList()
    {
        var selected = SkillList.SelectedItems.Cast<SkillPickItem>()
            .Select(i => i.Info.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = SkillRegistry.List(_root).Select(s => new SkillPickItem(s)).ToList();
        SkillList.ItemsSource = items;
        foreach (var item in items.Where(i => selected.Contains(i.Info.Id)))
            SkillList.SelectedItems.Add(item);

        ShowPreview(SkillList.SelectedItems.Cast<SkillPickItem>().LastOrDefault() ?? items.FirstOrDefault());
        UpdateStatus();
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)));
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "skill" : cleaned;
    }

    /// <summary>列表项包装：给 XAML 提供 Name/Description/DomainText/Note/NoteVisibility。</summary>
    public sealed class SkillPickItem
    {
        public SkillPickItem(SkillInfo info) => Info = info;

        public SkillInfo Info { get; }
        public string Name => Info.Name;
        public string Description => Info.Description;
        public string DomainText => Info.DomainText;

        public string Note
        {
            get
            {
                if (!Info.Applicable)
                    return "⚠ 涉及的能力域当前全部未启用";
                if (Info.DisabledDomains.Count > 0)
                    return "部分域未启用：" + Info.DisabledDomains.Count;
                return string.Empty;
            }
        }

        public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    }
}
