using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>
/// 自建「打开方式」对话框（避开 Windows 11 对系统 OpenAs 的限制）。
/// 按文件扩展名收集候选程序：扩展名 OpenWithProgids / OpenWithList + HKCR\Applications（含 SupportedTypes 过滤）。
/// </summary>
public partial class OpenWithDialog : FluentWindow
{
    private readonly string _file;
    private bool _launched;

    public OpenWithDialog(string file)
    {
        InitializeComponent();
        _file = file;
        FileText.Text = file;
        List.ItemsSource = EnumeratePrograms(file);
    }

    /// <summary>弹窗让用户选择程序打开文件；返回是否已用某程序打开。</summary>
    public static bool Show(Window? owner, string file)
    {
        var dialog = new OpenWithDialog(file);
        if (owner is not null)
            dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._launched;
    }

    private static List<ProgramItem> EnumeratePrograms(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var programs = new List<ProgramItem>();
        var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? command, string? fallbackName)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            var commandText = command!;
            var exe = TryGetExe(commandText) ?? string.Empty;
            if (exe.Length > 0 && !seenExe.Add(exe))
                return;

            var name = Description(exe) ?? fallbackName;
            if (string.IsNullOrWhiteSpace(name))
                name = exe.Length > 0 ? Path.GetFileName(exe) : commandText;
            if (!seenName.Add(name!))
                return;
            programs.Add(new ProgramItem(name!, exe, commandText));
        }

        // 1) 扩展名的 OpenWithProgids（HKCR + HKCU FileExts）
        foreach (var (hive, path) in new[]
                 {
                     (Registry.ClassesRoot, ext + "\\OpenWithProgids"),
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + "\\OpenWithProgids")
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null)
                    continue;
                foreach (var progId in key.GetValueNames())
                    Add(GetProgidCommand(progId), GetProgidName(progId));
            }
            catch
            {
                // 忽略
            }
        }

        // 2) OpenWithList（exe 名）
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + "\\OpenWithList");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (string.Equals(valueName, "MRUList", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (key.GetValue(valueName) is string exeName && !string.IsNullOrWhiteSpace(exeName))
                        Add(GetApplicationCommand(exeName) ?? $"\"{exeName}\" %1", null);
                }
            }
        }
        catch
        {
            // 忽略
        }

        // 3) HKCR\Applications（若声明 SupportedTypes 则按扩展名过滤）
        try
        {
            using var apps = Registry.ClassesRoot.OpenSubKey("Applications");
            if (apps is not null)
            {
                foreach (var name in apps.GetSubKeyNames())
                {
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var appKey = apps.OpenSubKey(name);
                    using var supported = appKey?.OpenSubKey("SupportedTypes");
                    if (supported is not null)
                    {
                        var types = supported.GetValueNames();
                        if (types.Length > 0 && ext.Length > 0 &&
                            !types.Any(n => string.Equals(n, ext, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }
                    var command = appKey?.OpenSubKey("shell\\open\\command")?.GetValue(null) as string;
                    Add(command, appKey?.GetValue(null) as string);
                }
            }
        }
        catch
        {
            // 忽略
        }

        if (programs.Count == 0)
            programs.Add(new ProgramItem("记事本", "notepad.exe", "notepad.exe %1"));

        return programs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? GetProgidCommand(string progId)
    {
        try { return Registry.ClassesRoot.OpenSubKey(progId + "\\shell\\open\\command")?.GetValue(null) as string; }
        catch { return null; }
    }

    private static string? GetProgidName(string progId)
    {
        try { return Registry.ClassesRoot.OpenSubKey(progId)?.GetValue(null) as string; }
        catch { return null; }
    }

    private static string? GetApplicationCommand(string exeName)
    {
        try { return Registry.ClassesRoot.OpenSubKey("Applications\\" + exeName + "\\shell\\open\\command")?.GetValue(null) as string; }
        catch { return null; }
    }

    private static string? Description(string exe)
    {
        try
        {
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                return FileVersionInfo.GetVersionInfo(exe).FileDescription;
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is ProgramItem item)
        {
            if (Launch(item.Command, _file))
            {
                _launched = true;
                Close();
            }
        }
        else
        {
            System.Windows.MessageBox.Show("请选择一个程序。", "打开方式", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择程序", Filter = "程序 (*.exe)|*.exe|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (Launch(dialog.FileName, _file))
        {
            _launched = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private static string? TryGetExe(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 0 ? c[1..end] : null;
        }
        var sp = c.IndexOf(' ');
        return sp < 0 ? c : c[..sp];
    }

    private static bool Launch(string commandTemplate, string file)
    {
        try
        {
            string exe;
            string args;
            var command = commandTemplate.Trim();
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                if (end < 0)
                {
                    exe = command.Trim('"');
                    args = string.Empty;
                }
                else
                {
                    exe = command[1..end];
                    args = command[(end + 1)..].Trim();
                }
            }
            else
            {
                var space = command.IndexOf(' ');
                exe = space < 0 ? command : command[..space];
                args = space < 0 ? string.Empty : command[(space + 1)..].Trim();
            }

            if (!File.Exists(exe))
            {
                System.Windows.MessageBox.Show($"找不到程序：{exe}", "打开方式", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return false;
            }

            if (args.Contains("%1"))
                args = args.Replace("%1", $"\"{file}\"");
            else
                args = string.IsNullOrEmpty(args) ? $"\"{file}\"" : $"{args} \"{file}\"";

            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("启动失败: " + ex.Message, "打开方式", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    private sealed record ProgramItem(string Name, string Exe, string Command);
}
