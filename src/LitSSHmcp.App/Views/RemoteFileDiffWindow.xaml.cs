using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services.RemoteFiles;

namespace LitSSHmcp.App.Views;

public partial class RemoteFileDiffWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _syncingScroll;
    private ScrollViewer? _leftScroll;
    private ScrollViewer? _rightScroll;

    internal RemoteFileDiffWindow(
        string title, string leftLabel, string rightLabel, string leftText, string rightText)
    {
        InitializeComponent();
        var diff = RemoteFileDiff.Build(leftText, rightText);
        Bar.Title = title;
        Title = title;
        LeftLabel.Text = leftLabel;
        RightLabel.Text = rightLabel;
        SummaryText.Text = $"差异：+{diff.Added} / -{diff.Removed}";
        LeftEditor.Text = string.Join("\n", diff.Left.Select(line => line.Text));
        RightEditor.Text = string.Join("\n", diff.Right.Select(line => line.Text));
        LeftEditor.TextArea.TextView.LineTransformers.Add(new DiffLineTransformer(diff.Left, left: true));
        RightEditor.TextArea.TextView.LineTransformers.Add(new DiffLineTransformer(diff.Right, left: false));
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public static void Show(
        Window? owner, string title, string leftLabel, string rightLabel, string leftText, string rightText)
    {
        var window = new RemoteFileDiffWindow(title, leftLabel, rightLabel, leftText, rightText)
        {
            Owner = owner
        };
        window.ShowDialog();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _leftScroll = FindVisualChild<ScrollViewer>(LeftEditor);
        _rightScroll = FindVisualChild<ScrollViewer>(RightEditor);
        if (_leftScroll is not null)
            _leftScroll.ScrollChanged += OnLeftScroll;
        if (_rightScroll is not null)
            _rightScroll.ScrollChanged += OnRightScroll;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_leftScroll is not null)
            _leftScroll.ScrollChanged -= OnLeftScroll;
        if (_rightScroll is not null)
            _rightScroll.ScrollChanged -= OnRightScroll;
    }

    private void OnLeftScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || Math.Abs(e.VerticalChange) < 0.01)
            return;
        _syncingScroll = true;
        try { _rightScroll?.ScrollToVerticalOffset(e.VerticalOffset); }
        finally { _syncingScroll = false; }
    }

    private void OnRightScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || Math.Abs(e.VerticalChange) < 0.01)
            return;
        _syncingScroll = true;
        try { _leftScroll?.ScrollToVerticalOffset(e.VerticalOffset); }
        finally { _syncingScroll = false; }
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var found = FindVisualChild<T>(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
