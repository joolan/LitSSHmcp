using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>自建「打开方式」对话框（避开 Windows 11 对系统 OpenAs 的限制）：列出已注册应用，可选“浏览…”。</summary>
public partial class OpenWithDialog : FluentWindow
{
    private readonly string _file;

    public OpenWithDialog(string file)
    {
        InitializeComponent();
        _file = file;
        FileText.Text = file;
        List.ItemsSource = EnumeratePrograms();
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

    private bool _launched;

    private static List<ProgramItem> EnumeratePrograms()
    {
        var programs = new List<ProgramItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    var display = appKey?.GetValue(null) as string;
                    using var cmdKey = appKey?.OpenSubKey("shell\\open\\command");
                    var command = cmdKey?.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(command))
                        continue;
                    var label = string.IsNullOrWhiteSpace(display) ? name : display!;
                    if (seen.Add(label))
                        programs.Add(new ProgramItem(label, name, command!));
                }
            }
        }
        catch
        {
            // 注册表读取失败则回退
        }

        if (programs.Count == 0)
        {
            foreach (var exe in new[] { "notepad.exe" })
                programs.Add(new ProgramItem(exe, exe, exe));
        }
        return programs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
