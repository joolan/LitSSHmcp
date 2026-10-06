using System.Windows;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>远程文件/目录属性与权限编辑对话框（参考宝塔：rwx 勾选 + 权限码 + 属主/属组 + 递归）。</summary>
public partial class RemoteFilePropertiesWindow : FluentWindow
{
    private readonly RemoteFileBrowserViewModel _vm;
    private readonly RemoteFileItem _item;
    private readonly string _ownerInitial;
    private readonly string _groupInitial;
    private bool _syncing;

    public RemoteFilePropertiesWindow(RemoteFileBrowserViewModel vm, RemoteFileItem item)
    {
        InitializeComponent();
        _vm = vm;
        _item = item;

        NameText.Text = item.Name;
        PathText.Text = item.FullName;
        TypeText.Text = item.IsDirectory ? "目录" : item.IsSymbolicLink ? "符号链接" : "文件";
        SizeText.Text = item.IsDirectory ? "—" : $"{item.SizeText}（{item.Size} 字节）";
        ModifiedText.Text = item.LastModified == default ? "—" : item.LastModified.ToString("yyyy-MM-dd HH:mm:ss");

        (_ownerInitial, _groupInitial) = SplitOwner(item.Owner);

        var octal = TryOctalFromSymbolic(item.Permissions);
        ModeBox.Text = octal ?? (item.IsDirectory ? "755" : "644");
        SetFromOctal(ModeBox.Text);

        Loaded += async (_, _) => await LoadAccountsAsync();
    }

    private async Task LoadAccountsAsync()
    {
        try
        {
            var (users, groups) = await _vm.LoadAccountsAsync();
            OwnerBox.ItemsSource = WithCurrent(users, _ownerInitial);
            GroupBox.ItemsSource = WithCurrent(groups, _groupInitial);
            OwnerBox.SelectedItem = string.IsNullOrEmpty(_ownerInitial) ? null : _ownerInitial;
            GroupBox.SelectedItem = string.IsNullOrEmpty(_groupInitial) ? null : _groupInitial;
        }
        catch
        {
            // 账号列表加载失败：下拉为空
        }
    }

    private static List<string> WithCurrent(List<string> items, string current)
    {
        var list = new List<string>(items);
        if (!string.IsNullOrEmpty(current) && !list.Contains(current, StringComparer.Ordinal))
            list.Insert(0, current);
        return list;
    }

    private void OnPermChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        _syncing = true;
        ModeBox.Text = OctalFromCheckboxes();
        _syncing = false;
    }

    private void OnModeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing)
            return;
        var text = ModeBox.Text.Trim();
        if (text.Length is 3 or 4 && text.All(c => c >= '0' && c <= '7'))
        {
            _syncing = true;
            SetFromOctal(text);
            _syncing = false;
        }
    }

    private string OctalFromCheckboxes()
    {
        int Bits(bool r, bool w, bool x) => (r ? 4 : 0) + (w ? 2 : 0) + (x ? 1 : 0);
        var parts = new[]
        {
            Bits(OwnerRead.IsChecked == true, OwnerWrite.IsChecked == true, OwnerExec.IsChecked == true),
            Bits(GroupRead.IsChecked == true, GroupWrite.IsChecked == true, GroupExec.IsChecked == true),
            Bits(OthersRead.IsChecked == true, OthersWrite.IsChecked == true, OthersExec.IsChecked == true)
        };
        return string.Concat(parts);
    }

    private void SetFromOctal(string octal)
    {
        var o = octal.Trim();
        if (o.Length == 4)
            o = o[1..]; // 忽略 setuid/setgid/sticky 位（展示用）
        if (o.Length != 3)
            return;

        void Apply(char digit, System.Windows.Controls.CheckBox r, System.Windows.Controls.CheckBox w, System.Windows.Controls.CheckBox x)
        {
            var v = digit - '0';
            r.IsChecked = (v & 4) != 0;
            w.IsChecked = (v & 2) != 0;
            x.IsChecked = (v & 1) != 0;
        }

        _syncing = true;
        Apply(o[0], OwnerRead, OwnerWrite, OwnerExec);
        Apply(o[1], GroupRead, GroupWrite, GroupExec);
        Apply(o[2], OthersRead, OthersWrite, OthersExec);
        ModeBox.Text = o;
        _syncing = false;
    }

    private static string? TryOctalFromSymbolic(string symbolic)
    {
        if (string.IsNullOrWhiteSpace(symbolic) || symbolic.Length < 10)
            return null;
        var chars = symbolic[1..10];
        int Bits(int offset) => (chars[offset] == 'r' ? 4 : 0) + (chars[offset + 1] == 'w' ? 2 : 0) + (chars[offset + 2] is 'x' or 's' or 't' ? 1 : 0);
        return $"{Bits(0)}{Bits(3)}{Bits(6)}";
    }

    private static (string Owner, string Group) SplitOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return (string.Empty, string.Empty);
        var slash = owner.IndexOf('/');
        return slash >= 0 ? (owner[..slash], owner[(slash + 1)..]) : (owner, string.Empty);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_item.FullName); } catch { /* ignore */ }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var owner = OwnerBox.SelectedItem as string;
        var group = GroupBox.SelectedItem as string;
        var ok = await _vm.ApplyPermissionsAsync(_item, ModeBox.Text, owner, group, RecursiveBox.IsChecked == true);
        if (!ok)
        {
            System.Windows.MessageBox.Show(_vm.StatusMessage, "修改权限失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
