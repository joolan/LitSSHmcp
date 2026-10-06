using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LitSSHmcp.App.Controls;

namespace LitSSHmcp.App.Views;

public partial class TerminalSettingsView : UserControl
{
    public TerminalSettingsView()
    {
        InitializeComponent();

        ThemeBox.ItemsSource = TerminalSettings.Themes.Select(t => t.Name).ToList();
        CursorBox.ItemsSource = new[] { "block", "bar", "underline" };
        FontBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        ThemeBox.SelectedItem = TerminalSettings.Theme.Name;
        FontBox.Text = TerminalSettings.FontFamilyName;
        FontSizeBox.Text = TerminalSettings.FontSize.ToString("0.#");
        CursorBox.SelectedItem = TerminalSettings.CursorStyle;
        ScrollbackBox.Text = TerminalSettings.Scrollback.ToString();
        CopyOnSelectBox.IsChecked = TerminalSettings.CopyOnSelect;
        RecordSessionsBox.IsChecked = TerminalSettings.RecordSessions;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        double.TryParse(FontSizeBox.Text, out var fontSize);
        if (fontSize <= 0)
            fontSize = 14;
        int.TryParse(ScrollbackBox.Text, out var scrollback);
        if (scrollback <= 0)
            scrollback = 2000;

        TerminalSettings.Save(
            FontBox.Text,
            fontSize,
            scrollback,
            CursorBox.SelectedItem as string ?? "block",
            CopyOnSelectBox.IsChecked == true,
            ThemeBox.SelectedItem as string ?? TerminalSettings.Themes[0].Name,
            RecordSessionsBox.IsChecked == true);

        StatusText.Text = "已保存并应用";
    }
}
