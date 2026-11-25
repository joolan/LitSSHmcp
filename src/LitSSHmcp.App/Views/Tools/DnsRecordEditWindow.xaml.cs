using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.Core.Services.DnsServer;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class DnsRecordEditWindow : FluentWindow
{
    public DnsRecordEditWindow()
    {
        InitializeComponent();
    }

    public DnsLocalRecord? Result { get; private set; }

    public void Configure(DnsLocalRecord? record)
    {
        Title = record is null ? "新增本地记录" : "编辑本地记录";
        NameBox.Text = record?.Name ?? string.Empty;
        ValueBox.Text = record?.Value ?? string.Empty;
        TtlBox.Text = (record?.Ttl ?? 300).ToString();
        EnabledCheck.IsChecked = record?.Enabled ?? true;
        SelectType(record?.Type ?? "A");
        UpdateHint();
        if (record is null)
            NameBox.Focus();
    }

    private void SelectType(string? type)
    {
        foreach (var item in TypeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, type, StringComparison.OrdinalIgnoreCase))
            {
                TypeBox.SelectedItem = item;
                return;
            }
        }
        TypeBox.SelectedIndex = 0;
    }

    private string SelectedType() =>
        (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "A";

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (HintText is null)
            return;

        HintText.Text = SelectedType() switch
        {
            "A" => "值填写 IPv4 地址，例如 192.168.1.20。",
            "AAAA" => "值填写 IPv6 地址，例如 2001:db8::20。",
            "CNAME" => "值填写目标域名，例如 nas.lab；客户端会继续解析该目标。",
            "PTR" => "值填写目标域名；名称通常为反向域名，例如 20.1.168.192.in-addr.arpa。",
            _ => string.Empty
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TtlBox.Text.Trim(), out var ttl) || ttl is < 0 or > 86400)
        {
            StatusText.Text = "TTL 应为 0~86400 的整数。";
            return;
        }

        var record = new DnsLocalRecord
        {
            Name = NameBox.Text.Trim(),
            Type = SelectedType(),
            Value = ValueBox.Text.Trim(),
            Ttl = ttl,
            Enabled = EnabledCheck.IsChecked == true
        };
        record.Normalize();

        var error = record.Validate();
        if (error is not null)
        {
            StatusText.Text = error;
            return;
        }

        Result = record;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
