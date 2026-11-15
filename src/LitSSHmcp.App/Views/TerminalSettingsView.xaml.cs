using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LitSSHmcp.App.Controls;
using Microsoft.Win32;

namespace LitSSHmcp.App.Views;

public partial class TerminalSettingsView : UserControl
{
    public TerminalSettingsView()
    {
        InitializeComponent();

        ThemeBox.ItemsSource = TerminalSettings.AllThemes.Select(t => t.Name).ToList();
        CursorBox.ItemsSource = new[] { "block", "bar", "underline" };
        FontBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        ThemeBox.SelectedItem = TerminalSettings.Theme.Name;
        FontBox.Text = TerminalSettings.FontFamilyName;
        FontSizeBox.Text = TerminalSettings.FontSize.ToString("0.#");
        CursorBox.SelectedItem = TerminalSettings.CursorStyle;
        ScrollbackBox.Text = TerminalSettings.Scrollback.ToString();
        CopyOnSelectBox.IsChecked = TerminalSettings.CopyOnSelect;
        ShowFileBrowserBox.IsChecked = TerminalSettings.ShowFileBrowser;
        RecordSessionsBox.IsChecked = TerminalSettings.RecordSessions;
        LogRetentionBox.Text = TerminalSettings.LogRetentionDays.ToString();
    }

    private void OnImportTheme(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 Windows Terminal 配色",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var theme = TerminalSettings.ImportWindowsTerminalTheme(dialog.FileName);
            ThemeBox.ItemsSource = TerminalSettings.AllThemes.Select(t => t.Name).ToList();
            ThemeBox.SelectedItem = theme?.Name;
            StatusText.Text = theme is null ? "未读取到配色" : $"已导入配色：{theme.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        double.TryParse(FontSizeBox.Text, out var fontSize);
        if (fontSize <= 0)
            fontSize = 14;
        int.TryParse(ScrollbackBox.Text, out var scrollback);
        int.TryParse(LogRetentionBox.Text, out var logRetentionDays);
        if (logRetentionDays <= 0)
            logRetentionDays = 30;
        if (scrollback <= 0)
            scrollback = 2000;

        TerminalSettings.Save(
            FontBox.Text,
            fontSize,
            scrollback,
            CursorBox.SelectedItem as string ?? "block",
            CopyOnSelectBox.IsChecked == true,
            ThemeBox.SelectedItem as string ?? TerminalSettings.AllThemes[0].Name,
            RecordSessionsBox.IsChecked == true,
            ShowFileBrowserBox.IsChecked == true,
            logRetentionDays);

        StatusText.Text = "已保存并应用";
    }
}
