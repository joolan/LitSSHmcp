using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>工具审批策略窗口：逐工具设置 默认 / 允许 / 拒绝（会话级，Agent 层）。</summary>
public partial class ToolPolicyWindow : FluentWindow
{
    public ToolPolicyWindow(IReadOnlyList<ToolPolicyItem> tools, IReadOnlyDictionary<string, AgentToolPolicy> current)
    {
        InitializeComponent();

        foreach (var tool in tools)
            Rows.Add(new ToolPolicyRow(tool, current.TryGetValue(tool.Name, out var p) ? p : AgentToolPolicy.Default));

        Grid.ItemsSource = Rows;
        RefreshStatus();
    }

    private readonly ObservableCollection<ToolPolicyRow> Rows = new();

    /// <summary>确定后返回整份策略（含默认项；默认项在应用时会被忽略/落库时过滤）。</summary>
    public IReadOnlyDictionary<string, AgentToolPolicy> Result { get; private set; } =
        new Dictionary<string, AgentToolPolicy>();

    private void RefreshStatus()
    {
        var denied = Rows.Count(r => r.Policy == AgentToolPolicy.Deny);
        var allowed = Rows.Count(r => r.Policy == AgentToolPolicy.Allow);
        StatusText.Text = $"拒绝 {denied} · 允许 {allowed} · 默认 {Rows.Count - denied - allowed}";
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
            row.SelectedIndex = 0;
        RefreshStatus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = Rows.ToDictionary(r => r.Name, r => r.Policy, StringComparer.OrdinalIgnoreCase);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>一行工具：名称/域/类型 + 策略下拉（SelectedIndex 0=默认 1=允许 2=拒绝）。</summary>
public sealed class ToolPolicyRow : INotifyPropertyChanged
{
    public ToolPolicyRow(ToolPolicyItem tool, AgentToolPolicy policy)
    {
        Name = tool.Name;
        GroupText = ToolGroups.Display(tool.Group);
        KindText = tool.Destructive ? "写(敏感)" : tool.ReadOnly ? "只读" : "写";
        _policy = policy;
    }

    public string Name { get; }
    public string GroupText { get; set; } = string.Empty;
    public string KindText { get; set; } = string.Empty;

    private AgentToolPolicy _policy;
    public AgentToolPolicy Policy => _policy;

    public int SelectedIndex
    {
        get => _policy switch { AgentToolPolicy.Allow => 1, AgentToolPolicy.Deny => 2, _ => 0 };
        set
        {
            var next = value switch { 1 => AgentToolPolicy.Allow, 2 => AgentToolPolicy.Deny, _ => AgentToolPolicy.Default };
            if (_policy == next) return;
            _policy = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Policy)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
