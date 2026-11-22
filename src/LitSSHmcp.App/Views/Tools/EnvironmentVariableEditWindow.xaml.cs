using System.Windows;
using LitSSHmcp.App.Services.Toolbox;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class EnvironmentVariableEditWindow : FluentWindow
{
    private EnvironmentVariableScope _scope = EnvironmentVariableScope.User;

    public EnvironmentVariableEditWindow()
    {
        InitializeComponent();
    }

    public EnvironmentVariableEntry? Result { get; private set; }

    public void Configure(EnvironmentVariableScope scope, EnvironmentVariableEntry? entry, bool isNew)
    {
        _scope = scope;
        Title = isNew ? "新增环境变量" : "编辑环境变量";
        ScopeText.Text = scope == EnvironmentVariableScope.User
            ? @"用户变量（HKEY_CURRENT_USER\Environment）"
            : @"系统变量（HKEY_LOCAL_MACHINE\...\Environment，保存时需要管理员权限）";
        NameBox.Text = entry?.Name ?? string.Empty;
        ValueBox.Text = entry is not null && EnvironmentVariableRules.IsPath(entry.Name)
            ? string.Join(Environment.NewLine, entry.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            : entry?.Value ?? string.Empty;
        KindBox.SelectedIndex = entry?.Kind == RegistryValueKind.ExpandString ? 1 : 0;
        UpdateHints();
        if (isNew)
            NameBox.Focus();
    }

    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateHints();

    private void UpdateHints()
    {
        var name = NameBox.Text.Trim();
        var isPath = EnvironmentVariableRules.IsPath(name);
        var isCritical = EnvironmentVariableRules.IsCritical(name);
        ValueLabel.Text = isPath ? "值（每行一个路径）" : "值";
        ValueHint.Text = isPath
            ? "每行一个路径，保存为分号分隔；保留 %VAR%"
            : "支持 %VAR%，保存时不会自动展开";
        NameHint.Text = EnvironmentVariableRules.IsValidName(name) || name.Length == 0
            ? "只能使用字母、数字、下划线，且不能以数字开头。"
            : "变量名格式不正确。";
        CriticalPanel.Visibility = isCritical ? Visibility.Visible : Visibility.Collapsed;
        CriticalText.Text = isCritical
            ? $"{name} 是关键环境变量。删除或置空可能影响系统/程序启动；请确认后再修改。"
            : string.Empty;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (!EnvironmentVariableRules.IsValidName(name))
        {
            StatusText.Text = "变量名不合法：只能使用字母、数字、下划线，且不能以数字开头。";
            return;
        }

        var kind = KindBox.SelectedIndex == 1
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String;
        if (EnvironmentVariableRules.IsPath(name))
            kind = RegistryValueKind.ExpandString;
        var value = ValueBox.Text;
        if (EnvironmentVariableRules.IsPath(name))
        {
            value = string.Join(";", value
                .Split(new[] { "\r\n", "\n", "\r", ";" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        }

        var entry = new EnvironmentVariableEntry
        {
            Scope = _scope,
            Name = name,
            Value = value,
            Kind = kind
        };
        var error = EnvironmentVariableRules.ValidateChange(
            new EnvironmentVariableChange(_scope, name, value, kind, Delete: false));
        if (error is not null)
        {
            StatusText.Text = error;
            return;
        }

        Result = entry;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
