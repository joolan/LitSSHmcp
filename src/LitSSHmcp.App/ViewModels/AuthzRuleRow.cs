using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 「设置 → 安全设置 → Web 授权矩阵」的一条可编辑规则行：
/// <see cref="UserText"/> 对应 <c>security.authorization.users[].user</c>，
/// <see cref="TargetsText"/>（空格/逗号分隔）对应 <c>targets</c>，
/// <see cref="LevelIndex"/>（0=read，1=exec）对应 <c>level</c>。
/// </summary>
public sealed class AuthzRuleRow : INotifyPropertyChanged
{
    private string _userText = string.Empty;
    private string _targetsText = string.Empty;
    private int _levelIndex;

    public string UserText { get => _userText; set => Set(ref _userText, value); }
    public string TargetsText { get => _targetsText; set => Set(ref _targetsText, value); }
    public int LevelIndex { get => _levelIndex; set => Set(ref _levelIndex, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
